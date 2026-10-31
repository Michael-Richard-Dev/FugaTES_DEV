using System.Net;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Http;
namespace FugaTES.Testes;
public sealed class ToledoSapDeliveryContractTests
{
 static string Xml()=>File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","ToledoSapCreated.SYNTHETIC.xml"));
 static DocumentoToledoSap Document(string? json=null){json??=ToledoSapOData201Tests.Payload;return ToledoSapPartnerFixture.Bind(new(Guid.NewGuid(),"3011",json.Contains("00900003")?"00900003":"900003",json,RegistrarPesagemToledoCasoDeUso.Fingerprint(json),true,false));}
 static DocumentoToledoSapGateway Gateway(Wire wire)=>new(new(new HttpClient(wire),new(){BaseUrl=new("https://sap.invalid"),SapClient="110",Timeout=TimeSpan.FromSeconds(1)},new Auth(),new CookieContainer()));
 [Fact] public async Task Proven_201_confirms_once_with_versioned_fingerprints_and_fresh_session()
 {var w=new Wire();var r=await Gateway(w).EnviarOriginalAsync(Document(),(_,_)=>Task.FromResult(true),default);Assert.True(r.Confirmado);Assert.Equal(201,r.HttpStatus);Assert.Equal(1,w.Posts);Assert.Equal(5,w.Lookups);Assert.Equal(1,w.Fetches);Assert.Equal(64,r.RequestSha256!.Length);Assert.Equal(64,r.ResponseSha256!.Length);Assert.Equal(ToledoSapPreparedPayload.Version,r.PayloadVersion);}
 [Fact] public async Task Existing_matching_receipt_is_confirmed_without_CSRF_or_POST()
 {var w=new Wire{LookupCode=200};var r=await Gateway(w).EnviarOriginalAsync(Document(),(_,_)=>Task.FromResult(true),default);Assert.True(r.Confirmado);Assert.False(r.PostIniciado);Assert.Equal(ResultadoToledoSapCodigo.RecebimentoJaConfirmadoSap,r.Codigo);Assert.Equal(0,w.Posts);Assert.Equal(0,w.Fetches);}
 [Fact] public async Task Existing_divergent_receipt_is_blocked_without_POST()
 {var w=new Wire{LookupCode=200,Body=Xml().Replace(">1.1<",">4.4<")};var r=await Gateway(w).EnviarOriginalAsync(Document(),(_,_)=>Task.FromResult(true),default);Assert.False(r.Confirmado);Assert.Equal(ResultadoToledoSapCodigo.RegistroSapExistenteDivergente,r.Codigo);Assert.Equal(0,w.Posts);}
 [Fact] public async Task Leading_zero_alias_is_checked_but_never_silently_replaces_payload_identity()
 {var w=new Wire{AliasExists=true};var doc=Document(ToledoSapOData201Tests.Payload.Replace("\"IdPesagem\":900003","\"IdPesagem\":\"00900003\""));var r=await Gateway(w).EnviarOriginalAsync(doc,(_,_)=>Task.FromResult(true),default);Assert.Equal(2,w.Lookups);Assert.Equal(0,w.Posts);Assert.Equal(ResultadoToledoSapCodigo.RegistroSapExistenteDivergente,r.Codigo);}
 [Theory][InlineData(404,"<html>not found</html>")][InlineData(401,"{}")] [InlineData(500,"{}")] [InlineData(404,"{}")] 
 public async Task Unproven_absence_cannot_dispatch(int status,string body)
 {var w=new Wire{LookupCode=status,LookupError=body,AbsenceFeed="<html>not a proven collection</html>"};var r=await Gateway(w).EnviarOriginalAsync(Document(),(_,_)=>Task.FromResult(true),default);Assert.Equal(ResultadoToledoSapCodigo.FalhaAntesDoPost,r.Codigo);Assert.Equal(0,w.Posts);}
 [Theory][InlineData(201,"<html>created</html>")][InlineData(200,"{}")] [InlineData(204,"")]
 public async Task Transport_success_without_proven_entity_is_unknown_not_retried(int status,string body)
 {var w=new Wire{PostCode=status,Body=body};var r=await Gateway(w).EnviarOriginalAsync(Document(),(_,_)=>Task.FromResult(true),default);Assert.True(r.Indeterminado);Assert.False(r.Confirmado);Assert.Equal(1,w.Posts);}
 [Fact] public async Task Real_gateway_timeout_cannot_retry()
 {var w=new Wire{Timeout=true};var r=await Gateway(w).EnviarOriginalAsync(Document(),(_,_)=>Task.FromResult(true),default);Assert.True(r.Indeterminado);Assert.Equal(1,w.Posts);}
 [Fact] public async Task Live_fence_is_rechecked_after_reads_and_CSRF()
 {var w=new Wire();var r=await Gateway(w).EnviarOriginalAsync(Document(),(_,_)=>Task.FromResult(false),default);Assert.Equal(1,w.Fetches);Assert.Equal(0,w.Posts);Assert.False(r.PostIniciado);}
 [Fact] public async Task Missing_measured_quality_is_blocked_before_any_SAP_request()
 {var w=new Wire();var d=Document(ToledoSapOData201Tests.Payload.Replace("\"IMPUREZA\"","\"\"").Replace("\"1.1\"","\"0.0\"").Replace("\"UMIDADE\"","\"\"").Replace("\"2.2\"","\"0.0\""));var r=await Gateway(w).EnviarOriginalAsync(d,(_,_)=>Task.FromResult(true),default);Assert.Equal("SAP_MEASURED_QUALITY_REQUIRED",r.DetailCode);Assert.Equal(0,w.Lookups);Assert.Equal(0,w.Posts);}
 [Fact] public async Task Missing_service_OData_error_is_not_entity_absence_and_cannot_POST()
 {var w=new Wire{LookupError="""{"error":{"code":"SERVICE_NOT_FOUND","message":{"value":"Missing service"}}}""",AbsenceFeed="<error/>"};var r=await Gateway(w).EnviarOriginalAsync(Document(),(_,_)=>Task.FromResult(true),default);Assert.Equal(0,w.Posts);Assert.Equal("SAP_EXISTENCE_NOT_VERIFIED",r.DetailCode);}
 [Fact] public async Task Prepared_projection_is_delivered_to_durable_guard_before_any_POST()
 {var w=new Wire();PlanoEnvioToledoSap? seen=null;var r=await Gateway(w).EnviarOriginalAsync(Document(),(plan,_)=>{Assert.Equal(0,w.Posts);seen=plan;return Task.FromResult(true);},default);Assert.NotNull(seen);Assert.True(seen.Valido);Assert.Equal(r.RequestSha256,seen.RequestSha256);Assert.Equal(1767225600000,seen.EpochMilliseconds);}
 [Fact] public async Task Predispatch_audit_failure_never_emits_a_POST_or_false_success()
 {var w=new Wire();var r=await Gateway(w).EnviarOriginalAsync(Document(),(_,_)=>throw new IOException("SYNTHETIC_AUDIT_FAILURE"),default);Assert.Equal(0,w.Posts);Assert.False(r.Confirmado);}
 sealed class Auth:IAutenticacaoSap{public Task AplicarAsync(HttpRequestMessage r,CancellationToken ct){r.Headers.Authorization=new("Basic","OFFLINE_ONLY");return Task.CompletedTask;}}
 sealed class Wire:HttpMessageHandler
 {
  public int LookupCode=404,PostCode=201,Lookups,Posts,Fetches;public bool Timeout,AliasExists;public string? AbsenceFeed;public string Body=Xml(),LookupError="""{"error":{"code":"NOT_FOUND","message":{"value":"No entry"}}}""";
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {
   Assert.Equal("sap.invalid",request.RequestUri!.Host);Assert.Contains("sap-client=110",request.RequestUri.Query);Assert.NotNull(request.Headers.Authorization);
   if(request.Method==HttpMethod.Get&&request.Headers.Contains("X-CSRF-Token")){Fetches++;var response=new HttpResponseMessage(HttpStatusCode.OK);response.Headers.Add("X-CSRF-Token","FRESH_SYNTHETIC");response.Headers.Add("Set-Cookie","SESSION=SYNTHETIC; Path=/");return Task.FromResult(response);}
   if(request.RequestUri.Query.Contains("$inlinecount="))return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(AbsenceFeed??"""<feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata"><id>https://sap.invalid/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem</id><m:count>0</m:count></feed>""")});
   if(request.Method==HttpMethod.Get){Lookups++;Assert.Contains("$expand=",request.RequestUri.Query);var code=AliasExists&&Lookups==2?200:LookupCode;return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code){Content=new StringContent(code==200?Body:LookupError)});}
   Assert.Equal(HttpMethod.Post,request.Method);Posts++;Assert.Equal("application/atom+xml",request.Headers.GetValues("Accept").Single());Assert.Equal("FRESH_SYNTHETIC",request.Headers.GetValues("X-CSRF-Token").Single());Assert.Contains("SESSION=SYNTHETIC",request.Headers.GetValues("Cookie").Single());if(Timeout)throw new TaskCanceledException();return Task.FromResult(new HttpResponseMessage((HttpStatusCode)PostCode){Content=new StringContent(Body)});
  }
 }
}
