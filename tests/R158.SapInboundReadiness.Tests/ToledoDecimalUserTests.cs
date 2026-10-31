using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Callback.Http;
using FugaTES.Integracao.SAP.Http;
namespace FugaTES.Testes;

public sealed class ToledoDecimalUserTests
{
 const string Input="""{"SistemaBalanca":"Toledo","DataPesagem":"31/10/2026 14:55:31","TipoPesagem":"S","IdPesagem":"00900020","UsuarioPesagem":"operador.sintetico","Centro":"3011","Material":"9000000","PesoBruto":"18000.0","PesoTara":"12000.0","PesoAntesDesconto":"6000.0","PesoAposDesconto":"6000.0","Observacao":"SYNTHETIC, OFFLINE ONLY","to_QualidPes":[{"Tipo":"UMIDADE","ValorAferido":"0.0"},{"Tipo":"PROTEINA_BRUTA","ValorAferido":"4.20"},{"Tipo":"","ValorAferido":""}],"to_Pesagem":[{"Indice":1,"NrIndiceTotal":1,"ContratoACM":"0099999920","IdParceiro":"90000020","ParceiroNome":"SYNTHETIC, PARTNER","IdParceiroEntrega":"90000020","PercentualDesdobro":"100","PesoAntesDesconto":"6000.0","PesoAposDesconto":"6000.0","ChaveAcessoNFe":""}]}""";
 static JsonObject Node()=>JsonNode.Parse(Input)!.AsObject();
 static ToledoSapPreparedPayload Prepare(string raw)=>ToledoSapODataPayload.Prepare(raw,new("America/Sao_Paulo"));
 static string Hash(string raw)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
 static JsonObject At(JsonObject n,string scope)=>scope=="item"?n["to_Pesagem"]![0]!.AsObject():scope=="quality"?n["to_QualidPes"]![0]!.AsObject():n;

 [Fact] public void Receipt_accepts_comma_decimals_without_changing_original_or_fingerprint()
 {
  var node=Node();foreach(var k in new[]{"PesoBruto","PesoTara","PesoAntesDesconto","PesoAposDesconto"})node[k]=node[k]!.ToString().Replace('.',',');
  var item=At(node,"item");item["PercentualDesdobro"]="100,00";item["PesoAntesDesconto"]="6000,0";item["PesoAposDesconto"]="6000,0";
  At(node,"quality")["ValorAferido"]="0,0";node["to_QualidPes"]![1]!["ValorAferido"]="4,20";
  var raw=node.ToJsonString();var fp=RegistrarPesagemToledoCasoDeUso.Fingerprint(raw);
  using var doc=JsonDocument.Parse(raw);var dto=ToledoPesagemCallbackDto.Deserializar(doc.RootElement);
  Assert.Equal(18000m,dto.PesoBruto);Assert.Equal(12000m,dto.PesoTara);Assert.Equal(6000m,dto.PesoAntesDesconto);Assert.Equal(6000m,dto.PesoAposDesconto);
  Assert.Equal(100m,dto.Pesagens[0].PercentualDesdobro);Assert.Equal(6000m,dto.Pesagens[0].PesoAposDesconto);
  Assert.Equal(raw,dto.DocumentoJson);Assert.Equal(raw,dto.Mapear().DocumentoJson);Assert.Equal(fp,RegistrarPesagemToledoCasoDeUso.Fingerprint(dto.DocumentoJson));
  var outbound=JsonNode.Parse(Prepare(dto.DocumentoJson).Json)!.AsObject();
  Assert.Equal("operador.sintet",outbound["UsuarioPesagem"]!.ToString());
  Assert.Equal("18000.0",outbound["PesoBruto"]!.ToString());Assert.Equal("100.00",At(outbound,"item")["PercentualDesdobro"]!.ToString());
  Assert.Equal("4.20",outbound["to_QualidPes"]![1]!["ValorAferido"]!.ToString());
  Assert.Equal("SYNTHETIC, OFFLINE ONLY",outbound["Observacao"]!.ToString());Assert.Equal("SYNTHETIC, PARTNER",At(outbound,"item")["ParceiroNome"]!.ToString());
  Assert.Equal("0000900020",outbound["IdPesagem"]!.ToString());Assert.Equal("S",outbound["TipoPesagem"]!.ToString());
 }
 [Theory]
 [InlineData("root","PesoBruto")][InlineData("root","PesoTara")][InlineData("root","PesoAntesDesconto")][InlineData("root","PesoAposDesconto")]
 [InlineData("item","PercentualDesdobro")][InlineData("item","PesoAntesDesconto")][InlineData("item","PesoAposDesconto")][InlineData("quality","ValorAferido")]
 public void Each_decimal_field_is_projected_with_dot_and_original_text_is_untouched(string scope,string field)
 {
  var node=Node();At(node,scope)[field]="12,340";var raw=node.ToJsonString();var output=JsonNode.Parse(Prepare(raw).Json)!.AsObject();
  Assert.Equal("12.340",At(output,scope)[field]!.GetValue<string>());Assert.Equal("12,340",At(node,scope)[field]!.GetValue<string>());
 }
 [Theory]
 [InlineData("","")][InlineData("short","short")][InlineData("123456789012345","123456789012345")]
 [InlineData("1234567890123456","123456789012345")][InlineData("andreia.strapasson","andreia.strapas")]
 [InlineData(" 1234567890123456"," 12345678901234")][InlineData("áéíóú123456789012345","áéíóú1234567890")]
 public void SAP_only_user_is_limited_to_first_fifteen_characters_without_trimming(string source,string expected)
 {
  var node=Node();node["UsuarioPesagem"]=source;node["UsuarioClassificacao"]="UNCHANGED_CLASSIFIER";var raw=node.ToJsonString();
  using var received=JsonDocument.Parse(raw);Assert.Equal(raw,ToledoPesagemCallbackDto.Deserializar(received.RootElement).DocumentoJson);
  var output=JsonNode.Parse(Prepare(raw).Json)!;Assert.Equal(expected,output["UsuarioPesagem"]!.GetValue<string>());
  Assert.Equal(source,node["UsuarioPesagem"]!.GetValue<string>());Assert.Equal("UNCHANGED_CLASSIFIER",output["UsuarioClassificacao"]!.GetValue<string>());
 }
 [Fact] public void User_cut_cannot_leave_half_a_UTF16_surrogate_pair()
 {
  var node=Node();node["UsuarioPesagem"]="12345678901234😀tail";var output=JsonNode.Parse(Prepare(node.ToJsonString()).Json)!;
  Assert.Equal("12345678901234",output["UsuarioPesagem"]!.GetValue<string>());
 }
 [Theory][InlineData("1.234,50")][InlineData("1,234.50")][InlineData("1,,2")][InlineData("1..2")][InlineData("NaN")][InlineData("1e3")][InlineData("-1,2")][InlineData("1,")]
 public void Ambiguous_or_invalid_weights_remain_rejected_at_both_boundaries(string invalid)
 {
  var node=Node();node["PesoBruto"]=invalid;using var json=JsonDocument.Parse(node.ToJsonString());
  Assert.Equal("PesoBruto",Assert.Throws<ArgumentException>(()=>ToledoPesagemCallbackDto.Deserializar(json.RootElement)).ParamName);
  Assert.Throws<ToledoSapPayloadException>(()=>Prepare(node.ToJsonString()));
 }
 [Theory][InlineData("1.234,50")][InlineData("1,234.50")][InlineData("1,,2")][InlineData("NaN")][InlineData("-1,2")]
 public void Invalid_quality_never_becomes_a_sendable_SAP_payload(string invalid)
 {var node=Node();At(node,"quality")["ValorAferido"]=invalid;Assert.Throws<ToledoSapPayloadException>(()=>Prepare(node.ToJsonString()));}
 [Fact] public void Comma_values_cannot_silently_round_beyond_decimal_precision()
 {
  var node=Node();node["PesoBruto"]="0,123456789012345678901234567891";
  using var json=JsonDocument.Parse(node.ToJsonString());Assert.Throws<ArgumentException>(()=>ToledoPesagemCallbackDto.Deserializar(json.RootElement));
  Assert.Throws<ToledoSapPayloadException>(()=>Prepare(node.ToJsonString()));
 }
 [Theory][InlineData("pt-BR")][InlineData("en-US")]
 public void Parsing_and_projection_are_independent_of_machine_culture(string culture)
 {
  var old=CultureInfo.CurrentCulture;try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(culture);
   var node=Node();node["PesoBruto"]="18000,25";using var doc=JsonDocument.Parse(node.ToJsonString());
   Assert.Equal(18000.25m,ToledoPesagemCallbackDto.Deserializar(doc.RootElement).PesoBruto);
   Assert.Equal("18000.25",JsonNode.Parse(Prepare(node.ToJsonString()).Json)!["PesoBruto"]!.GetValue<string>());
  }finally{CultureInfo.CurrentCulture=old;}
 }
 [Fact] public void Numeric_JSON_and_existing_dot_precision_remain_compatible()
 {
  var node=Node();node["PesoBruto"]=18000.25m;node["UsuarioPesagem"]="short";
  var output=JsonNode.Parse(Prepare(node.ToJsonString()).Json)!;
  Assert.Equal(JsonValueKind.Number,output["PesoBruto"]!.GetValueKind());Assert.Equal(18000.25m,output["PesoBruto"]!.GetValue<decimal>());
  Assert.Equal("4.20",output["to_QualidPes"]![1]!["ValorAferido"]!.GetValue<string>());
 }
 [Fact] public void New_normalized_payload_is_reproduced_only_with_its_exact_journal_hash()
 {
  var node=Node();node["PesoBruto"]="18000,0";var raw=node.ToJsonString();var sent=Prepare(raw);
  var found=ToledoSapODataPayload.ReproduceAudited(raw,new("America/Sao_Paulo"),sent.Sha256,ToledoSapPreparedPayload.Version);
  Assert.NotNull(found);Assert.Equal(sent.Json,found.Value.Payload.Json);
  Assert.Null(ToledoSapODataPayload.ReproduceAudited(raw,new("America/Sao_Paulo"),new string('A',64),ToledoSapPreparedPayload.Version));
 }
 [Fact] public void Historical_long_user_remains_readable_with_exact_pre_patch_hash_but_never_sent_that_way_again()
 {
  var node=Node();node["DataPesagem"]="/Date(1767225600000)/";var raw=node.ToJsonString();
  // Hand-derived former projection: ID10, unchanged long username, drop only the empty quality row.
  var historic=Node();historic["DataPesagem"]="/Date(1767225600000)/";historic["IdPesagem"]="0000900020";historic["to_QualidPes"]!.AsArray().RemoveAt(2);
  var expected=historic.ToJsonString();var hash=Hash(expected);
  var found=ToledoSapODataPayload.ReproduceAudited(raw,new(),hash,ToledoSapPreparedPayload.Version);
  Assert.NotNull(found);Assert.Equal(expected,found.Value.Payload.Json);Assert.Equal("operador.sintetico",JsonNode.Parse(found.Value.Payload.Json)!["UsuarioPesagem"]!.ToString());
  Assert.Equal("operador.sintet",JsonNode.Parse(Prepare(raw).Json)!["UsuarioPesagem"]!.ToString());
 }

 [Fact] public async Task Existing_gateway_audits_normalized_document_before_one_simulated_POST_and_never_changes_the_receipt()
 {
  var node=Node();node["PesoBruto"]="18000,0";At(node,"item")["PesoAntesDesconto"]="6000,0";At(node,"quality")["ValorAferido"]="0,0";
  var raw=node.ToJsonString();using var input=JsonDocument.Parse(raw);var dto=ToledoPesagemCallbackDto.Deserializar(input.RootElement);
  var document=ToledoSapPartnerFixture.Bind(new FugaTES.Aplicacao.Servicos.Recebimento.DocumentoToledoSap(Guid.NewGuid(),dto.Centro,dto.IdPesagem,dto.DocumentoJson,RegistrarPesagemToledoCasoDeUso.Fingerprint(raw),true,false));
  var wire=new OfflineWire();var executor=new SapODataHttpExecutor(new HttpClient(wire),new(){BaseUrl=new("https://sap.invalid"),SapClient="110",Timeout=TimeSpan.FromSeconds(2)},new OfflineAuth(),new System.Net.CookieContainer());
  var result=await new DocumentoToledoSapGateway(executor,new("America/Sao_Paulo")).EnviarOriginalAsync(document,(plan,_)=>
   {Assert.Equal(0,wire.Posts);Assert.True(plan.Valido);wire.AuditedHash=plan.RequestSha256;return Task.FromResult(true);},default);
  Assert.Equal(1,wire.Posts);Assert.Equal(400,result.HttpStatus);Assert.Equal(FugaTES.Aplicacao.Servicos.Recebimento.ResultadoToledoSapCodigo.RejeicaoHttp,result.Codigo);
  Assert.Equal(raw,document.DocumentoJson);Assert.NotNull(wire.AuditedHash);
 }
 sealed class OfflineAuth:FugaTES.Integracao.SAP.Autenticacao.IAutenticacaoSap
 {public Task AplicarAsync(HttpRequestMessage request,CancellationToken ct){request.Headers.Authorization=new("Basic","SYNTHETIC_OFFLINE");return Task.CompletedTask;}}
 sealed class OfflineWire:HttpMessageHandler
 {
  public int Posts;public string? AuditedHash;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {
   Assert.Equal("sap.invalid",request.RequestUri!.Host);
   if(request.Headers.Contains("X-CSRF-Token")&&request.Method==HttpMethod.Get)
   {var r=new HttpResponseMessage(System.Net.HttpStatusCode.OK);r.Headers.Add("X-CSRF-Token","SYNTHETIC");r.Headers.Add("Set-Cookie","SESSION=SYNTHETIC; Path=/");return r;}
   if(request.Method==HttpMethod.Get&&request.RequestUri.Query.Contains("$inlinecount="))
    return new(System.Net.HttpStatusCode.OK){Content=new StringContent("""<feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata"><id>https://sap.invalid/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem</id><m:count>0</m:count></feed>""")};
   if(request.Method==HttpMethod.Get)return new(System.Net.HttpStatusCode.NotFound){Content=new StringContent("{}")};
   Assert.Equal(HttpMethod.Post,request.Method);Assert.NotNull(AuditedHash);Assert.Equal("SYNTHETIC",request.Headers.GetValues("X-CSRF-Token").Single());
   var raw=await request.Content!.ReadAsStringAsync(ct);Assert.Equal(AuditedHash,Hash(raw));var body=JsonNode.Parse(raw)!;
   Assert.Equal("operador.sintet",body["UsuarioPesagem"]!.ToString());Assert.Equal("18000.0",body["PesoBruto"]!.ToString());
   Assert.Equal("6000.0",body["to_Pesagem"]![0]!["PesoAntesDesconto"]!.ToString());Assert.Equal("0.0",body["to_QualidPes"]![0]!["ValorAferido"]!.ToString());
   Posts++;return new(System.Net.HttpStatusCode.BadRequest){Content=new StringContent("{\"error\":\"SYNTHETIC_NO_RETRY\"}")};
  }
 }
}
