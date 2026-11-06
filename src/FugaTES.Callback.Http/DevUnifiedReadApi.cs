using System.Net;
using System.Security.Cryptography;
using System.Text;
using FugaTES.Persistencia.Consultas.UnifiedDev;
using FugaTES.Persistencia.MotorIntegracao;
using Npgsql;

namespace FugaTES.Callback.Http;

// Explicit DEV-only host mode. It does not configure inbound, callbacks, workers, Guardian or SAP.
public static class DevUnifiedReadApi
{
    public static bool Selected(string[] args)
    {
        if(!args.Any(a=>a.StartsWith("--dev-unified",StringComparison.Ordinal)))return false;
        if(args.Length!=1||args[0]!="--dev-unified-read-only")throw new InvalidOperationException("UNIFIED_DEV_READ_MODE_ARGUMENTS_REJECTED");
        return true;
    }
    public static async Task RunAsync()
    {
        if(!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("FUGATES_DEV_READ_APPROVAL")!="FUGATES-UNIFIED-POSTGRESQL-DEV")
            throw new InvalidOperationException("UNIFIED_DEV_READ_MODE_NOT_AUTHORIZED");
        var keyFile=Reference("FUGATES_DEV_READ_KEY_FILE");
        if(new FileInfo(keyFile).Length!=64 || (File.GetAttributes(keyFile)&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0 ||
            (File.GetUnixFileMode(keyFile)&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute))!=0)
            throw new InvalidOperationException("UNIFIED_DEV_PRIVATE_KEY_REFERENCE_REJECTED");
        var key=File.ReadAllText(keyFile).Trim();
        var connection=new NpgsqlConnectionStringBuilder{Host="127.0.0.1",Port=5433,Database="fugates_dev",SearchPath="fugates",Username=RoleDeLeitura(),
            Passfile=Reference("R165AL_APPROVED_PASSFILE"),IncludeErrorDetail=false,ApplicationName="FugaTES.UnifiedDev.ReadOnly",Options="-c default_transaction_read_only=on"};
        await using var source=NpgsqlDataSourceFactory.CriarAcm(connection.ConnectionString);
        // A role configurada atravessa o store: sem isto o guard de identidade compara current_user
        // com a role ACM compartilhada e recusa a identidade GET-only na partida.
        var store=new DevUnifiedReadStore(source,RoleDeLeitura());await store.IdentityAsync(CancellationToken.None);
        var builder=CreateReadOnlyHostBuilder();
        await using var app=builder.Build();
        var mmDirectory=Environment.GetEnvironmentVariable("FUGATES_DEV_MM_SNAPSHOT_DIRECTORY");
        if(mmDirectory is not null&&mmDirectory!="/var/lib/fugates-mm")throw new InvalidOperationException("MM_VM_OBSERVATION_DIRECTORY_REJECTED");
        Map(app,store,key,mmDirectory is null?null:new MmSnapshotReadSource(mmDirectory));await app.RunAsync();
    }
    public static WebApplicationBuilder CreateReadOnlyHostBuilder()
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=[]});
        builder.Configuration.Sources.Clear();builder.Configuration.AddInMemoryCollection();builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:18762");return builder;
    }
    // R164: identidade de banco desta API. O padrao continua sendo a role operacional
    // compartilhada, para que nada mude sem acao explicita do operador. A unica alternativa
    // aceita e a identidade GET-only dedicada; qualquer outro valor e recusado, de modo que
    // esta variavel nunca possa apontar a API de leitura para uma identidade com escrita.
    public const string RoleEnvironmentVariable = "FUGATES_DEV_READ_ROLE";
    public static string RoleDeLeitura()
    {
        var escolhida = Environment.GetEnvironmentVariable(RoleEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(escolhida)) return NpgsqlDataSourceFactory.AcmApplicationRole;
        if (escolhida != NpgsqlDataSourceFactory.DevReadOnlyApplicationRole)
            throw new InvalidOperationException("UNIFIED_DEV_READ_ROLE_NOT_AUTHORIZED");
        return escolhida;
    }

    static string Reference(string name)
    {
        var value=Environment.GetEnvironmentVariable(name);
        if(string.IsNullOrWhiteSpace(value)||!Path.IsPathFullyQualified(value)||value.Any(char.IsControl)||!File.Exists(value))
            throw new InvalidOperationException("UNIFIED_DEV_EXISTING_PROVIDER_REFERENCE_REQUIRED");
        return value;
    }
    public static void Map(WebApplication app,IDevUnifiedReadStore store,string key,IMmSnapshotReadSource? mm=null)
    {
        if(key.Length!=64||!key.All(Uri.IsHexDigit))throw new InvalidOperationException("UNIFIED_DEV_READ_KEY_INVALID");
        var expected=Encoding.UTF8.GetBytes(key);
        app.Use(async(context,next)=>
        {
            var peer=context.Connection.RemoteIpAddress;
            if(peer is null||!IPAddress.IsLoopback(peer)){context.Response.StatusCode=403;return;}
            var values=context.Request.Headers["X-R165AL-Read-Key"];
            if(values.Count!=1||values[0]?.Length!=64||!CryptographicOperations.FixedTimeEquals(expected,Encoding.UTF8.GetBytes(values[0]!)))
            {context.Response.StatusCode=401;return;}
            context.Response.Headers.CacheControl="no-store";
            try{await next(context);}
            catch(OperationCanceledException) when(context.RequestAborted.IsCancellationRequested){}
            catch(PostgresException error) when(error.SqlState=="42501")
            {if(!context.Response.HasStarted){context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{code="UNIFIED_DEV_READ_PRIVILEGE_REQUIRED"});}}
            catch{if(!context.Response.HasStarted){context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{code="UNIFIED_DEV_READ_UNAVAILABLE"});}}
        });
        app.MapGet("/dev/read/mm",async(CancellationToken ct)=>
        {
            var identity=await store.IdentityAsync(ct);identity.Validate();
            if(mm is null)return Results.Json(new{scope="mm",code="MM_VM_OBSERVATION_NOT_CONFIGURED",identity},statusCode:503);
            try
            {
                var value=await mm.ReadAsync(ct);
                return Results.Json(new DevReadEnvelope<FugaTES.Aplicacao.Modelos.Mm.MmRemoteReadback>(identity,value));
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception error)
            {
                // No exception text, document, path or credential crosses the diagnostic boundary.
                var code=error is System.Text.Json.JsonException?"MM_VM_OBSERVATION_DOCUMENT_REJECTED":
                    error is InvalidOperationException {Message:"MM_VM_OBSERVATION_SCOPE_REJECTED"}?"MM_VM_OBSERVATION_SCOPE_REJECTED":
                    error is InvalidOperationException {Message:"MM_VM_OBSERVATION_DOCUMENT_REJECTED" or "MM_CACHE_POLICY_INVALID" or "MM_CACHE_IDENTITY_INVALID" or "MM_CACHE_SCOPE_INVALID" or "MM_GUARDIAN_CACHE_INVALID" or "MM_CACHE_INVALID" or "MM_CACHE_SIZE_LIMIT"}?"MM_VM_OBSERVATION_DOCUMENT_REJECTED":
                    "MM_VM_OBSERVATION_UNAVAILABLE";
                return Results.Json(new{scope="mm",code,identity},statusCode:503);
            }
        });
        app.MapGet("/dev/read/identity",async(CancellationToken ct)=>await store.IdentityAsync(ct));
        app.MapGet("/dev/read/{resource}",async(string resource,string? selector,CancellationToken ct)=>
        {
            if(resource is not("contracts" or "monitor" or "detail" or "protocol" or "masterdata" or "toledo" or "toledo-json" or "toledo-sap-json"))return Results.NotFound();
            if(selector?.Length>200||selector?.Any(char.IsControl)==true)return Results.BadRequest(new{code="UNIFIED_DEV_FILTER_INVALID"});
            try{return Results.Json(await store.ReadAsync(resource,selector,ct));}
            catch(KeyNotFoundException){return Results.NotFound(new{code="UNIFIED_DEV_RECORD_NOT_FOUND"});}
            catch(ArgumentException){return Results.BadRequest(new{code="UNIFIED_DEV_FILTER_INVALID"});}
        });
    }
}
