using System.Net;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Integracao.Guardian;

public class R57AdaptersTests
{
 static ContratoGuardianSet26 Contract()=>new("1106","SYNTHETIC",12.345m,"APPROVED-E",0m,"APPROVED-T"){CodigoEmissor="EM02",CodigoItem="MAT02"};
 // R69/R104 composer contract; approvals below are synthetic and scoped to the offline handler.
 static PreCadastroAcmR57 Request()=>new("https://callback.invalid/approved",
  new(0,"7000000","20000184","1106",[new("DOC-OFFLINE","2.5")],"OC1106",
   [new(1000,"AC3P"),new(1002,"3011"),new(1003,"10")])
  {CodigoTransportadora="SYNTH-CARRIER",CodigoFluxo="FLX_RECEBIMENTO"}){CallbackHomologado=true};
 static string SoapResponse(string error="0",string code="1106",string sequence="581")=>$"<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><CadastraContratoResponse xmlns=\"http://toledobrasil.com.br/WS_Guardian\"><voRetornoContrato><VORetornoContrato><Erro>{error}</Erro><Contrato><Codigo>{code}</Codigo><Sequencial>{sequence}</Sequencial></Contrato></VORetornoContrato></voRetornoContrato></CadastraContratoResponse></soap:Body></soap:Envelope>";
 [Fact] public async Task Soap_zero_retains_code_and_separate_sequence()
 {var h=new Wire(SoapResponse());var c=new GuardianContratoSoapClient(new(h),new(){ContratoSoapEndpoint=new("https://guardian.invalid/WS_GUARDIAN_PLUS.asmx"),Timeout=TimeSpan.FromSeconds(3)});var r=await c.CadastrarComRespostaAsync(Contract(),default);Assert.Equal("1106",r.CodigoContratoConfirmado);Assert.Equal(581,r.SequencialContrato);Assert.Contains("CodigoEmissor",h.Body);Assert.Contains("CodigoItem",h.Body);Assert.DoesNotContain("Veiculos",h.Body);}
 [Fact] public async Task Soap_nonzero_is_identifiable_without_leaking_body()
 {var h=new Wire(SoapResponse("7"));var c=new GuardianContratoSoapClient(new(h),new(){ContratoSoapEndpoint=new("https://guardian.invalid/WS_GUARDIAN_PLUS.asmx"),Timeout=TimeSpan.FromSeconds(3)});var e=await Assert.ThrowsAsync<GuardianHttpException>(()=>c.CadastrarComRespostaAsync(Contract(),default));Assert.Equal(7,e.BusinessErrorCode);Assert.DoesNotContain("1106",e.Message);}
 [Theory][InlineData("http://192.168.17.91/WS_GUARDIAN_PLUS.asmx",false,"HML")][InlineData("http://192.168.17.92/WS_GUARDIAN_PLUS.asmx",true,"HML")][InlineData("http://192.168.17.91:8080/WS_GUARDIAN_PLUS.asmx",true,"HML")][InlineData("http://192.168.17.91/WS_GUARDIAN_PLUS.asmx",true,"PRD")]
 public async Task Internal_http_requires_exact_destination_and_approval(string uri,bool approved,string environment)
 {var h=new Wire(SoapResponse());var c=new GuardianContratoSoapClient(new(h),new(){ContratoSoapEndpoint=new(uri),Timeout=TimeSpan.FromSeconds(3),ContratoSoapInternalHmlHttpApproved=approved,ContratoSoapEnvironment=environment});await Assert.ThrowsAsync<GuardianHttpException>(()=>c.CadastrarComRespostaAsync(Contract(),default));Assert.Equal(0,h.Requests);}
 [Fact] public async Task Explicit_internal_hml_http_is_supported_with_fake_transport_only()
 {var h=new Wire(SoapResponse());var c=new GuardianContratoSoapClient(new(h),new(){ContratoSoapEndpoint=new("http://192.168.17.91/WS_GUARDIAN_PLUS.asmx"),Timeout=TimeSpan.FromSeconds(3),ContratoSoapInternalHmlHttpApproved=true,ContratoSoapEnvironment="HML"});var r=await c.CadastrarComRespostaAsync(Contract(),default);Assert.Equal("1106",r.CodigoContratoConfirmado);Assert.Equal(1,h.Requests);}
 [Fact] public async Task Rest_minimal_accepts_positive_id_and_null_ticket()
 {
  var h=new Wire("{\"id\":3,\"codigoContrato\":\"1106\",\"codigoItem\":\"7000000\",\"codigoEmissor\":\"20000184\",\"numeroReferencia\":\"OC1106\",\"numeroTicket\":null}");
  var r=await Rest(h).EnviarMinimoAcmAsync(Request(),default);Assert.Equal(3,r.Id);Assert.Null(r.NumeroTicket);Assert.Equal(2,h.Requests);
  using var doc=JsonDocument.Parse(h.Body!);var data=doc.RootElement.GetProperty("dadosProtocoloPreCadastro");
  // AcmR57Mapping.Criar requires carrier/flow; retain an exact whitelist, not an open-ended count.
  Assert.Equal(new[]{"id","codigoItem","codigoEmissor","codigoContrato","documentos","numeroReferencia","camposAdicionais","codigoTransportadora","codigoFluxo"}.Order(StringComparer.Ordinal),data.EnumerateObject().Select(x=>x.Name).Order(StringComparer.Ordinal));
  Assert.Equal("SYNTH-CARRIER",data.GetProperty("codigoTransportadora").GetString());Assert.Equal("FLX_RECEBIMENTO",data.GetProperty("codigoFluxo").GetString());
  Assert.Equal(new[]{1000,1002,1003},data.GetProperty("camposAdicionais").EnumerateArray().Select(x=>x.GetProperty("numero").GetInt32()));
 }
 [Fact] public async Task Rest_response_wrong_reference_and_duplicate_keys_fail_closed_without_retry()
 {foreach(var response in new[]{"{\"id\":3,\"codigoContrato\":\"1106\",\"codigoItem\":\"7000000\",\"codigoEmissor\":\"20000184\",\"numeroReferencia\":\"OTHER\"}","{\"id\":3,\"id\":4}"}){var h=new Wire(response);var error=await Assert.ThrowsAsync<GuardianRestR57HttpException>(()=>Rest(h).EnviarMinimoAcmAsync(Request(),default));Assert.Equal("GUARDIAN_R57_RESPONSE_INVALID",error.SafeCode);Assert.True(error.ResultadoIndeterminado);Assert.Equal(2,h.Requests);}}
 [Fact] public async Task Rest_missing_document_blocks_before_token_authentication()
 {var h=new Wire("{}");await Assert.ThrowsAsync<AcmR57BlockedException>(()=>Rest(h).EnviarMinimoAcmAsync(Request()with{Dados=Request().Dados with{Documentos=[]}},default));Assert.Equal(0,h.Requests);}
 [Fact] public async Task Rest_timeout_has_no_automatic_retry()
 {var h=new Wire("{}"){Timeout=true};var error=await Assert.ThrowsAsync<GuardianRestR57HttpException>(()=>Rest(h).EnviarMinimoAcmAsync(Request(),default));Assert.Equal("GUARDIAN_R57_TIMEOUT",error.SafeCode);Assert.Equal(0,error.HttpStatusCode);Assert.True(error.ResultadoIndeterminado);Assert.Equal(2,h.Requests);}
 static GuardianSet26HttpClient Rest(Wire h){var o=new GuardianHttpOptions{BaseUrl=new("https://guardian.invalid"),Timeout=TimeSpan.FromSeconds(3),PreCadastroEndpoint="/api/v1/Guardian/ProtocoloPreCadastro"};var http=new HttpClient(h);return new(http,o,new(http,o,new Credentials()));}
 sealed class Credentials:ICredenciaisGuardianSet26Provider{public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct)=>Task.FromResult(new CredenciaisGuardianSet26("SYNTHETIC-USER","SYNTHETIC-PASSWORD","SYNTHETIC-SIGNATURE"));}
 sealed class Wire(string response):HttpMessageHandler
 {public int Requests;public string? Body;public bool Timeout;protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Requests++;if(r.RequestUri!.AbsolutePath.Contains("CreateToken"))return new(HttpStatusCode.OK){Content=new StringContent("{\"token\":\"synthetic-token\"}")};Body=await r.Content!.ReadAsStringAsync(ct);if(Timeout)throw new TaskCanceledException();return new(HttpStatusCode.OK){Content=new StringContent(response,Encoding.UTF8,r.Content.Headers.ContentType?.MediaType=="text/xml"?"text/xml":"application/json")};}}
}
