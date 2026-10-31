using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Callback.Http;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
namespace FugaTES.Testes;

public sealed class ToledoDeliveryPartnerTests
{
 internal sealed class Supplier:IToledoSupplierLookup
 {
  public int Calls; public ToledoSupplierStatus Status=ToledoSupplierStatus.Found;
  public Task<ToledoSupplierResult> FindAsync(string code,CancellationToken ct)
  {Calls++;return Task.FromResult(new ToledoSupplierResult(Status,code.PadLeft(10,'0'),code.PadLeft(10,'0'),"SAP SYNTHETIC SUPPLIER","Z001",200,new string('A',64)));}
 }
 internal static ToledoDeliveryPartnerProof Proof(string raw)=>ToledoDeliveryPartners.ValidateAsync(raw,new Supplier(),CancellationToken.None).GetAwaiter().GetResult();
 [Fact] public async Task SAP_name_replaces_untrusted_name_but_keeps_original_code_and_raw_JSON()
 {
  var raw=ToledoDeliveryBpAdmissionTests.Payload();var source=new Supplier();var proof=await ToledoDeliveryPartners.ValidateAsync(raw,source,CancellationToken.None);
  var projected=JsonNode.Parse(ToledoDeliveryPartners.Apply(raw,proof))!;var w=projected["to_Pesagem"]![0]!;
  Assert.Equal("90000733",w["IdParceiroEntrega"]!.ToString());Assert.Equal("SAP SYNTHETIC SUPPLIER",w["ParceiroEntregaNome"]!.ToString());
  Assert.Equal("UNTRUSTED GUARDIAN NAME",JsonNode.Parse(raw)!["to_Pesagem"]![0]!["ParceiroEntregaNome"]!.ToString());Assert.Equal(1,source.Calls);
  Assert.Equal(RegistrarPesagemToledoCasoDeUso.Fingerprint(raw),proof.OriginalSha256);
 }
 [Theory][InlineData(null)][InlineData("")][InlineData(" ")][InlineData("0")][InlineData("0000000000")]
 public async Task Empty_or_zero_uses_original_Guardian_owner_and_name_without_SAP_lookup(string? code)
 {
  var raw=ToledoDeliveryBpAdmissionTests.Payload(code);using var j=JsonDocument.Parse(raw);_=ToledoPesagemCallbackDto.Deserializar(j.RootElement);
  var proof=await ToledoDeliveryPartners.ValidateAsync(raw,null,CancellationToken.None);var item=JsonNode.Parse(ToledoDeliveryPartners.Apply(raw,proof))!["to_Pesagem"]![0]!;
  Assert.Equal("90000003",item["IdParceiroEntrega"]!.ToString());Assert.Equal("SYNTHETIC ORIGINAL PARTNER",item["ParceiroEntregaNome"]!.ToString());
 }
 [Fact] public async Task Projection_keeps_delivery_default_from_Guardian_when_contract_owner_is_different()
 {
  var raw=ToledoDeliveryBpAdmissionTests.Payload("0");var proof=await ToledoDeliveryPartners.ValidateAsync(raw,null,CancellationToken.None);
  var contract=ToledoSapPartnerFixture.Source(partner:"90000888");
  var doc=ToledoSapPartnerFixture.Bind(new(Guid.NewGuid(),"3011","900003",raw,RegistrarPesagemToledoCasoDeUso.Fingerprint(raw),true,false)) with{DeliveryPartners=proof,ContratosSap=[contract]};
  var item=JsonNode.Parse(ToledoSapODataPayload.Prepare(doc,new("America/Sao_Paulo")).Json)!["to_Pesagem"]![0]!;
  Assert.Equal("90000888",item["IdParceiro"]!.ToString());Assert.Equal("90000003",item["IdParceiroEntrega"]!.ToString());Assert.Equal("SYNTHETIC ORIGINAL PARTNER",item["ParceiroEntregaNome"]!.ToString());
 }
 [Theory][InlineData(ToledoSupplierStatus.NotFound,422,"NOT_FOUND")][InlineData(ToledoSupplierStatus.NotSupplier,422,"NOT_SUPPLIER")]
 [InlineData(ToledoSupplierStatus.Blocked,422,"BLOCKED")][InlineData(ToledoSupplierStatus.Unavailable,503,"UNAVAILABLE")][InlineData(ToledoSupplierStatus.InvalidResponse,502,"INVALID_SAP_RESPONSE")]
 public async Task Rejection_has_safe_business_or_technical_classification_before_store(ToledoSupplierStatus status,int http,string suffix)
 {
  var store=new ToledoDeliveryBpAdmissionTests.Store();var raw=ToledoDeliveryBpAdmissionTests.Payload();using var j=JsonDocument.Parse(raw);
  var usecase=new RegistrarPesagemToledoCasoDeUso(store,new Supplier{Status=status});
  var error=await Assert.ThrowsAsync<ToledoDeliveryPartnerException>(()=>usecase.ExecutarAsync(ToledoPesagemCallbackDto.Deserializar(j.RootElement).Mapear() with{OrigemAutenticada="OFFLINE"},CancellationToken.None));
  Assert.Equal(http,error.HttpStatus);Assert.EndsWith(suffix,error.Code);Assert.Equal(0,store.Writes);
 }
 [Fact] public async Task Same_BP_in_multiple_items_uses_one_lookup_and_each_item_is_bound()
 {
  var root=JsonNode.Parse(ToledoDeliveryBpAdmissionTests.Payload())!;var weights=root["to_Pesagem"]!.AsArray();var second=weights[0]!.DeepClone();weights[0]!["NrIndiceTotal"]=2;second["NrIndiceTotal"]=2;second["Indice"]=2;second["IdParceiroEntrega"]="0090000733";weights.Add(second);
  var source=new Supplier();var proof=await ToledoDeliveryPartners.ValidateAsync(root.ToJsonString(),source,CancellationToken.None);
  Assert.Equal(1,source.Calls);Assert.Equal(2,proof.Items.Count);Assert.Equal("0090000733",proof.Items[1].Code);
 }
 [Fact] public void Missing_or_tampered_proof_cannot_prepare_outbound_document()
 {
  var raw=ToledoDeliveryBpAdmissionTests.Payload();var doc=ToledoSapPartnerFixture.Bind(new(Guid.NewGuid(),"3011","900003",raw,RegistrarPesagemToledoCasoDeUso.Fingerprint(raw),true,false));
  Assert.Throws<ToledoDeliveryPartnerException>(()=>ToledoSapODataPayload.Prepare(doc with{DeliveryPartners=null},new()));var proof=Proof(raw);
  Assert.Throws<ToledoDeliveryPartnerException>(()=>ToledoDeliveryPartners.Apply(raw,proof with{OriginalSha256=new string('B',64)}));
  Assert.Throws<ToledoDeliveryPartnerException>(()=>ToledoDeliveryPartners.Apply(raw,proof with{Items=[proof.Items[0] with{Code="90000999"}]}));
 }
 internal sealed class Auth:IAutenticacaoSap
 {public Task AplicarAsync(HttpRequestMessage req,CancellationToken ct){req.Headers.Authorization=new("Basic","SYNTHETIC");return Task.CompletedTask;}}
 sealed class Handler(HttpStatusCode status,string body):HttpMessageHandler
 {
  internal readonly List<HttpRequestMessage> Requests=[];
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
  {Requests.Add(r);return Task.FromResult(new HttpResponseMessage(status){Content=new StringContent(body)});}
 }
 static ToledoSupplierSapLookup Lookup(Handler h)=>new(new(new HttpClient(h),new OpcoesSap{BaseUrl=new("https://sap.invalid/"),SapClient="110",Timeout=TimeSpan.FromSeconds(1)},new Auth(),new CookieContainer()));
 internal static string Response=>"""{"d":{"results":[{"BusinessPartner":"0090000733","BusinessPartnerFullName":"SYNTHETIC BP","BusinessPartnerCategory":"2","BusinessPartnerIsBlocked":false,"to_Supplier":{"Supplier":"0090000733","SupplierName":"SAP SYNTHETIC SUPPLIER","SupplierAccountGroup":"Z001","SupplierIsBlockedForPosting":false}}]}}""";
 [Fact] public async Task Real_adapter_uses_single_authenticated_bounded_GET_and_proves_supplier()
 {
  var handler=new Handler(HttpStatusCode.OK,Response);var result=await Lookup(handler).FindAsync("90000733",CancellationToken.None);
  Assert.Equal(ToledoSupplierStatus.Found,result.Status);Assert.Equal("SAP SYNTHETIC SUPPLIER",result.SupplierName);Assert.Equal(64,result.ResponseSha256!.Length);
  var req=Assert.Single(handler.Requests);Assert.Equal(HttpMethod.Get,req.Method);Assert.Null(req.Content);Assert.Equal("Basic",req.Headers.Authorization!.Scheme);
  var query=Uri.UnescapeDataString(req.RequestUri!.Query);Assert.Contains("sap-client=110",query);Assert.Contains("$expand=to_Supplier",query);Assert.Contains("BusinessPartner eq '90000733'",query);Assert.Contains("BusinessPartner eq '0090000733'",query);
  Assert.False(req.Headers.Contains("X-CSRF-Token"));Assert.False(req.Headers.Contains("Cookie"));
 }
 [Theory][InlineData(401)][InlineData(403)][InlineData(404)][InlineData(500)]
 public async Task HTTP_fault_never_means_supplier_not_found(int http)
 {var h=new Handler((HttpStatusCode)http,"<html>error</html>");var r=await Lookup(h).FindAsync("90000733",CancellationToken.None);Assert.Equal(ToledoSupplierStatus.Unavailable,r.Status);Assert.Single(h.Requests);}
 [Fact] public async Task Only_valid_empty_filtered_collection_proves_not_found()
 {var r=await Lookup(new(HttpStatusCode.OK,"{\"d\":{\"results\":[]}}" )).FindAsync("90000733",CancellationToken.None);Assert.Equal(ToledoSupplierStatus.NotFound,r.Status);}
 [Theory][InlineData("identity")][InlineData("supplier")][InlineData("name")][InlineData("group")][InlineData("ambiguous")][InlineData("deferred")][InlineData("html")][InlineData("wrongFlag")][InlineData("next")]
 public async Task Invalid_or_ambiguous_success_is_not_accepted(string kind)
 {
  var n=JsonNode.Parse(Response)!;var bp=n["d"]!["results"]![0]!;var supplier=bp["to_Supplier"]!;
  switch(kind){case "identity":bp["BusinessPartner"]="90000999";break;case "supplier":supplier["Supplier"]="90000999";break;
   case "name":supplier["SupplierName"]="";break;case "group":supplier["SupplierAccountGroup"]="";break;
   case "ambiguous":n["d"]!["results"]!.AsArray().Add(bp.DeepClone());break;case "deferred":bp["to_Supplier"]=JsonNode.Parse("{\"__deferred\":{\"uri\":\"ignored\"}}");break;
   case "wrongFlag":bp["BusinessPartnerIsBlocked"]="false";break;case "next":n["d"]!["__next"]="ignored";break;}
  var r=await Lookup(new(HttpStatusCode.OK,kind=="html"?"<html>error</html>":n.ToJsonString())).FindAsync("90000733",CancellationToken.None);
  Assert.Equal(ToledoSupplierStatus.InvalidResponse,r.Status);
 }
 [Theory][InlineData(false)][InlineData(true)] public async Task Blocked_supplier_or_BP_is_rejected(bool general)
 {var n=JsonNode.Parse(Response)!;var bp=n["d"]!["results"]![0]!;if(general)bp["BusinessPartnerIsBlocked"]=true;else bp["to_Supplier"]!["SupplierIsBlockedForPosting"]=true;
  Assert.Equal(ToledoSupplierStatus.Blocked,(await Lookup(new(HttpStatusCode.OK,n.ToJsonString())).FindAsync("90000733",CancellationToken.None)).Status);}
 [Fact] public async Task Existing_BP_without_supplier_role_is_not_accepted()
 {var n=JsonNode.Parse(Response)!;n["d"]!["results"]![0]!["to_Supplier"]=null;Assert.Equal(ToledoSupplierStatus.NotSupplier,(await Lookup(new(HttpStatusCode.OK,n.ToJsonString())).FindAsync("90000733",CancellationToken.None)).Status);}
}
