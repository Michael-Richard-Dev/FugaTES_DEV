using FugaTES.Aplicacao.Configuracao;
using System.Security.Cryptography;
using System.Text.Json;
using FugaTES.Persistencia.MotorIntegracao;
using Npgsql;

namespace FugaTES.HostIntegracao;
public static class AcmProviderReadOnlyEntrypoint
{
 public const string Argument="--acm-provider-readonly=R161L";
 public static string? ConfigurationBlock(IConfiguration config)
 {
  var approval=config["AcmR165R:ApprovalReference"];
  if(config["AcmR165R:CredentialSecurityCleared"]!="true" || string.IsNullOrWhiteSpace(approval) || approval.Any(char.IsControl))
   return "R165R_CREDENTIAL_SECURITY_APPROVAL_REQUIRED";
  // Fake is the existing inert external mode, not a simulated database provider.
  // R57 selects the same physically guarded ACM factory used by the business entrypoint.
  if(config["HostIntegracao:Mode"]=="CarrierSync"||config["HostIntegracao:PersistenceMode"]!="PostgreSql"||
     config["HostIntegracao:ExternalMode"]!="Fake"||config["AcmR57:Enabled"]!="true")return "R161L_INERT_PROFILE_REQUIRED";
  foreach(var key in new[]{"FUGATES_INTEGRATION_WORKER_ENABLED","INC005_WORKER_ENABLED","FUGATES_CARRIER_SYNC_ENABLED","CARRIER_WORKER_ENABLED","Sap:Enabled"})
   if(!bool.TryParse(config[key],out var enabled)||enabled)return "R161L_WORKERS_OR_SAP_NOT_DISABLED";
  if(!bool.TryParse(config["AcmR104:ExecutionAuthorized"],out var authorized)||authorized)return "R161L_BUSINESS_EXECUTION_FORBIDDEN";
  try
  {
   var ambiente=AcmEnvironmentBinding.Banco(config);
   var c=new NpgsqlConnectionStringBuilder(config.GetConnectionString(NpgsqlDataSourceFactory.ConnectionStringName));
   if(c.Host!="127.0.0.1"||c.Port!=5433||AmbientesBanco.Por(c.Database,c.Username,ambiente)!=ambiente||c.Username!=ambiente.PapelAcm||c.SearchPath!="fugates"||c.Enlist||
      c.Options!="-c default_transaction_read_only=on")return "R161L_READONLY_ACM_CONNECTION_REQUIRED";
   if(!NpgsqlDataSourceFactory.HasExplicitAcmPassfile(c) || !string.IsNullOrEmpty(config["PGPASSWORD"]) ||
      (config["PGPASSFILE"]?.Contains(".fugates-r116-dev",StringComparison.OrdinalIgnoreCase)??false))
    return "R165R_EXPLICIT_ACM_PASSFILE_REQUIRED";
  }
  catch{return "R161L_READONLY_ACM_CONNECTION_REQUIRED";}
  return null;
 }
 public static void Configure(HostApplicationBuilder builder)
 {
  var block=ConfigurationBlock(builder.Configuration);
  if(block is not null)throw new InvalidOperationException(block);
  HostIntegracaoComposition.Configurar(builder);
  AcmR104OneShotEntrypoint.AddUnitaryServices(builder.Services,new(){ExecutionAuthorized=false});
  if(builder.Services.Any(x=>x.ServiceType==typeof(IHostedService)))throw new InvalidOperationException("R161L_HOSTED_SERVICE_FORBIDDEN");
 }
 public static async Task<int> RunAsync(string[] args,HostApplicationBuilder builder,CancellationToken ct=default)
 {
  if(args.Length!=1||args[0]!=Argument){Console.WriteLine("R161L_ARGUMENT_SCOPE_INVALID");return 64;}
  try
  {
   builder.Logging.ClearProviders();
   var block=ConfigurationBlock(builder.Configuration);
   if(block is not null){Console.WriteLine(block);return 78;}
   Configure(builder);
   var ambiente=AcmEnvironmentBinding.Banco(builder.Configuration);
   // Never Start/Run the host; never resolve or invoke gateways, workers or the unitary invoker.
   using var host=builder.Build();using var scope=host.Services.CreateScope();
   var source=scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
   var repository=scope.ServiceProvider.GetRequiredService<RepositorioMotorIntegracaoPostgreSql>();
   // This is the production repository, through the datasource supplied by the real host DI.
   if(!await repository.EstaDisponivelAsync(ct))throw new InvalidOperationException("R161L_REPOSITORY_UNAVAILABLE");
   await using var connection=await source.OpenConnectionAsync(ct);
   await using var transaction=await connection.BeginTransactionAsync(ct);
   object? evidence=null;
   try
   {
    await using(var readOnly=new NpgsqlCommand("set transaction read only",connection,transaction){CommandTimeout=5})
     await readOnly.ExecuteNonQueryAsync(ct);
    await using var command=new NpgsqlCommand("""
     select current_setting('cluster_name'),current_setting('port')::int,inet_server_port(),
            current_database(),current_schema(),current_user,session_user,current_setting('transaction_read_only'),
            pg_is_in_recovery(),to_regclass('fugates.correlacao_guardian_sap') is not null
     """,connection,transaction){CommandTimeout=5};
    await using var row=await command.ExecuteReaderAsync(ct);
    if(!await row.ReadAsync(ct)||row.GetString(0)!="18/fugatesacmdev"||row.GetInt32(1)!=5433||row.GetInt32(2)!=5433||
       row.GetString(3)!=ambiente.Banco||row.GetString(4)!="fugates"||row.GetString(5)!=ambiente.PapelAcm||
       row.GetString(6)!=ambiente.PapelAcm||row.GetString(7)!="on"||row.GetBoolean(8))
     throw new InvalidOperationException("R161L_EFFECTIVE_IDENTITY_MISMATCH");
    static object AssemblyProof(Type type)
    {
     var assembly=type.Assembly;var path=assembly.Location;
     return new{name=assembly.GetName().Name,path,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))};
    }
    evidence=new{
     gate="R165R",binding="PASS",credential_security_cleared=true,process_id=Environment.ProcessId,cluster=row.GetString(0),physical_port=row.GetInt32(1),
     database=row.GetString(3),schema=row.GetString(4),role=row.GetString(5),session_role=row.GetString(6),transaction_read_only=row.GetString(7),
     base_correlation_table_exists=row.GetBoolean(9),repository=repository.GetType().FullName,
     di_composition="HostIntegracaoComposition.Configurar + AcmR104OneShotEntrypoint.AddUnitaryServices (execution unauthorized)",
     factory="NpgsqlDataSourceFactory.CriarAcm",physical_initializer="AcmPostgreSqlIdentityGuard.VerifyAsync",
     host_assembly=AssemblyProof(typeof(AcmProviderReadOnlyEntrypoint)),factory_assembly=AssemblyProof(typeof(NpgsqlDataSourceFactory)),
     provider_assembly=AssemblyProof(typeof(NpgsqlDataSource)),host_started=false,database_writes=0,guardian_posts=0,sap_posts=0
    };
   }
   finally{await transaction.RollbackAsync(CancellationToken.None);}
   Console.WriteLine(JsonSerializer.Serialize(evidence));
   return 0;
  }
  catch(OperationCanceledException){Console.WriteLine("R161L_CANCELLED_NO_DISPATCH");return 70;}
  catch(PostgresException e){Console.WriteLine("R161L_POSTGRESQL_FAILURE_"+e.SqlState);return 70;}
  catch(NpgsqlException e) when(e.Message.Contains("password",StringComparison.OrdinalIgnoreCase))
  {Console.WriteLine("R161L_APPLICATION_CREDENTIAL_UNAVAILABLE");return 70;}
  catch{Console.WriteLine("R161L_BINDING_UNVERIFIED_NO_DISPATCH");return 70;}
 }
}
