using System.Net;
using System.Text.Json;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Callback.Http;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Http;
namespace FugaTES.Testes;

public sealed class ToledoGuardianWireCompatibilityTests
{
 const string Input="""{"SistemaBalanca":"Toledo","DataPesagem":"10/29/2026 9:48:24 AM","TipoPesagem":"E","IdPesagem":"00900003","Centro":"3011","Material":"9000000","PesoBruto":"33000.0","PesoTara":"15000.0","PesoAntesDesconto":"18000.0","PesoAposDesconto":"18000.0","Observacao":"SYNTHETIC OFFLINE ONLY","to_QualidPes":[{"Tipo":"1","ValorAferido":"0.0"},{"Tipo":"2","ValorAferido":"0.0"},{"Tipo":"","ValorAferido":""}],"to_Pesagem":[{"Indice":1,"NrIndiceTotal":1,"ContratoACM":"0099999910","IdParceiro":"90000010","IdParceiroEntrega":"90000010","ChaveAcessoNFe":"00000000000000000000000000000000000000000001","PercentualDesdobro":"100","PesoAntesDesconto":"18000.0","PesoAposDesconto":"18000.0"}]}""";
 static string WithDirection(string type)=>Input.Replace("\"TipoPesagem\":\"E\"","\"TipoPesagem\":\""+type+"\"");
 static ToledoSapPreparedPayload Prepare(string input)=>ToledoSapODataPayload.Prepare(input,new("America/Sao_Paulo"));
 [Theory][InlineData("E")][InlineData("S")][InlineData("1")][InlineData("2")]
 public void Callback_accepts_direction_without_rewriting_original_document(string type)
 {
  var original=WithDirection(type);using var j=JsonDocument.Parse(original);
  var dto=ToledoPesagemCallbackDto.Deserializar(j.RootElement);
  Assert.Equal(type,dto.TipoPesagem);Assert.Equal(original,dto.DocumentoJson);
  Assert.Equal(original,dto.Mapear().DocumentoJson);Assert.Equal("00900003",dto.IdPesagem);
  Assert.Equal("1",dto.Qualidades[0].Tipo);Assert.Equal("0.0",dto.Qualidades[0].ValorAferido);
 }
 [Theory][InlineData("E","E")][InlineData("S","S")][InlineData("1","E")][InlineData("2","S")]
 public void SAP_projection_normalizes_documented_direction_and_quality_aliases_only(string type,string expected)
 {
  var original=WithDirection(type);var fingerprint=RegistrarPesagemToledoCasoDeUso.Fingerprint(original);
  var prepared=Prepare(original);using var j=JsonDocument.Parse(prepared.Json);var r=j.RootElement;
  Assert.Equal(expected,r.GetProperty("TipoPesagem").GetString());
  Assert.Equal("0000900003",r.GetProperty("IdPesagem").GetString());
  Assert.Equal("9000000",r.GetProperty("Material").GetString());
  Assert.Equal("0099999910",r.GetProperty("to_Pesagem")[0].GetProperty("ContratoACM").GetString());
  Assert.Equal("90000010",r.GetProperty("to_Pesagem")[0].GetProperty("IdParceiro").GetString());
  var q=r.GetProperty("to_QualidPes");Assert.Equal(2,q.GetArrayLength());
  Assert.Equal("IMPUREZA",q[0].GetProperty("Tipo").GetString());Assert.Equal("UMIDADE",q[1].GetProperty("Tipo").GetString());
  Assert.All(q.EnumerateArray(),v=>Assert.Equal("0.0",v.GetProperty("ValorAferido").GetString()));
  Assert.Equal(new DateTimeOffset(2026,10,29,12,48,24,TimeSpan.Zero).ToUnixTimeMilliseconds(),prepared.EpochMilliseconds);
  Assert.Equal(fingerprint,RegistrarPesagemToledoCasoDeUso.Fingerprint(original));
  Assert.Equal(type,JsonDocument.Parse(original).RootElement.GetProperty("TipoPesagem").GetString());
 }
 [Fact] public void Named_quality_and_arbitrary_measured_values_remain_unchanged()
 {
  var input=Input.Replace("\"Tipo\":\"1\"","\"Tipo\":\"IMPUREZA\"").Replace("\"Tipo\":\"2\"","\"Tipo\":\"UMIDADE\"").Replace("\"0.0\"","\"3.25\"");
  using var j=JsonDocument.Parse(Prepare(input).Json);
  Assert.All(j.RootElement.GetProperty("to_QualidPes").EnumerateArray(),v=>Assert.Equal("3.25",v.GetProperty("ValorAferido").GetString()));
 }
 [Theory][InlineData("1","IMPUREZA")][InlineData("2","UMIDADE")]
 public void Numeric_and_named_alias_cannot_hide_duplicate_SAP_quality_key(string numeric,string named)
 {
  var json=WithDirection("1").Replace("\"Tipo\":\"1\"","\"Tipo\":\""+numeric+"\"").Replace("\"Tipo\":\"2\"","\"Tipo\":\""+named+"\"");
  Assert.Equal("SAP_QUALITY_DUPLICATE",Assert.Throws<ToledoSapPayloadException>(()=>Prepare(json)).Code);
 }
 [Theory][InlineData("X")][InlineData("3")][InlineData("")]
 public void Unknown_direction_is_blocked_before_business_transport(string direction)
 {Assert.Equal("SAP_WEIGHING_TYPE_INVALID",Assert.Throws<ToledoSapPayloadException>(()=>Prepare(WithDirection(direction))).Code);}
 [Fact] public void Outbound_audit_preserves_the_compatible_wire_format_version()
 {Assert.Equal("TOLEDO_SAP_ODATA_V1",ToledoSapPreparedPayload.Version);}
 [Theory][InlineData("E")][InlineData("S")] public async Task Existing_gateway_sends_normalized_body_once_after_prePOST_audit_without_real_network(string direction)
 {
  var input=WithDirection(direction);var dto=ToledoPesagemCallbackDto.Deserializar(JsonDocument.Parse(input).RootElement);
  var document=ToledoSapPartnerFixture.Bind(new DocumentoToledoSap(Guid.NewGuid(),dto.Centro,dto.IdPesagem,dto.DocumentoJson,RegistrarPesagemToledoCasoDeUso.Fingerprint(input),true,false));
  var wire=new OfflineWire{ExpectedDirection=direction};var executor=new SapODataHttpExecutor(new HttpClient(wire),new(){BaseUrl=new("https://sap.invalid"),SapClient="110",Timeout=TimeSpan.FromSeconds(2)},new OfflineAuth(),new CookieContainer());
  var audited=false;var gateway=new DocumentoToledoSapGateway(executor,new("America/Sao_Paulo"));
  var result=await gateway.EnviarOriginalAsync(document,(plan,_)=>{Assert.Equal(0,wire.Posts);Assert.True(plan.Valido);audited=true;return Task.FromResult(true);},default);
  Assert.True(audited);Assert.Equal(1,wire.Posts);Assert.Equal(400,result.HttpStatus);
  Assert.Equal(ResultadoToledoSapCodigo.RejeicaoHttp,result.Codigo);Assert.Equal(input,document.DocumentoJson);
 }
 sealed class OfflineAuth:IAutenticacaoSap
 {public Task AplicarAsync(HttpRequestMessage request,CancellationToken ct){request.Headers.Authorization=new("Basic","SYNTHETIC_OFFLINE");return Task.CompletedTask;}}
 sealed class OfflineWire:HttpMessageHandler
 {
  public int Posts;public string ExpectedDirection="E";
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {
   Assert.Equal("sap.invalid",request.RequestUri!.Host);Assert.NotNull(request.Headers.Authorization);
   if(request.Headers.Contains("X-CSRF-Token")&&request.Method==HttpMethod.Get)
   {var r=new HttpResponseMessage(HttpStatusCode.OK);r.Headers.Add("X-CSRF-Token","SYNTHETIC");r.Headers.Add("Set-Cookie","SESSION=SYNTHETIC; Path=/");return r;}
   if(request.Method==HttpMethod.Get&&request.RequestUri.Query.Contains("$inlinecount="))
    return new(HttpStatusCode.OK){Content=new StringContent("""<feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata"><id>https://sap.invalid/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem</id><m:count>0</m:count></feed>""")};
   if(request.Method==HttpMethod.Get)return new(HttpStatusCode.NotFound){Content=new StringContent("{}")};
   Assert.Equal(HttpMethod.Post,request.Method);Posts++;Assert.Equal("SYNTHETIC",request.Headers.GetValues("X-CSRF-Token").Single());
   using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var root=body.RootElement;
   Assert.Equal(ExpectedDirection,root.GetProperty("TipoPesagem").GetString());Assert.Equal("IMPUREZA",root.GetProperty("to_QualidPes")[0].GetProperty("Tipo").GetString());
   return new(HttpStatusCode.BadRequest){Content=new StringContent("{\"error\":\"SYNTHETIC_TERMINAL_NO_RETRY\"}")};
  }
 }
}
