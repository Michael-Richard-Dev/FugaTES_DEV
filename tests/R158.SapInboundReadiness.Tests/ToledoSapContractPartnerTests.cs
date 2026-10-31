using System.Net;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Http;
namespace FugaTES.Testes;
public sealed class ToledoSapContractPartnerTests
{
 [Fact] public async Task Missing_authoritative_contract_partner_blocks_before_any_SAP_request()
 {
  var raw=ToledoSapOData201Tests.Payload;var document=new DocumentoToledoSap(Guid.NewGuid(),"3011","900003",raw,RegistrarPesagemToledoCasoDeUso.Fingerprint(raw),true,false);
  document=document with{DeliveryPartners=ToledoDeliveryPartnerTests.Proof(raw)};var wire=new Wire();using var http=new HttpClient(wire);
  var gateway=new DocumentoToledoSapGateway(new(http,new(){BaseUrl=new("https://sap.invalid"),SapClient="110",Timeout=TimeSpan.FromSeconds(1)},new Auth(),new CookieContainer()));
  var result=await gateway.EnviarOriginalAsync(document,(_,_)=>Task.FromResult(true),default);
  Assert.Equal(ResultadoToledoSapCodigo.PreEnvioBloqueado,result.Codigo);Assert.Equal("SAP_CONTRACT_PARTNER_UNPROVEN",result.DetailCode);Assert.Equal(0,wire.Calls);Assert.False(result.PostIniciado);
 }

 static DocumentoToledoSap Document(string? partner=null,string? delivery="90000011")
 {
  var raw=System.Text.Json.Nodes.JsonNode.Parse(ToledoSapOData201Tests.Payload)!;raw["to_Pesagem"]![0]!["IdParceiro"]=partner??"90000011";raw["to_Pesagem"]![0]!["IdParceiroEntrega"]=delivery;
  var text=raw.ToJsonString();return new(Guid.NewGuid(),"3011","900003",text,RegistrarPesagemToledoCasoDeUso.Fingerprint(text),true,false)
   {RecebidoEm=ToledoSapPartnerFixture.Received,ContratosSap=[ToledoSapPartnerFixture.Source()]};
 }
 static ToledoSapPreparedPayload Prepare(DocumentoToledoSap d)=>ToledoSapODataPayload.Prepare(d with{DeliveryPartners=ToledoDeliveryPartnerTests.Proof(d.DocumentoJson)},new());
 [Theory][InlineData("90000011","90000011")][InlineData("90000003","90000011")][InlineData("0000000011","0000000012")]
 public void Contract_owner_replaces_only_IdParceiro_and_Guardian_delivery_stays_exact(string guardian,string delivery)
 {
  var d=Document(guardian,delivery);var original=d.DocumentoJson;var p=Prepare(d);using var json=System.Text.Json.JsonDocument.Parse(p.Json);var item=json.RootElement.GetProperty("to_Pesagem")[0];
  Assert.Equal("90000003",item.GetProperty("IdParceiro").GetString());Assert.Equal(delivery,item.GetProperty("IdParceiroEntrega").GetString());Assert.Equal(original,d.DocumentoJson);Assert.Single(p.ParceirosContrato);
  Assert.Equal(d.ContratosSap[0].IntegracaoId,p.ParceirosContrato[0].IntegracaoContratoId);
 }
 [Fact] public void Contract_and_material_zero_widths_do_not_replace_the_original_outbound_contract()
 {
  var d=Document();var node=System.Text.Json.Nodes.JsonNode.Parse(d.DocumentoJson)!;node["to_Pesagem"]![0]!["ContratoACM"]="0000001168";node["Material"]="7000000";
  var raw=node.ToJsonString();d=d with{DocumentoJson=raw,FingerprintOriginal=RegistrarPesagemToledoCasoDeUso.Fingerprint(raw),ContratosSap=[ToledoSapPartnerFixture.Source("1168","90000003","000000000007000000")]};
  using var json=System.Text.Json.JsonDocument.Parse(Prepare(d).Json);Assert.Equal("0000001168",json.RootElement.GetProperty("to_Pesagem")[0].GetProperty("ContratoACM").GetString());Assert.Equal("7000000",json.RootElement.GetProperty("Material").GetString());
 }
 [Fact] public void Multiple_weighing_items_get_their_own_contract_owner_without_using_Indice_as_SAP_Item()
 {
  var d=Document();var node=System.Text.Json.Nodes.JsonNode.Parse(d.DocumentoJson)!;var first=node["to_Pesagem"]![0]!;first["NrIndiceTotal"]=2;
  var second=first.DeepClone();second["Indice"]=2;second["ContratoACM"]="0099999904";second["IdParceiroEntrega"]="90000012";node["to_Pesagem"]!.AsArray().Add(second);
  var raw=node.ToJsonString();d=d with{DocumentoJson=raw,FingerprintOriginal=RegistrarPesagemToledoCasoDeUso.Fingerprint(raw),ContratosSap=[ToledoSapPartnerFixture.Source(item:"10"),ToledoSapPartnerFixture.Source("0099999904","90000004",item:"20")]};
  using var j=System.Text.Json.JsonDocument.Parse(Prepare(d).Json);var items=j.RootElement.GetProperty("to_Pesagem");Assert.Equal("90000003",items[0].GetProperty("IdParceiro").GetString());Assert.Equal("90000004",items[1].GetProperty("IdParceiro").GetString());Assert.Equal("90000012",items[1].GetProperty("IdParceiroEntrega").GetString());
 }
 [Fact] public void Latest_version_per_SAP_item_at_receipt_time_is_pinned_and_future_changes_are_excluded()
 {
  var d=Document() with{ContratosSap=[ToledoSapPartnerFixture.Source(partner:"90000001",version:1,item:"10"),ToledoSapPartnerFixture.Source(partner:"90000002",version:2,item:"10"),ToledoSapPartnerFixture.Source(partner:"90000003",version:3,item:"10",at:ToledoSapPartnerFixture.Received.AddMinutes(1))]};
  var p=Prepare(d);Assert.Equal("90000002",p.ParceirosContrato.Single().IdParceiroSap);Assert.Equal(2,p.ParceirosContrato.Single().VersaoContrato);
 }
 [Theory][InlineData("material")][InlineData("center")]
 public void Newer_contract_scope_never_falls_back_to_a_superseded_matching_version(string changed)
 {
  var older=ToledoSapPartnerFixture.Source(version:1,item:"10");
  var newer=changed=="material"?ToledoSapPartnerFixture.Source(version:2,item:"10",material:"9000001"):ToledoSapPartnerFixture.Source(version:2,item:"10",center:"9999");
  var d=Document() with{ContratosSap=[older,newer]};Assert.Equal("SAP_CONTRACT_PARTNER_UNPROVEN",Assert.Throws<ToledoSapContractPartnerException>(()=>Prepare(d)).Code);
 }
 [Fact] public void Several_SAP_items_with_the_same_owner_are_unambiguous_but_different_owners_are_blocked()
 {
  var d=Document() with{ContratosSap=[ToledoSapPartnerFixture.Source(item:"10"),ToledoSapPartnerFixture.Source(item:"20")]};Assert.Equal("90000003",Prepare(d).ParceirosContrato.Single().IdParceiroSap);
  d=d with{ContratosSap=[ToledoSapPartnerFixture.Source(item:"10"),ToledoSapPartnerFixture.Source(partner:"90000004",item:"20")]};Assert.Equal("SAP_CONTRACT_PARTNER_AMBIGUOUS",Assert.Throws<ToledoSapContractPartnerException>(()=>Prepare(d)).Code);
 }
 [Theory][InlineData("hash")][InlineData("integrationHash")][InlineData("origin")][InlineData("operation")][InlineData("businessKey")][InlineData("payload")][InlineData("center")][InlineData("material")][InlineData("future")]
 public void Unproven_or_unrelated_sources_never_fall_back_to_Guardian_partner(string fault)
 {
  var d=Document();var s=d.ContratosSap[0];s=fault switch{
   "hash"=>s with{FingerprintContrato=new string('A',64)},"integrationHash"=>s with{FingerprintIntegracao=new string('B',64)},"origin"=>s with{Origem="GUARDIAN"},"operation"=>s with{Operacao="OTHER"},"businessKey"=>s with{ChaveNegocio="OTHER"},"payload"=>s with{PayloadContratoJson="{}"},
   "center"=>ToledoSapPartnerFixture.Source(center:"9999"),"material"=>ToledoSapPartnerFixture.Source(material:"9000001"),_=>s with{CriadoEm=d.RecebidoEm.AddSeconds(1)}};
  d=d with{ContratosSap=[s]};Assert.Throws<ToledoSapContractPartnerException>(()=>Prepare(d));
 }
 [Theory][InlineData(null)][InlineData("")][InlineData("0")]
 public void Missing_delivery_partner_uses_original_Guardian_owner_not_contract_owner(string? delivery)
 {var d=Document(delivery:delivery);using var json=System.Text.Json.JsonDocument.Parse(Prepare(d).Json);var item=json.RootElement.GetProperty("to_Pesagem")[0];Assert.Equal("90000011",item.GetProperty("IdParceiroEntrega").GetString());Assert.Equal("90000003",item.GetProperty("IdParceiro").GetString());}
 [Fact] public void Invalid_delivery_partner_is_rejected_before_projection()
 {Assert.Throws<ToledoDeliveryPartnerException>(()=>Prepare(Document(delivery:"not-a-partner")));}
 [Fact] public async Task Strict_gateway_audits_the_contract_binding_then_sends_only_the_corrected_owner()
 {
  var d=Document();d=d with{DeliveryPartners=ToledoDeliveryPartnerTests.Proof(d.DocumentoJson)};var wire=new PartnerWire();using var http=new HttpClient(wire);PlanoEnvioToledoSap? audit=null;
  var gateway=new DocumentoToledoSapGateway(new(http,new(){BaseUrl=new("https://sap.invalid"),SapClient="110",Timeout=TimeSpan.FromSeconds(2)},new Auth(),new CookieContainer()));
  var result=await gateway.EnviarOriginalAsync(d,(p,_)=>{Assert.Equal(0,wire.Posts);audit=p;return Task.FromResult(true);},default);
  Assert.Equal(1,wire.Posts);Assert.Equal(400,result.HttpStatus);Assert.NotNull(audit);Assert.True(audit.ParceirosComprovados);Assert.Equal("90000003",audit.ParceirosContrato.Single().IdParceiroSap);
  using var j=System.Text.Json.JsonDocument.Parse(wire.Body);Assert.Equal("90000003",j.RootElement.GetProperty("to_Pesagem")[0].GetProperty("IdParceiro").GetString());Assert.Equal("90000011",j.RootElement.GetProperty("to_Pesagem")[0].GetProperty("IdParceiroEntrega").GetString());
 }
 [Fact] public void JSON_SAP_reconstructs_the_audited_binding_instead_of_a_later_live_contract_owner()
 {
  var d=Document();var p=Prepare(d);var proof=p.ParceirosContrato;var now=DateTimeOffset.UtcNow;var attempt=Guid.NewGuid();
  var prepared=new FugaTES.Aplicacao.Consultas.RecebimentosToledo.ToledoSapJsonEvento(Guid.NewGuid(),"toledo_sap_documento_preparado",attempt,now,p.Sha256,ToledoSapPreparedPayload.Version,p.EpochMilliseconds,null,null,null,true){DeliveryPartners=p.DeliveryPartners,ParceirosVersao=ToledoSapContractPartners.Version,ParceirosContrato=proof};
  var result=new FugaTES.Aplicacao.Consultas.RecebimentosToledo.ToledoSapJsonEvento(Guid.NewGuid(),"toledo_sap_resultado",attempt,now.AddSeconds(1),p.Sha256,ToledoSapPreparedPayload.Version,null,true,201,"RecebimentoConfirmadoSap",true);
  var source=new FugaTES.Aplicacao.Consultas.RecebimentosToledo.ToledoSapJsonFonte(new(d.ReciboId,d.DocumentoJson,d.FingerprintOriginal),Guid.NewGuid(),ToledoSapWork.Fingerprint(d.ReciboId,d.FingerprintOriginal),1,[prepared,result],false)
   {RecebidoEm=d.RecebidoEm,ContratosSap=[ToledoSapPartnerFixture.Source(partner:"90000099")]};
  var view=ToledoSapDocumentoLeitura.Preparar(source);Assert.Equal("ENVIADO_CONFERIDO",view.Estado);Assert.Equal(p.Sha256,view.Sha256);
  using var j=System.Text.Json.JsonDocument.Parse(view.Json!);Assert.Equal("90000003",j.RootElement.GetProperty("to_Pesagem")[0].GetProperty("IdParceiro").GetString());
  var bad=prepared with{ParceirosContrato=[proof[0] with{IdParceiroSap="90000088"}]};Assert.Null(ToledoSapDocumentoLeitura.Preparar(source with{Eventos=[bad,result]}).Json);
 }
 [Fact] public void Pre_POST_journal_parser_keeps_only_valid_versioned_partner_evidence()
 {
  var d=Document();var p=Prepare(d);var plan=new PlanoEnvioToledoSap(p.Sha256,ToledoSapPreparedPayload.Version,p.EpochMilliseconds){DeliveryPartners=p.DeliveryPartners,ParceirosVersao=ToledoSapContractPartners.Version,ParceirosContrato=p.ParceirosContrato};
  var parsed=FugaTES.Persistencia.Consultas.RecebimentosToledo.RecebimentosToledoPostgreSqlReadRepository.ParseSapJsonEvento(Guid.NewGuid(),"toledo_sap_documento_preparado",Guid.NewGuid(),DateTimeOffset.UtcNow,System.Text.Json.JsonSerializer.Serialize(plan));
  Assert.True(parsed.Valido);Assert.Equal(plan.ParceirosContrato,parsed.ParceirosContrato);Assert.Equal(plan.ParceirosVersao,parsed.ParceirosVersao);
 }
 [Fact] public void Optional_actual_contract1168_source_passes_canonical_integrity_and_offline_projection_only()
 {
  var path=Environment.GetEnvironmentVariable("FUGATES_PARTNER_PHYSICAL_SOURCE");if(string.IsNullOrWhiteSpace(path))return;
  using var file=System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));var identity=file.RootElement.GetProperty("identity");
  Assert.Equal("18/fugatesacmdev",identity.GetProperty("cluster").GetString());Assert.Equal("5433",identity.GetProperty("port").GetString());Assert.Equal("on",identity.GetProperty("readOnly").GetString());
  var sources=System.Text.Json.JsonSerializer.Deserialize<ToledoSapContratoFonte[]>(file.RootElement.GetProperty("sources").GetRawText())!;
  var d=Document();var node=System.Text.Json.Nodes.JsonNode.Parse(d.DocumentoJson)!;node["to_Pesagem"]![0]!["ContratoACM"]="0000001168";node["Material"]="7000000";
  var raw=node.ToJsonString();d=d with{DocumentoJson=raw,FingerprintOriginal=RegistrarPesagemToledoCasoDeUso.Fingerprint(raw),RecebidoEm=sources.Max(x=>x.CriadoEm).AddSeconds(1),ContratosSap=sources};
  var p=Prepare(d);using var result=System.Text.Json.JsonDocument.Parse(p.Json);var item=result.RootElement.GetProperty("to_Pesagem")[0];
  Assert.Equal("0020000610",item.GetProperty("IdParceiro").GetString());Assert.Equal("90000011",item.GetProperty("IdParceiroEntrega").GetString());
  File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path))!,"PHYSICAL_SOURCE_PROJECTOR_VERIFIED.json"),System.Text.Json.JsonSerializer.Serialize(new{result="PASS",contract="0000001168",authoritativePartner="0020000610",guardianDeliveryPreserved=true,sourceCanonicalHashVerified=true,projectionNature="OFFLINE_SYNTHETIC_WEIGHING_WITH_READONLY_PHYSICAL_CONTRACT_SOURCE",businessPosts=0,databaseWrites=0}));
 }
 sealed class PartnerWire:HttpMessageHandler
 {
  public int Posts;public string Body="";
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
  {
   Assert.Equal("sap.invalid",r.RequestUri!.Host);
   if(r.Method==HttpMethod.Post){Posts++;Body=await r.Content!.ReadAsStringAsync(ct);return new(HttpStatusCode.BadRequest){Content=new StringContent("{}")};}
   if(r.Headers.Contains("X-CSRF-Token")){var response=new HttpResponseMessage(HttpStatusCode.OK);response.Headers.Add("X-CSRF-Token","OFFLINE_CSRF");response.Headers.Add("Set-Cookie","SYNTHETIC=YES; Path=/");return response;}
   return r.RequestUri.Query.Contains("$inlinecount=")?new(HttpStatusCode.OK){Content=new StringContent("""<feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata"><id>https://sap.invalid/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem</id><m:count>0</m:count></feed>""")}:new(HttpStatusCode.NotFound){Content=new StringContent("{}")};
  }
 }
 sealed class Wire:HttpMessageHandler{public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken c){Calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));}}
 sealed class Auth:IAutenticacaoSap{public Task AplicarAsync(HttpRequestMessage r,CancellationToken c)=>Task.CompletedTask;}
}
