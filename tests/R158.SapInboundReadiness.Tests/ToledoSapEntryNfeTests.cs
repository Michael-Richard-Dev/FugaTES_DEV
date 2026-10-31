using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Callback.Http;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Http;
namespace FugaTES.Testes;
public sealed class ToledoSapEntryNfeTests
{
 const string SyntheticKey="00000000000000000000000000000000000000000001"; // Offline fixture only; never a business default.
 static string Input(string direction="E",string? key=SyntheticKey,string? party=null,bool omit=false)
 {
  var root=JsonNode.Parse(ToledoSapOData201Tests.Payload)!;root["TipoPesagem"]=direction;
  var item=root["to_Pesagem"]![0]!;if(!omit)item["ChaveAcessoNFe"]=key;if(party is not null)item["ParceiroCpfCnpj"]=party;
  return root.ToJsonString();
 }
 static ToledoSapPreparedPayload Prepare(string json)=>ToledoSapODataPayload.Prepare(json,new());
 [Theory][InlineData("E","11111111111")][InlineData("E","11111111111111")][InlineData("1","11111111111")][InlineData("1","11111111111111")]
 public void Entry_key_is_required_and_preserved_for_PF_and_PJ_without_a_person_type_exception(string direction,string party)
 {
  var raw=Input(direction,SyntheticKey,party);var hash=RegistrarPesagemToledoCasoDeUso.Fingerprint(raw);var p=Prepare(raw);using var body=JsonDocument.Parse(p.Json);
  Assert.Equal(SyntheticKey,body.RootElement.GetProperty("to_Pesagem")[0].GetProperty("ChaveAcessoNFe").GetString());Assert.Equal(party,body.RootElement.GetProperty("to_Pesagem")[0].GetProperty("ParceiroCpfCnpj").GetString());
  Assert.Equal("E",body.RootElement.GetProperty("TipoPesagem").GetString());Assert.Equal("0000900003",p.IdPesagem);Assert.Equal(hash,RegistrarPesagemToledoCasoDeUso.Fingerprint(raw));
  Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.Json))),p.Sha256);
 }
 [Theory][InlineData("E",null)][InlineData("E","")][InlineData("E"," ")][InlineData("1",null)][InlineData("1","")]
 public void Entry_with_no_key_is_blocked_instead_of_fabricating_a_key(string direction,string? key)
 {Assert.Equal("SAP_ENTRY_NFE_ACCESS_KEY_REQUIRED",Assert.Throws<ToledoSapPayloadException>(()=>Prepare(Input(direction,key))).Code);}
 [Theory][InlineData("E")][InlineData("1")]
 public void Missing_property_is_also_blocked_for_entry(string direction)
 {Assert.Equal("SAP_ENTRY_NFE_ACCESS_KEY_REQUIRED",Assert.Throws<ToledoSapPayloadException>(()=>Prepare(Input(direction,omit:true))).Code);}
 [Theory][InlineData("20318")][InlineData("000000000000000000000000000000000000000000001")][InlineData("0000000000000000000000000000000000000000000A")][InlineData(" 0000000000000000000000000000000000000000001")]
 public void Invalid_key_cannot_be_padded_trimmed_or_replaced_by_a_receipt_id(string key)
 {Assert.Equal("SAP_NFE_ACCESS_KEY_INVALID",Assert.Throws<ToledoSapPayloadException>(()=>Prepare(Input(key:key))).Code);}
 [Fact] public void Every_entry_item_must_have_its_own_key()
 {
  var root=JsonNode.Parse(Input())!;var first=root["to_Pesagem"]![0]!;first["NrIndiceTotal"]=2;var second=first.DeepClone();second["Indice"]=2;second["ChaveAcessoNFe"]="";root["to_Pesagem"]!.AsArray().Add(second);
  Assert.Equal("SAP_ENTRY_NFE_ACCESS_KEY_REQUIRED",Assert.Throws<ToledoSapPayloadException>(()=>Prepare(root.ToJsonString())).Code);
  var secondKey=new string('2',44);second["ChaveAcessoNFe"]=secondKey;using var j=JsonDocument.Parse(Prepare(root.ToJsonString()).Json);
  Assert.Equal(SyntheticKey,j.RootElement.GetProperty("to_Pesagem")[0].GetProperty("ChaveAcessoNFe").GetString());Assert.Equal(secondKey,j.RootElement.GetProperty("to_Pesagem")[1].GetProperty("ChaveAcessoNFe").GetString());
 }
 [Theory][InlineData("S")][InlineData("2")]
 public void Exit_does_not_acquire_a_new_mandatory_key_policy(string direction)
 {using var j=JsonDocument.Parse(Prepare(Input(direction,omit:true)).Json);Assert.False(j.RootElement.GetProperty("to_Pesagem")[0].TryGetProperty("ChaveAcessoNFe",out _));}
 [Fact] public void Receiver_preserves_the_original_missing_key_document_for_durable_receipt()
 {var root=JsonNode.Parse(Input(key:""))!;root["DataPesagem"]="10/30/2026 9:13:34 AM";root["Material"]="9000000";var item=root["to_Pesagem"]![0]!;item["PercentualDesdobro"]="100";item["PesoAntesDesconto"]="20000.0";item["PesoAposDesconto"]="20000.0";var raw=root.ToJsonString();var dto=ToledoPesagemCallbackDto.Deserializar(JsonDocument.Parse(raw).RootElement);Assert.Equal(raw,dto.DocumentoJson);Assert.Equal(raw,dto.Mapear().DocumentoJson);}
 [Fact] public void Atom_confirmation_must_echo_the_actual_entry_key()
 {
  var p=Prepare(Input());var uri=new Uri("https://sap.invalid/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem?sap-client=110");
  var xml=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","ToledoSapCreated.SYNTHETIC.xml")).Replace(">S<",">E<");
  Assert.Null(ToledoSapAtomReceipt.Validate(xml,p,uri));var filled=xml.Replace("<ns2:ChaveAcessoNFe />","<ns2:ChaveAcessoNFe>"+SyntheticKey+"</ns2:ChaveAcessoNFe>");Assert.NotNull(ToledoSapAtomReceipt.Validate(filled,p,uri));
  Assert.Null(ToledoSapAtomReceipt.Validate(filled.Replace(SyntheticKey,new string('3',44)),p,uri));
 }
 [Fact] public async Task Missing_entry_key_stops_before_any_SAP_lookup_CSRF_or_POST()
 {
  var wire=new Wire();var raw=Input(key:"");var audited=false;
  var r=await Gateway(wire).EnviarOriginalAsync(Document(raw),(plan,_)=>{audited=true;return Task.FromResult(true);},default);
  Assert.Equal(ResultadoToledoSapCodigo.PreEnvioBloqueado,r.Codigo);Assert.Equal("SAP_ENTRY_NFE_ACCESS_KEY_REQUIRED",r.DetailCode);Assert.Equal(0,wire.Calls);Assert.False(audited);Assert.False(r.PostIniciado);
 }
 [Theory][InlineData("11111111111")][InlineData("11111111111111")]
 public async Task Valid_entry_key_reaches_the_existing_transport_once_after_durable_guard(string party)
 {
  var wire=new Wire();var raw=Input(party:party);PlanoEnvioToledoSap? audited=null;
  var r=await Gateway(wire).EnviarOriginalAsync(Document(raw),(plan,_)=>{Assert.Equal(0,wire.Posts);audited=plan;return Task.FromResult(true);},default);
  Assert.Equal(1,wire.Posts);Assert.Equal(400,r.HttpStatus);Assert.NotNull(audited);Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(wire.Body))),audited.RequestSha256);
  using var body=JsonDocument.Parse(wire.Body);Assert.Equal(SyntheticKey,body.RootElement.GetProperty("to_Pesagem")[0].GetProperty("ChaveAcessoNFe").GetString());
 }
 static DocumentoToledoSap Document(string raw)=>ToledoSapPartnerFixture.Bind(new(Guid.NewGuid(),"3011","900003",raw,RegistrarPesagemToledoCasoDeUso.Fingerprint(raw),true,false));
 static DocumentoToledoSapGateway Gateway(Wire w)=>new(new(new HttpClient(w),new(){BaseUrl=new("https://sap.invalid"),SapClient="110",Timeout=TimeSpan.FromSeconds(2)},new Auth(),new CookieContainer()));
 sealed class Auth:IAutenticacaoSap{public Task AplicarAsync(HttpRequestMessage r,CancellationToken ct){r.Headers.Authorization=new("Basic","SYNTHETIC_OFFLINE_ONLY");return Task.CompletedTask;}}
 sealed class Wire:HttpMessageHandler
 {
  public int Calls,Posts;public string Body="";
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
  {
   Calls++;Assert.Equal("sap.invalid",r.RequestUri!.Host);
   if(r.Method==HttpMethod.Get&&r.Headers.Contains("X-CSRF-Token")){var response=new HttpResponseMessage(HttpStatusCode.OK);response.Headers.Add("X-CSRF-Token","SYNTHETIC");response.Headers.Add("Set-Cookie","SESSION=SYNTHETIC; Path=/");return response;}
   if(r.Method==HttpMethod.Get&&r.RequestUri.Query.Contains("$inlinecount="))return new(HttpStatusCode.OK){Content=new StringContent("""<feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata"><id>https://sap.invalid/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem</id><m:count>0</m:count></feed>""")};
   if(r.Method==HttpMethod.Get)return new(HttpStatusCode.NotFound){Content=new StringContent("{}")};
   Assert.Equal(HttpMethod.Post,r.Method);Posts++;Body=await r.Content!.ReadAsStringAsync(ct);return new(HttpStatusCode.BadRequest){Content=new StringContent("SYNTHETIC_TERMINAL_ERROR_NO_RETRY")};
  }
 }
}
