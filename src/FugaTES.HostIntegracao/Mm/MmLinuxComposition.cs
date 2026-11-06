using FugaTES.Aplicacao.Configuracao;
using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
using FugaTES.Infraestrutura.Mm;
using FugaTES.Integracao.Guardian;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.Persistencia.Mm;
using Npgsql;
namespace FugaTES.HostIntegracao.Mm;
public static class MmLinuxComposition
{
 public static void Configure(IServiceCollection services,IConfiguration config)
 {
  var options=config.GetSection("MmPurchaseOrders").Get<MmLinuxOptions>()??new();
  if(config["Sap:BaseUrl"] is not null||config["Sap:SapClient"] is not null)
  {
   var sap=AcmEnvironmentBinding.Sap(config);
   if(config["MmPurchaseOrders:SapOrigin"] is not null&&options.SapOrigin!=sap.Origem||config["MmPurchaseOrders:SapClient"] is not null&&options.SapClient!=sap.SapClient)throw new InvalidOperationException("MM_PROCESS_ENVIRONMENT_MISMATCH");
   options=options with{SapOrigin=sap.Origem,SapClient=sap.SapClient};
  }
  options.Validate();
  services.AddSystemd();services.AddSingleton(options);services.AddHostedService<MmLinuxWorker>();
 }
}
public sealed class MmLinuxWorker(MmLinuxOptions options,ILogger<MmLinuxWorker> log):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken stoppingToken)
 {
  if(!options.Enabled){log.LogInformation("MM_VM_INERT");return;}
  try
  {
   await using var runtime=await MmLinuxRuntime.CreateAsync(options,stoppingToken);
   await runtime.RunAsync(stoppingToken);
  }
  catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){}
  catch{Environment.ExitCode=1;log.LogError("MM_VM_STOPPED_SAFE; no automatic replay of occupied keys");throw new InvalidOperationException("MM_VM_RUNTIME_FAILED");}
 }
}
public sealed class MmLinuxRuntime:IAsyncDisposable,IMmGuardianDispatcher
{
 private readonly MmLinuxOptions options;
 private readonly NpgsqlDataSource database;
 private readonly MmWorkerLease lease;
 private readonly HttpClient sap,guardian;
 private readonly MmFileSnapshotStore store;
 private readonly RegistroProtocolosMmPostgreSql registry;
 private readonly EnvioAutomaticoMm sender;
 private readonly GuardianEmitterReadTransport? emitterRead;
 private readonly GuardianMaterialReadTransport? materialRead;
 private readonly MmRetryExecutor? retry;
 private MmLinuxRuntime(MmLinuxOptions options,NpgsqlDataSource database,MmWorkerLease lease,HttpClient sap,HttpClient guardian,MmFileSnapshotStore store,GuardianTokenClient token,MmRetryOptions retryOptions)
 {
  this.options=options;this.database=database;this.lease=lease;this.sap=sap;this.guardian=guardian;this.store=store;
  registry=new(database,options.AmbienteBanco);
  var raw=new GuardianProtocoloMmClient(guardian,token.ObterTokenAsync,TimeSpan.FromSeconds(30));
  IEnviarProtocoloMmGateway delivery=raw;
  if(options.MasterdataValidationEnabled)
  {
   emitterRead=new();materialRead=new();var httpOptions=new GuardianHttpOptions{BaseUrl=new("https://guardian.cloudprix.com.br"),Timeout=TimeSpan.FromSeconds(20)};
   var gate=new MmMasterdataGate(new GuardianEmitterReadAdapter(emitterRead.Client,httpOptions,token,retryOptions.GuardianSignatureId),new GuardianMaterialReadAdapter(materialRead.Client,httpOptions,token,retryOptions.GuardianSignatureId),retryOptions.GuardianSignatureId);
   delivery=new MmValidatedGuardianGateway(gate,raw);
   if(options.RetryCommandsEnabled&&retryOptions.Enabled)retry=new(new MmRetryPostgreSqlStore(database,options.AmbienteBanco),gate,raw,retryOptions);
  }
  sender=new(registry,delivery,options.CallbackUrl,lease.CheckAsync);
 }
 public static async Task<MmLinuxRuntime> CreateAsync(MmLinuxOptions options,CancellationToken ct)
 {
  options.Validate();if(!options.Enabled||!OperatingSystem.IsLinux()||Environment.MachineName!="vm-fugatesbio")throw new InvalidOperationException("MM_VM_HOST_REJECTED");
  var dir=MmRetryPolicyFile.RuntimeDirectoryFor(options.AmbienteBanco);
  if(Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY")!=dir)throw new InvalidOperationException("MM_SYSTEMD_SCOPE_REJECTED");
  var retryPolicy=options.MasterdataValidationEnabled?MmRetryPolicyFile.ReadPolicy(dir+"/mm-retry-policy",options.AmbienteBanco):new MmRetryOptions();
  if(options.MasterdataValidationEnabled&&retryPolicy.GuardianSignatureId<=0||options.RetryCommandsEnabled&&!retryPolicy.Enabled)throw new InvalidOperationException("MM_RETRY_APPROVED_POLICY_REQUIRED");
  var credentials=new CarrierSystemdCredentials(dir,options.AmbienteBanco);
  // Existing approved MM JSON provider is mounted under this credential ID; no Windows secret or fallback.
  using var doc=JsonDocument.Parse(credentials.Read("carrier-hml-postgres-password"));var db=doc.RootElement;
  if(db.GetProperty("username").GetString()!=options.Role||db.GetProperty("database").GetString()!=options.Database||db.GetProperty("port").GetInt32()!=options.Port)throw new InvalidOperationException("MM_PROVIDER_IDENTITY_REJECTED");
  var password=db.GetProperty("password").GetString();if(string.IsNullOrWhiteSpace(password))throw new InvalidOperationException("MM_PROVIDER_EMPTY");
  var cs=new NpgsqlConnectionStringBuilder{Host=options.Host,Port=options.Port,Database=options.Database,Username=options.Role,Password=password,SearchPath="fugates",IncludeErrorDetail=false,ApplicationName="FugaTES.MM.VM",Timeout=10,CommandTimeout=10}.ConnectionString;
  NpgsqlDataSource? source=null;MmWorkerLease? lease=null;HttpClient? sap=null,guardian=null;
  try
  {
   source=new NpgsqlDataSourceBuilder(cs).Build();lease=await MmWorkerLease.AcquireAsync(cs,ct,options.AmbienteBanco);
   sap=new(new MmSapOnlyHandler(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false},options.AmbienteSap)){Timeout=Timeout.InfiniteTimeSpan};
   guardian=new(new MmGuardianOnlyHandler(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false})){Timeout=Timeout.InfiniteTimeSpan};
   var token=new GuardianTokenClient(guardian,new GuardianHttpOptions{BaseUrl=new("https://guardian.cloudprix.com.br"),Timeout=TimeSpan.FromSeconds(30)},new CarrierLinuxGuardianCredentials(credentials));
   var store=new MmFileSnapshotStore(options.StateDirectory,ambiente:options.AmbienteSap);
   var records=await new RegistroProtocolosMmPostgreSql(source,options.AmbienteBanco).LerAsync(ct);
   store.Read();
   store.Write(new(){Origin=options.SapOrigin,Client=options.SapClient,RuntimeMode="VM",Host="vm-fugatesbio",State="MM_NOT_STARTED",GuardianState="MM_GUARDIAN_PRONTO",GuardianCallback=options.CallbackUrl,GuardianRecords=records,RegistryReadUtc=DateTimeOffset.UtcNow});
   return new(options,source,lease,sap,guardian,store,token,retryPolicy);
  }
  catch{sap?.Dispose();guardian?.Dispose();if(lease is not null)await lease.DisposeAsync();if(source is not null)await source.DisposeAsync();throw;}
 }
 public static IPedidosCompraMmConsulta CreateQuery(MmLinuxOptions options,HttpClient sap,FugaTES.Integracao.SAP.Autenticacao.IAutenticacaoSap authentication)
 {options.Validate();return new PedidosCompraMmConsultaAdapter(sap,authentication,options.AmbienteSap);}
 public async Task RunAsync(CancellationToken ct)
 {
  var credentials=new CarrierSystemdCredentials(MmRetryPolicyFile.RuntimeDirectoryFor(options.AmbienteBanco),options.AmbienteBanco);
  var query=CreateQuery(options,sap,new CarrierLinuxSapAuthentication(new(options.SapOrigin),credentials,"FUGATES"));
  using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);
  var loop=new MmPurchaseOrderCollector(query,store,this,()=>lease.IsHeld).RunAsync(MmCollectionPolicy.Interval,lifetime.Token);
  try
  {
   while(!loop.IsCompleted)
   {
    await Task.WhenAny(loop,Task.Delay(TimeSpan.FromSeconds(5),ct));
    ct.ThrowIfCancellationRequested();if(loop.IsCompleted)break;
    if(!await lease.CheckAsync(ct))throw new InvalidOperationException("MM_WORKER_LEASE_LOST");
   }
   await loop;
  }
  finally
  {
   lifetime.Cancel();try{await loop;}catch(OperationCanceledException){}
   store.Update(x=>x with{State="MM_VM_STOPPED",GuardianState="MM_GUARDIAN_BLOQUEADO_LEASE"});
  }
 }
 public async Task<MmGuardianReadback> ProcessarAsync(IReadOnlyList<PedidoCompraMmLeitura> rows,CancellationToken ct,Func<bool> admission)
 {
  if(rows.Any(x=>!MmCollectionPolicy.Eligible(x)))return new("MM_GUARDIAN_BLOQUEADO_DATA",null);
  var outcome=await sender.ProcessarAsync(rows,ct,admission);
  if(retry is not null)
   for(var n=0;n<10&&admission();n++)if(!await retry.ProcessOneAsync(rows,admission,lease.CheckAsync,ct))break;
  using var readback=new CancellationTokenSource(TimeSpan.FromSeconds(15));
  return new(outcome.Bloqueados>0?"MM_GUARDIAN_COM_BLOQUEIOS":"MM_GUARDIAN_VERIFICADO",await registry.LerAsync(readback.Token));
 }
 public async ValueTask DisposeAsync(){emitterRead?.Dispose();materialRead?.Dispose();sap.Dispose();guardian.Dispose();await lease.DisposeAsync();await database.DisposeAsync();}
}
public sealed class MmSapOnlyHandler(HttpMessageHandler inner,AmbienteSap? ambiente=null):DelegatingHandler(inner)
{
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
 {if(request.Method!=HttpMethod.Get||request.RequestUri is null)throw new InvalidOperationException("MM_SAP_METHOD_REJECTED");PedidosCompraMmConsultaAdapter.ValidateUri(request.RequestUri);var a=ambiente??AmbientesSap.Dev110;
 if(request.RequestUri.GetLeftPart(UriPartial.Authority)!=a.Origem||System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["sap-client"] is {} client&&client!=a.SapClient)throw new InvalidOperationException("MM_SAP_PROCESS_ENVIRONMENT_REJECTED");return base.SendAsync(request,ct);}
}
public sealed class MmGuardianOnlyHandler(HttpMessageHandler inner):DelegatingHandler(inner)
{
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
 {
  var u=request.RequestUri;
  if(request.Method!=HttpMethod.Post||u is null||u.Scheme!="https"||u.GetLeftPart(UriPartial.Authority)!="https://guardian.cloudprix.com.br"||u.UserInfo.Length!=0||u.Fragment.Length!=0||u.Query.Length!=0||u.AbsolutePath is not("/api/v2/CreateToken" or "/api/v1/Guardian/ProtocoloPreCadastro"))throw new InvalidOperationException("MM_GUARDIAN_ROUTE_REJECTED");
  return base.SendAsync(request,ct);
 }
}
