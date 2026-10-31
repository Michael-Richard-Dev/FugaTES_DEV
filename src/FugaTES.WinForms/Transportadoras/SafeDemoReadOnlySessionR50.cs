using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using FugaTES.Aplicacao.Transportadoras.SnapshotsR24;
using FugaTES.WinForms.Transportadoras.MasterdataR3;
namespace FugaTES.WinForms.Transportadoras;
public sealed record DemoSapObservationR50(string BusinessPartner,string State,int? HttpStatus,bool IdentityMatch,bool NamePresent,bool LegalNamePresent,string Crm010Eligibility);
// Manual read-only preview. Reuses R20B, R25T and the R15/R20C lock; no database, timer or write composition.
public sealed class SafeDemoReadOnlySessionR50:IDisposable
{
 public static bool Enabled{get{
#if FUGATES_SAFE_DEMO_R50
return true;
#else
return false;
#endif
}}
 public OrquestradorCompartilhadoMasterdataR3 Shared{get;}=new();
 readonly Func<string,CancellationToken,Task<DemoSapObservationR50>> reader;
 readonly HashSet<string> allowed;
 readonly DateTimeOffset until;
 public string InitialBp{get;}
 public GuardianReadSessionR50B? GuardianRead{get;private set;}
 public GuardianReadObservationR50B? LastGuardian{get;private set;}
 public string GuardianStatus=>LastGuardian is {} g?$"{g.State} | AUTH_HTTP={g.AuthenticationHttp?.ToString()??"NÃO EXECUTADO"} | GET_HTTP={g.GetHttp?.ToString()??"NÃO EXECUTADO"} | Codigo confirmado={g.CodeMatch} | Escopo confirmado={g.ScopeConfirmed}. Não autoriza CREATE.":GuardianRead is null?"BLOQUEADO: provider Guardian READ não vinculado nesta sessão.":"NÃO CONSULTADO: autenticação HML e GET unitário disponíveis somente por ação manual com escopo aprovado R43. Escritas de negócio bloqueadas.";
 public string GuardianHistoricalStatus{get;private set;}="Sem evidência histórica carregada; nenhuma consulta Guardian nesta sessão.";
 public string? SapStatus{get;private set;}
 public DemoSapObservationR50? LastSap{get;private set;}
 public SafeDemoReadOnlySessionR50(Func<string,CancellationToken,Task<DemoSapObservationR50>>? reader=null,IEnumerable<string>? allowedBps=null,string initialBp="20000014",DateTimeOffset? untilUtc=null,GuardianReadSessionR50B? guardianRead=null)
 {
  GuardianRead=guardianRead;
  this.reader=reader??((bp,ct)=>Task.FromResult(Block(bp,"R50_SAP_PROFILE_UNAVAILABLE")));
  allowed=new(allowedBps??[initialBp],StringComparer.Ordinal);InitialBp=initialBp;until=untilUtc??DateTimeOffset.MaxValue;
 }
 static DemoSapObservationR50 Block(string bp,string code,int? status=null)=>new(bp,code,status,false,false,false,"UNVERIFIED");
 public static SafeDemoReadOnlySessionR50 Create(string directory)
 {
  try
  {
   if(File.Exists(Path.Combine(directory,"Acm.execution.R57.json")))return new(allowedBps:Array.Empty<string>(),initialBp:"");
   var json=File.ReadAllText(Path.Combine(directory,"FugaTES.demo.json"));
   var profile=JsonSerializer.Deserialize<DemoProfile>(json,new JsonSerializerOptions{UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow})!;
   if(profile.Mode!="READ_ONLY_DEMO_R50"||profile.AllowedBps.Length!=1||profile.InitialBp!="20000014"||profile.InitialBp!=profile.AllowedBps[0]||profile.SapClient!="110"||profile.SapBaseUrl!="https://vhfufds4ci.sap.fugacouros.com.br:44300/sap/opu/odata/sap/API_BUSINESS_PARTNER/")throw new InvalidOperationException();
   var session=new SafeDemoReadOnlySessionR50((bp,ct)=>ReadSapAsync(profile,bp,ct),profile.AllowedBps,profile.InitialBp,profile.UntilUtc);
   var historicPath=Path.Combine(directory,"Guardian.R43.observation.json");
   if(File.Exists(historicPath))
   {
    using var h=JsonDocument.Parse(File.ReadAllText(historicPath));var e=h.RootElement;
    if(e.GetProperty("Code").GetString()==profile.InitialBp&&e.GetProperty("HttpStatus").GetInt32()==404&&e.GetProperty("Gate").GetString()=="R43")
     session.GuardianHistoricalStatus=$"EVIDÊNCIA HISTÓRICA R43 — {e.GetProperty("ObservedOn").GetString()}: GET 404 scoped para o BP informado. Não é leitura R50 nem autorização de CREATE.";
   }
   if(!File.Exists(Path.Combine(directory,"Acm.preview.R55.json"))&&!File.Exists(Path.Combine(directory,"Acm.preview.R53.json")))session.GuardianRead=GuardianReadSessionR50B.Create(directory);
   return session;
  }catch{return new(allowedBps:Array.Empty<string>(),initialBp:"");}
 }
 public bool CanQuery(string bp)=>allowed.Contains(bp)&&DateTimeOffset.UtcNow<until;
 public async Task<DemoSapObservationR50> ConsultarSapAsync(string bp,CancellationToken ct=default)
 {
  if(!CanQuery(bp))return Observe(Block(bp,"R50_BP_NOT_AUTHORIZED"),DateTimeOffset.UtcNow);
  var reference=DateTimeOffset.UtcNow;
  try
  {
   var locked=await Shared.ExecutarConsultaProtegidaAsync(async token=>
   {
    if(!CanQuery(bp))return Block(bp,"R50_BP_NOT_AUTHORIZED");
    return await reader(bp,token).ConfigureAwait(false);
   },ct).ConfigureAwait(false);
   var result=locked.Situacao==SituacaoConsultaProtegidaMasterdataR3.Executada&&locked.Resultado is {} r?r:Block(bp,"R50_QUERY_CANCELLED");
   return Observe(result,reference);
  }catch(OperationCanceledException){return Observe(Block(bp,"R50_QUERY_CANCELLED"),reference);}
  catch{return Observe(Block(bp,"R50_QUERY_FAILED_NO_RETRY"),reference);}
 }
 DemoSapObservationR50 Observe(DemoSapObservationR50 result,DateTimeOffset reference)
 {
  LastSap=result;SapStatus=$"Consulta manual {reference.ToOffset(TimeSpan.FromHours(-3)):dd/MM/yyyy HH:mm:ss} BRT | {result.State}";return result;
 }
 static async Task<DemoSapObservationR50> ReadSapAsync(DemoProfile p,string bp,CancellationToken ct)
 {
  // Same approved INC002/R38 environment provider; no alternate user, origin, client or secret file.
  string? E(string k)=>Environment.GetEnvironmentVariable(k);
  if(!Uri.TryCreate(E("FUGAPET_SAP_BASE_URL"),UriKind.Absolute,out var source)||source.Scheme!="https"||source.Authority!="vhfufds4ci.sap.fugacouros.com.br:44300"||source.UserInfo.Length!=0||E("FUGAPET_SAP_CLIENT")!="110"||E("FUGAPET_SAP_ALLOWED_HOSTS")!="vhfufds4ci.sap.fugacouros.com.br"||string.IsNullOrWhiteSpace(E("FUGAPET_SAP_USERNAME"))||string.IsNullOrWhiteSpace(E("FUGAPET_SAP_PASSWORD")))return Block(bp,"R50_SAP_HML_READ_PROFILE_BLOCKED");
  using var transport=new SapGetOnlyHandler(bp,new HttpClientHandler{AllowAutoRedirect=false});
  using var http=new HttpClient(transport){Timeout=TimeSpan.FromSeconds(30),MaxResponseContentBufferSize=4*1024*1024};
  var client=new ClienteHttpParceiroNegocioSap(http,new OpcoesSap{BaseUrl=new(p.SapBaseUrl),SapClient=p.SapClient,Timeout=TimeSpan.FromSeconds(30)},new ExistingSapEnvironmentAuthentication());
  var reference=DateTimeOffset.UtcNow;
  var response=await new SapBpReadAdapterR20B(client).ReadAsync(bp,ct).ConfigureAwait(false);
  if(!response.Complete)return Block(bp,"R50_SAP_GET_NOT_CONFIRMED",transport.LastStatus);
  var raw=response.RawSapData;
  static string? Text(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
  string eligibility="INDETERMINATE";
  if(raw.TryGetProperty("to_BusinessPartnerRole",out var nav)&&nav.TryGetProperty("results",out var rows)&&rows.ValueKind==JsonValueKind.Array)
  {
   var crm=rows.EnumerateArray().Where(r=>Text(r,"BusinessPartnerRole")=="CRM010").ToArray();
   if(crm.Length==1&&Text(crm[0],"BusinessPartner")==bp)
   {
    var from=CarrierManualODataDateParser.Parse(Text(crm[0],"ValidFrom"));var to=CarrierManualODataDateParser.Parse(Text(crm[0],"ValidTo"));
    eligibility=new Crm010BusinessDatePolicyR25T().Evaluate(from,to,reference).Status switch{AvaliacaoCrm010R24.EVALUATED_VALID=>"VALID",AvaliacaoCrm010R24.EVALUATED_INVALID=>"INVALID",_=>"INDETERMINATE"};
   }
  }
  return new(bp,"R50_SAP_GET_CONFIRMED",transport.LastStatus,Text(raw,"BusinessPartner")==bp,!string.IsNullOrWhiteSpace(Text(raw,"BusinessPartnerName")),!string.IsNullOrWhiteSpace(Text(raw,"BusinessPartnerFullName")),eligibility);
 }
 public async Task<GuardianReadObservationR50B> ConsultarGuardianAsync(string code,string approvedScope,CancellationToken ct=default,bool scopeAttested=false)
 {
  if(GuardianRead is null||!CanQuery(code))return LastGuardian=new("BLOCKED_BINDING",null,null);
  var result=await Shared.ExecutarConsultaProtegidaAsync(token=>GuardianRead.ReadAsync(code,approvedScope,token,scopeAttested),ct).ConfigureAwait(false);
  return LastGuardian=result.Resultado??new("BLOCKED_BUSY_OR_CANCELLED",null,null);
 }
 public Task StopAsync()=>Shared.EncerrarAsync();
 public void Dispose(){Shared.Encerrar();GuardianRead?.Dispose();}
 sealed record DemoProfile(string Mode,string InitialBp,string[] AllowedBps,DateTimeOffset UntilUtc,string SapBaseUrl,string SapClient);
 sealed class ExistingSapEnvironmentAuthentication:IAutenticacaoSap
 {
  public Task AplicarAsync(HttpRequestMessage request,CancellationToken ct)
  {
   ct.ThrowIfCancellationRequested();if(request.Method!=HttpMethod.Get||request.RequestUri?.GetLeftPart(UriPartial.Authority)!="https://vhfufds4ci.sap.fugacouros.com.br:44300")throw new InvalidOperationException("R50_SAP_READ_ORIGIN_BLOCKED");
   var user=Environment.GetEnvironmentVariable("FUGAPET_SAP_USERNAME");var password=Environment.GetEnvironmentVariable("FUGAPET_SAP_PASSWORD");
   if(string.IsNullOrWhiteSpace(user)||string.IsNullOrWhiteSpace(password))throw new InvalidOperationException("R50_SAP_CREDENTIAL_UNAVAILABLE");
   request.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(user+":"+password)));return Task.CompletedTask;
  }
 }
 public sealed class SapGetOnlyHandler(string bp,HttpMessageHandler inner):DelegatingHandler(inner)
 {
  public int? LastStatus{get;private set;}
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {
   var uri=request.RequestUri;
   if(request.Method!=HttpMethod.Get||uri is null||uri.Scheme!="https"||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||uri.GetLeftPart(UriPartial.Authority)!="https://vhfufds4ci.sap.fugacouros.com.br:44300"||Uri.UnescapeDataString(uri.AbsolutePath)!="/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner('"+bp+"')"||!uri.Query.Split('&').Any(s=>s=="sap-client=110"))throw new InvalidOperationException("R50_HTTP_WRITE_OR_SCOPE_BLOCKED");
   var response=await base.SendAsync(request,ct).ConfigureAwait(false);LastStatus=(int)response.StatusCode;return response;
  }
 }
}
