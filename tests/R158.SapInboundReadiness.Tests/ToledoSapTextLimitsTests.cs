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
public sealed class ToledoSapTextLimitsTests
{
 static JsonObject Input()=>JsonNode.Parse(ToledoSapOData201Tests.Payload)!.AsObject();
 static JsonObject At(JsonObject root,string field)=>field=="NomeMotorista"?root:root["to_Pesagem"]![0]!.AsObject();
 static JsonObject Prepare(string raw)=>JsonNode.Parse(ToledoSapODataPayload.Prepare(raw,new("America/Sao_Paulo")).Json)!.AsObject();
 static string Hash(string raw)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
 [Theory][InlineData("NomeMotorista",20)][InlineData("ParceiroNome",35)][InlineData("ParceiroEntregaNome",50)]
 public void Overlong_name_is_cut_only_in_outbound_and_original_receipt_is_preserved(string field,int limit)
 {var root=Input();root["DataPesagem"]="31/10/2026 14:55:31";root["IdPesagem"]="900003";root["to_Pesagem"]![0]!["PercentualDesdobro"]="100";root["to_Pesagem"]![0]!["PesoAntesDesconto"]="20000.0";root["to_Pesagem"]![0]!["PesoAposDesconto"]="20000.0";var text=new string('Á',limit)+" EXCESSO";At(root,field)[field]=text;var raw=root.ToJsonString();using var json=JsonDocument.Parse(raw);var dto=ToledoPesagemCallbackDto.Deserializar(json.RootElement);var outbound=Prepare(dto.DocumentoJson);Assert.Equal(new string('Á',limit),At(outbound,field)[field]!.ToString());Assert.Equal(raw,dto.DocumentoJson);Assert.Equal(text,At(root,field)[field]!.ToString());}
 [Theory][InlineData("NomeMotorista",20,19)][InlineData("NomeMotorista",20,20)][InlineData("ParceiroNome",35,34)][InlineData("ParceiroNome",35,35)][InlineData("ParceiroEntregaNome",50,49)][InlineData("ParceiroEntregaNome",50,50)]
 public void Within_limit_names_are_not_changed(string field,int limit,int length)
 {Assert.True(length<=limit);var root=Input();var text=" Á  "+new string('x',length-4);At(root,field)[field]=text;Assert.Equal(text,At(Prepare(root.ToJsonString()),field)[field]!.ToString());}
 [Fact]public void Absent_null_and_empty_optional_names_are_preserved()
 {var root=Input();root.Remove("NomeMotorista");var item=At(root,"ParceiroNome");item["ParceiroNome"]=null;item["ParceiroEntregaNome"]="";var output=Prepare(root.ToJsonString());Assert.False(output.ContainsKey("NomeMotorista"));Assert.Null(At(output,"ParceiroNome")["ParceiroNome"]);Assert.Equal("",At(output,"ParceiroEntregaNome")["ParceiroEntregaNome"]!.ToString());}
 [Theory][InlineData("NomeMotorista",20)][InlineData("ParceiroNome",35)][InlineData("ParceiroEntregaNome",50)]
 public void Truncation_never_splits_a_UTF16_surrogate_pair(string field,int limit)
 {var root=Input();At(root,field)[field]=new string('x',limit-1)+"🚚EXTRA";var value=At(Prepare(root.ToJsonString()),field)[field]!.ToString();Assert.Equal(new string('x',limit-1),value);Assert.DoesNotContain("�",value);Assert.True(value.Length<=limit);}
 [Fact]public void All_items_are_limited_independently_without_changing_identifiers_or_values()
 {var root=Input();var items=root["to_Pesagem"]!.AsArray();items[0]!["NrIndiceTotal"]=2;var second=items[0]!.DeepClone();second["Indice"]=2;items.Add(second);foreach(var i in items){i!["ParceiroNome"]=new string('P',60);i["ParceiroEntregaNome"]=new string('E',70);}var output=Prepare(root.ToJsonString());foreach(var i in output["to_Pesagem"]!.AsArray()){Assert.Equal(35,i!["ParceiroNome"]!.ToString().Length);Assert.Equal(50,i["ParceiroEntregaNome"]!.ToString().Length);Assert.Equal("90000003",i["IdParceiro"]!.ToString());Assert.Equal("0099999903",i["ContratoACM"]!.ToString());}}
 [Fact]public void SAP_supplier_name_is_limited_after_frozen_delivery_enrichment()
 {var root=Input();root["NomeMotorista"]=new string('M',40);At(root,"ParceiroNome")["ParceiroNome"]=new string('P',70);var raw=root.ToJsonString();var doc=ToledoSapPartnerFixture.Bind(new(Guid.NewGuid(),"3011","900003",raw,RegistrarPesagemToledoCasoDeUso.Fingerprint(raw),true,false));var proof=doc.DeliveryPartners!;var name=new string('F',70);proof=proof with{Items=[proof.Items[0] with{Name=name}]};doc=doc with{DeliveryPartners=proof};var output=JsonNode.Parse(ToledoSapODataPayload.Prepare(doc,new()).Json)!;Assert.Equal(50,output["to_Pesagem"]![0]!["ParceiroEntregaNome"]!.ToString().Length);Assert.Equal(name,proof.Items[0].Name);Assert.Equal(raw,doc.DocumentoJson);}
 [Fact]public void Default_delivery_uses_full_original_owner_name_before_applying_distinct_SAP_limits()
 {var root=Input();var item=At(root,"ParceiroNome");item["IdParceiroEntrega"]="0";var name=new string('P',100);item["ParceiroNome"]=name;var raw=root.ToJsonString();var doc=ToledoSapPartnerFixture.Bind(new(Guid.NewGuid(),"3011","900003",raw,RegistrarPesagemToledoCasoDeUso.Fingerprint(raw),true,false));var output=JsonNode.Parse(ToledoSapODataPayload.Prepare(doc,new()).Json)!;Assert.Equal(new string('P',35),output["to_Pesagem"]![0]!["ParceiroNome"]!.ToString());Assert.Equal(new string('P',50),output["to_Pesagem"]![0]!["ParceiroEntregaNome"]!.ToString());Assert.Equal(name,doc.DeliveryPartners!.Items[0].Name);}
 [Fact]public void Historical_audited_payload_is_reproduced_exactly_without_rewriting_it_as_current_send()
 {var root=Input();root["NomeMotorista"]=new string('M',30);At(root,"ParceiroNome")["ParceiroNome"]=new string('P',40);At(root,"ParceiroEntregaNome")["ParceiroEntregaNome"]=new string('E',60);var raw=root.ToJsonString();var old=root.DeepClone();old["IdPesagem"]="0000900003";old["TipoPesagem"]="S";var expected=old.ToJsonString();var reproduced=ToledoSapODataPayload.ReproduceAudited(raw,new(),Hash(expected),ToledoSapPreparedPayload.Version);Assert.NotNull(reproduced);Assert.Equal(expected,reproduced.Value.Payload.Json);Assert.Equal("BeforeNameTruncation",reproduced.Value.Projection);Assert.NotEqual(expected,ToledoSapODataPayload.Prepare(raw,new()).Json);}
 [Fact]public async Task Existing_gateway_posts_only_bounded_names_after_hash_audit_using_simulated_transport()
 {var root=Input();root["NomeMotorista"]=new string('M',30);At(root,"ParceiroNome")["ParceiroNome"]=new string('P',40);var raw=root.ToJsonString();var doc=ToledoSapPartnerFixture.Bind(new(Guid.NewGuid(),"3011","900003",raw,RegistrarPesagemToledoCasoDeUso.Fingerprint(raw),true,false));var proof=doc.DeliveryPartners!;doc=doc with{DeliveryPartners=proof with{Items=[proof.Items[0] with{Name=new string('E',70)}]}};var wire=new Wire();var executor=new SapODataHttpExecutor(new HttpClient(wire),new(){BaseUrl=new("https://sap.invalid"),SapClient="110",Timeout=TimeSpan.FromSeconds(2)},new Auth(),new CookieContainer());var result=await new DocumentoToledoSapGateway(executor,new()).EnviarOriginalAsync(doc,(plan,_)=>{wire.Hash=plan.RequestSha256;return Task.FromResult(true);},default);Assert.Equal(1,wire.Posts);Assert.Equal(400,result.HttpStatus);Assert.Equal(ResultadoToledoSapCodigo.RejeicaoHttp,result.Codigo);Assert.Equal(raw,doc.DocumentoJson);}
 sealed class Auth:IAutenticacaoSap{public Task AplicarAsync(HttpRequestMessage r,CancellationToken ct){r.Headers.Authorization=new("Basic","SYNTHETIC_ONLY");return Task.CompletedTask;}}
 sealed class Wire:HttpMessageHandler
 {
  public string? Hash;public int Posts;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
  {
   Assert.Equal("sap.invalid",r.RequestUri!.Host);
   if(r.Method==HttpMethod.Get&&r.Headers.Contains("X-CSRF-Token")){var response=new HttpResponseMessage(HttpStatusCode.OK);response.Headers.Add("X-CSRF-Token","SYNTHETIC");response.Headers.Add("Set-Cookie","SESSION=SYNTHETIC; Path=/");return response;}
   if(r.Method==HttpMethod.Get&&r.RequestUri.Query.Contains("$inlinecount="))return new(HttpStatusCode.OK){Content=new StringContent("""<feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata"><id>https://sap.invalid/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem</id><m:count>0</m:count></feed>""")};
   if(r.Method==HttpMethod.Get)return new(HttpStatusCode.NotFound){Content=new StringContent("{}")};
   Assert.Equal(HttpMethod.Post,r.Method);var raw=await r.Content!.ReadAsStringAsync(ct);Assert.Equal(Hash,ToledoSapTextLimitsTests.Hash(raw));var n=JsonNode.Parse(raw)!;Assert.Equal(20,n["NomeMotorista"]!.ToString().Length);Assert.Equal(35,n["to_Pesagem"]![0]!["ParceiroNome"]!.ToString().Length);Assert.Equal(50,n["to_Pesagem"]![0]!["ParceiroEntregaNome"]!.ToString().Length);Posts++;return new(HttpStatusCode.BadRequest){Content=new StringContent("{}")} ;
  }
 }
}
