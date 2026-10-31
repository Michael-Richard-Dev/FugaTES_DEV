using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Persistencia.Transportadoras;
using Npgsql;

namespace FugaTES.HostIntegracao;

public sealed record CarrierLinuxDatabaseProfile(string Host,int Port,string Database,string Username,string Schema);
public sealed record CarrierLinuxRemoteProfile(string Environment,string BaseUrl,string ApprovalReference);
public sealed record CarrierLinuxProfile(string Environment,string HostVm,string ServiceAccount,bool IntegrationWorkerEnabled,int ReadinessSeconds,CarrierLinuxDatabaseProfile Database,CarrierLinuxRemoteProfile Guardian,CarrierLinuxRemoteProfile Sap)
{
    public static CarrierLinuxProfile Load(string path)
    {
        if(!File.Exists(path))throw new CarrierServiceStartupException("CARRIER_LINUX_PROFILE_MISSING");
        try{return JsonSerializer.Deserialize<CarrierLinuxProfile>(File.ReadAllText(path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new CarrierServiceStartupException("CARRIER_LINUX_PROFILE_INVALID");}
        catch(Exception ex) when(ex is JsonException or IOException or UnauthorizedAccessException){throw new CarrierServiceStartupException("CARRIER_LINUX_PROFILE_INVALID");}
    }
    public void Validar(string account)
    {
        if(Environment!="HML"||HostVm!="192.168.17.40")throw new CarrierServiceStartupException("CARRIER_LINUX_ENVIRONMENT_INVALID");
        if(IntegrationWorkerEnabled)throw new CarrierServiceStartupException("CARRIER_INC005_FORBIDDEN");
        if(ServiceAccount!="fugates-carrier"||account!=ServiceAccount)throw new CarrierServiceStartupException("CARRIER_LINUX_ACCOUNT_MISMATCH");
        if(Database is null||Database.Host!="127.0.0.1"||Database.Port!=5432||Database.Database!="fugates_dev"||Database.Username!="fugates_dev_app"||Database.Schema!="fugates")throw new CarrierServiceStartupException("CARRIER_LINUX_DATABASE_PROFILE_INVALID");
        if(ReadinessSeconds is <1 or >60)throw new CarrierServiceStartupException("CARRIER_LINUX_READINESS_INVALID");
        ValidateRemote(Guardian,"GUARDIAN");ValidateRemote(Sap,"SAP");
    }
    static void ValidateRemote(CarrierLinuxRemoteProfile? remote,string system)
    {
        if(remote is null||remote.Environment!="HML"||string.IsNullOrWhiteSpace(remote.ApprovalReference)
            ||!Uri.TryCreate(remote.BaseUrl,UriKind.Absolute,out var url)||url.Scheme!="https"||url.UserInfo.Length!=0||url.Query.Length!=0||url.Fragment.Length!=0)
            throw new CarrierServiceStartupException("CARRIER_LINUX_"+system+"_HML_PROFILE_INVALID");
    }
    public string ConnectionString(string password)
    {
        Validar(ServiceAccount);
        if(string.IsNullOrEmpty(password))throw new CarrierServiceStartupException("CARRIER_LINUX_CREDENTIAL_MISSING");
        return new NpgsqlConnectionStringBuilder{Host=Database.Host,Port=Database.Port,Database=Database.Database,Username=Database.Username,Password=password,
            Timeout=3,CommandTimeout=10,IncludeErrorDetail=false,ApplicationName="FugaTES.CarrierSync.HML.Linux"}.ConnectionString;
    }
}

public static class CarrierLinuxDatabaseIdentity
{
    public static void Validate(string database,string role,bool schema,bool elevated,bool ddl)
    {
        if(database!="fugates_dev"||role!="fugates_dev_app"||!schema)throw new CarrierServiceStartupException("CARRIER_LINUX_DATABASE_IDENTITY_INVALID");
        if(elevated||ddl)throw new CarrierServiceStartupException("CARRIER_LINUX_APP_ROLE_DDL_FORBIDDEN");
    }
}

// Startup only: authenticated SELECTs, no migration, checkpoint update or HTTP call.
public sealed class CarrierLinuxRuntimeBootstrap:IDisposable
{
    public CarrierLinuxProfile Profile{get;}
    public CarrierSystemdCredentials Credentials{get;}
    public NpgsqlDataSource Source{get;}
    public ICarrierSyncStore Store{get;}
    private CarrierLinuxRuntimeBootstrap(CarrierLinuxProfile profile,CarrierSystemdCredentials credentials,string connection)
    {Profile=profile;Credentials=credentials;Source=NpgsqlDataSource.Create(connection);Store=new CarrierSyncPostgreSqlStore(Source,connection);}
    public static async Task<CarrierLinuxRuntimeBootstrap> StartAsync(string profilePath,bool workerEnabled,CancellationToken ct)
    {
        if(!OperatingSystem.IsLinux())throw new CarrierServiceStartupException("CARRIER_LINUX_REQUIRED");
        CarrierLinuxRuntimeBootstrap? runtime=null;
        try
        {
            var profile=CarrierLinuxProfile.Load(profilePath);profile.Validar(System.Environment.UserName);
            if(GetEffectiveUid()==0)throw new CarrierServiceStartupException("CARRIER_LINUX_ROOT_FORBIDDEN");
            if(!NetworkInterface.GetAllNetworkInterfaces().SelectMany(n=>n.GetIPProperties().UnicastAddresses).Any(a=>a.Address.ToString()==profile.HostVm))throw new CarrierServiceStartupException("CARRIER_LINUX_VM_MISMATCH");
            var credentials=new CarrierSystemdCredentials(System.Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY")??"");
            credentials.ValidateRequired();
            runtime=new(profile,credentials,profile.ConnectionString(credentials.Read("carrier-hml-postgres-password")));
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(profile.ReadinessSeconds));
            await runtime.ValidateDatabaseAsync(workerEnabled,timeout.Token);return runtime;
        }
        catch(CarrierServiceStartupException){runtime?.Dispose();throw;}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){runtime?.Dispose();throw;}
        catch{runtime?.Dispose();throw new CarrierServiceStartupException("CARRIER_LINUX_PREFLIGHT_FAILED");}
    }
    private async Task ValidateDatabaseAsync(bool requireCarrierSchema,CancellationToken ct)
    {
        await using var connection=await Source.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(ct);
        await using(var readOnly=new NpgsqlCommand("set transaction read only",connection,transaction))await readOnly.ExecuteNonQueryAsync(ct);
        await using(var command=new NpgsqlCommand("""
            with reachable_roles as (select oid,rolsuper,rolcreatedb,rolcreaterole,rolreplication,rolbypassrls from pg_roles where pg_has_role(current_user,oid,'MEMBER')),
                 app_namespaces as (select oid,nspowner from pg_namespace where left(nspname,3)<>'pg_' and nspname<>'information_schema')
            select current_database(),current_user,to_regnamespace('fugates') is not null,
              exists(select 1 from reachable_roles r where r.rolsuper or r.rolcreatedb or r.rolcreaterole or r.rolreplication or r.rolbypassrls),
              exists(select 1 from reachable_roles r where has_database_privilege(r.oid,current_database(),'CREATE') or has_database_privilege(r.oid,current_database(),'TEMP'))
              or exists(select 1 from app_namespaces n cross join reachable_roles r where n.nspowner=r.oid or has_schema_privilege(r.oid,n.oid,'CREATE'))
              or exists(select 1 from pg_class c join app_namespaces n on n.oid=c.relnamespace join reachable_roles r on r.oid=c.relowner)
              or exists(select 1 from pg_proc p join app_namespaces n on n.oid=p.pronamespace join reachable_roles r on r.oid=p.proowner)
              or exists(select 1 from pg_type t join app_namespaces n on n.oid=t.typnamespace join reachable_roles r on r.oid=t.typowner)
            """,connection,transaction))
        {
            await using var reader=await command.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct))throw new CarrierServiceStartupException("CARRIER_LINUX_DATABASE_IDENTITY_INVALID");
            CarrierLinuxDatabaseIdentity.Validate(reader.GetString(0),reader.GetString(1),reader.GetBoolean(2),reader.GetBoolean(3),reader.GetBoolean(4));
        }
        if(requireCarrierSchema)
        {
            await using var command=new NpgsqlCommand("""
                select bool_and(to_regclass('fugates.'||name) is not null and
                  has_table_privilege(current_user,'fugates.'||name,'SELECT') and has_table_privilege(current_user,'fugates.'||name,'INSERT') and has_table_privilege(current_user,'fugates.'||name,'UPDATE') and
                  not has_table_privilege(current_user,'fugates.'||name,'DELETE,TRUNCATE,TRIGGER,REFERENCES'))
                from unnest(array['carrier_sync_cycle','carrier_sync_state','carrier_sync_attempt','carrier_sync_step','carrier_sync_checkpoint','carrier_sync_request']) name
                """,connection,transaction);
            if(await command.ExecuteScalarAsync(ct) is not true)throw new CarrierServiceStartupException("CARRIER_LINUX_SCHEMA_PENDING_GAIA");
        }
        await transaction.CommitAsync(ct);
    }
    public void Dispose()=>Source.Dispose();
    [DllImport("libc",EntryPoint="geteuid")]private static extern uint GetEffectiveUid();
}