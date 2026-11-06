using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Mm;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
using FugaTES.HostIntegracao.Mm;
using FugaTES.Integracao.Guardian;
using FugaTES.Persistencia.Mm;
using Npgsql;
namespace FugaTES.Callback.Http.ManualRetry;
public static class MmRetryCommandComposition
{
 public static bool Configure(IServiceCollection services,AcmManualRetryCommandProfile commandProfile)
 {
  var policyFile=Environment.GetEnvironmentVariable("FUGATES_MM_RETRY_POLICY_FILE");if(policyFile is null)return false;
  var options=MmRetryPolicyFile.ReadPolicy(policyFile);if(!options.Enabled)return false;
  if(options.OperatorSid!=commandProfile.OperatorSid)throw new InvalidOperationException("MM_RETRY_OPERATOR_BINDING_REJECTED");
  services.AddSingleton<MmRetryCommandRuntime>(_=>new(commandProfile,options));
  services.AddSingleton<IMmRetryStore>(sp=>new MmRetryPostgreSqlStore(sp.GetRequiredService<MmRetryCommandRuntime>().Database));
  services.AddScoped(sp=>new MmRetryService(sp.GetRequiredService<IMmRetryStore>(),async ct=>(await new MmSnapshotReadSource("/var/lib/fugates-mm").ReadAsync(ct)).Snapshot,sp.GetRequiredService<MmRetryCommandRuntime>().Gate,options,TimeProvider.System));
  return true;
 }
}
// A distinct wrapper deliberately does NOT replace the shared ACM NpgsqlDataSource registration.
public sealed class MmRetryCommandRuntime:IAsyncDisposable
{
 public NpgsqlDataSource Database{get;}
 public IMmMasterdataGate Gate{get;}
 readonly HttpClient auth;readonly GuardianEmitterReadTransport emitters;readonly GuardianMaterialReadTransport materials;
 public MmRetryCommandRuntime(AcmManualRetryCommandProfile profile,MmRetryOptions options)
 {
  using var db=JsonDocument.Parse(MmRetryPolicyFile.ReadDatabase());var d=db.RootElement;
  if(d.GetProperty("username").GetString()!=RegistroProtocolosMmPostgreSql.Role||d.GetProperty("port").GetInt32()!=5433||d.GetProperty("database").GetString()!="fugates_dev")throw new InvalidOperationException("MM_RETRY_DATABASE_SCOPE_REJECTED");
  if(!d.TryGetProperty("password",out var password)||password.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(password.GetString())||password.GetString()!.Any(char.IsControl))throw new InvalidOperationException("MM_RETRY_DATABASE_PROVIDER_INVALID");
  var cs=new NpgsqlConnectionStringBuilder{Host="127.0.0.1",Port=5433,Database="fugates_dev",SearchPath="fugates",Username=RegistroProtocolosMmPostgreSql.Role,Password=password.GetString(),IncludeErrorDetail=false,ApplicationName="FugaTES.MM.Retry.Command",Timeout=5,CommandTimeout=8};
  Database=new NpgsqlDataSourceBuilder(cs.ConnectionString).Build();
  var config=new ConfigurationBuilder().AddInMemoryCollection(AcmManualRetryCommandHost.LerCredenciaisGuardian(profile).Select(x=>new KeyValuePair<string,string?>("Guardian:"+x.Nome,x.Valor))).Build();
  var guardian=new GuardianHttpOptions{BaseUrl=new("https://guardian.cloudprix.com.br"),Timeout=TimeSpan.FromSeconds(20)};
  auth=new(new TokenOnly(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false})){Timeout=TimeSpan.FromSeconds(6)};
  var token=new GuardianTokenClient(auth,guardian,new GuardianRuntimeCredenciaisProvider(config));emitters=new();materials=new();
  Gate=new MmMasterdataGate(new GuardianEmitterReadAdapter(emitters.Client,guardian,token,options.GuardianSignatureId),new GuardianMaterialReadAdapter(materials.Client,guardian,token,options.GuardianSignatureId),options.GuardianSignatureId);
 }
 public async ValueTask DisposeAsync(){auth.Dispose();emitters.Dispose();materials.Dispose();await Database.DisposeAsync();}
 sealed class TokenOnly(HttpMessageHandler inner):DelegatingHandler(inner)
 {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){if(r.Method!=HttpMethod.Post||r.RequestUri?.AbsoluteUri!="https://guardian.cloudprix.com.br/api/v2/CreateToken")throw new InvalidOperationException("MM_RETRY_TOKEN_ROUTE_REJECTED");return base.SendAsync(r,ct);}}
}
