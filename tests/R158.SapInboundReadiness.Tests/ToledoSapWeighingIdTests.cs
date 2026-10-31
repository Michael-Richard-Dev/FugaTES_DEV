using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Http;
namespace FugaTES.Testes;

public sealed class ToledoSapWeighingIdTests
{
 static string Input(string id)=>ToledoSapOData201Tests.Payload.Replace("\"IdPesagem\":900003","\"IdPesagem\":\""+id+"\"");
 static ToledoSapPreparedPayload Prepare(string input)=>ToledoSapODataPayload.Prepare(input,new());
 static string Xml()=>File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","ToledoSapCreated.SYNTHETIC.xml"));
 [Theory][InlineData("0019689","0000019689")][InlineData("19689","0000019689")][InlineData("0000019689","0000019689")][InlineData("1","0000000001")][InlineData("0","0000000000")][InlineData("1234567890","1234567890")]
 public void Outbound_id_is_exactly_ten_digits_without_changing_the_original(string source,string expected)
 {
  var original=Input(source);var fingerprint=RegistrarPesagemToledoCasoDeUso.Fingerprint(original);var p=Prepare(original);
  using var j=JsonDocument.Parse(p.Json);Assert.Equal(expected,j.RootElement.GetProperty("IdPesagem").GetString());Assert.Equal(expected,p.IdPesagem);Assert.Equal(10,p.IdPesagem.Length);
  Assert.Equal(source,JsonDocument.Parse(original).RootElement.GetProperty("IdPesagem").GetString());Assert.Equal(fingerprint,RegistrarPesagemToledoCasoDeUso.Fingerprint(original));
  Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.Json))),p.Sha256);
  Assert.Equal("S",j.RootElement.GetProperty("TipoPesagem").GetString());Assert.Equal("0099999903",j.RootElement.GetProperty("to_Pesagem")[0].GetProperty("ContratoACM").GetString());
 }
 [Fact] public void Numeric_input_becomes_a_ten_character_JSON_string()
 {var p=Prepare(ToledoSapOData201Tests.Payload);using var j=JsonDocument.Parse(p.Json);Assert.Equal("0000900003",p.IdPesagem);Assert.Equal(JsonValueKind.String,j.RootElement.GetProperty("IdPesagem").ValueKind);}
 [Theory][InlineData("12345678901")][InlineData("00000019689")]
 public void More_than_ten_digits_are_blocked_without_truncation(string id)
 {Assert.Equal("SAP_WEIGHING_ID_LENGTH_INVALID",Assert.Throws<ToledoSapPayloadException>(()=>Prepare(Input(id))).Code);}
 [Theory][InlineData("")][InlineData("-1")][InlineData("12A")][InlineData("1.0")][InlineData(" 123")]
 public void Invalid_identifiers_are_never_repaired_into_a_different_weighing(string id)
 {Assert.Throws<ToledoSapPayloadException>(()=>Prepare(Input(id)));}
 [Fact] public void Only_the_ten_character_Atom_entity_and_child_id_can_confirm_the_new_request()
 {
  var p=Prepare(Input("00900003"));var uri=new Uri("https://sap.invalid/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem?sap-client=110");
  Assert.NotNull(ToledoSapAtomReceipt.Validate(Xml(),p,uri));Assert.Null(ToledoSapAtomReceipt.Validate(Xml().Replace("0000900003","00900003"),p,uri));
 }
 [Theory][InlineData("00900003")][InlineData("900003")][InlineData("0900003")][InlineData("000900003")]
 public async Task Existing_original_or_unpadded_alias_blocks_POST_and_cannot_be_claimed_as_new_confirmation(string legacyId)
 {
  var raw=Input("00900003");var wire=new Wire(legacyId);var calls=0;
  var result=await Gateway(wire).EnviarOriginalAsync(Document(raw,"00900003"),(_,_)=>{calls++;return Task.FromResult(true);},default);
  Assert.Equal(ResultadoToledoSapCodigo.RegistroSapExistenteDivergente,result.Codigo);Assert.False(result.Confirmado);Assert.Equal(0,wire.Posts);Assert.Equal(0,calls);
  Assert.Equal(legacyId,wire.Ids.Last());Assert.Equal("0000900003",wire.Ids.First());Assert.Equal(raw,Document(raw,"00900003").DocumentoJson);
 }
 [Fact] public async Task New_id_uses_the_same_canonical_identity_for_lookup_audit_POST_and_Atom_confirmation()
 {
  var raw=Input("00900003");var wire=new Wire(null);PlanoEnvioToledoSap? seen=null;
  var result=await Gateway(wire).EnviarOriginalAsync(Document(raw,"00900003"),(plan,_)=>{Assert.Equal(0,wire.Posts);seen=plan;return Task.FromResult(true);},default);
  Assert.True(result.Confirmado);Assert.Equal(1,wire.Posts);Assert.Equal(new[]{"0000900003","00900003","900003","0900003","000900003"},wire.Ids);
  using var body=JsonDocument.Parse(wire.Body);Assert.Equal("0000900003",body.RootElement.GetProperty("IdPesagem").GetString());Assert.Equal("S",body.RootElement.GetProperty("TipoPesagem").GetString());
  Assert.NotNull(seen);Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(wire.Body))),seen.RequestSha256);Assert.Equal(seen.RequestSha256,result.RequestSha256);
 }
 [Fact] public async Task Changed_Guardian_zero_width_cannot_recreate_an_existing_intermediate_legacy_identity()
 {
  var raw=Input("19689");var wire=new Wire("0019689");var audited=false;
  var result=await Gateway(wire).EnviarOriginalAsync(Document(raw,"19689"),(_,_)=>{audited=true;return Task.FromResult(true);},default);
  Assert.Equal(ResultadoToledoSapCodigo.RegistroSapExistenteDivergente,result.Codigo);Assert.Equal(0,wire.Posts);Assert.False(audited);Assert.Contains("0019689",wire.Ids);Assert.InRange(wire.Ids.Count,1,10);
 }
 [Fact] public async Task Invalid_length_is_blocked_before_any_transport_or_authorization()
 {
  var raw=Input("12345678901");var wire=new Wire(null);var called=false;
  var r=await Gateway(wire).EnviarOriginalAsync(Document(raw,"12345678901"),(_,_)=>{called=true;return Task.FromResult(true);},default);
  Assert.Equal("SAP_WEIGHING_ID_LENGTH_INVALID",r.DetailCode);Assert.Empty(wire.Ids);Assert.Equal(0,wire.Posts);Assert.False(called);
 }
 static DocumentoToledoSap Document(string raw,string id)=>ToledoSapPartnerFixture.Bind(new(Guid.NewGuid(),"3011",id,raw,RegistrarPesagemToledoCasoDeUso.Fingerprint(raw),true,false));
 static DocumentoToledoSapGateway Gateway(Wire wire)=>new(new(new HttpClient(wire),new(){BaseUrl=new("https://sap.invalid"),SapClient="110",Timeout=TimeSpan.FromSeconds(2)},new Auth(),new CookieContainer()));
 sealed class Auth:IAutenticacaoSap{public Task AplicarAsync(HttpRequestMessage r,CancellationToken ct){r.Headers.Authorization=new("Basic","SYNTHETIC_OFFLINE_ONLY");return Task.CompletedTask;}}
 sealed class Wire(string? legacyId):HttpMessageHandler
 {
  public List<string> Ids=[];public int Posts;public string Body="";
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {
   Assert.Equal("sap.invalid",request.RequestUri!.Host);
   if(request.Method==HttpMethod.Get&&request.Headers.Contains("X-CSRF-Token")){var r=new HttpResponseMessage(HttpStatusCode.OK);r.Headers.Add("X-CSRF-Token","SYNTHETIC");r.Headers.Add("Set-Cookie","SESSION=SYNTHETIC; Path=/");return r;}
   if(request.Method==HttpMethod.Get&&request.RequestUri.Query.Contains("$inlinecount="))return new(HttpStatusCode.OK){Content=new StringContent("""<feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata"><id>https://sap.invalid/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem</id><m:count>0</m:count></feed>""")};
   if(request.Method==HttpMethod.Get){var match=System.Text.RegularExpressions.Regex.Match(Uri.UnescapeDataString(request.RequestUri.AbsolutePath),"IdPesagem='([0-9]+)'");Assert.True(match.Success);var id=match.Groups[1].Value;Ids.Add(id);return id==legacyId?new(HttpStatusCode.OK){Content=new StringContent(Xml().Replace("0000900003",legacyId!))}:new(HttpStatusCode.NotFound){Content=new StringContent("{}")};}
   Assert.Equal(HttpMethod.Post,request.Method);Posts++;Body=await request.Content!.ReadAsStringAsync(ct);return new(HttpStatusCode.Created){Content=new StringContent(Xml())};
  }
 }
}
