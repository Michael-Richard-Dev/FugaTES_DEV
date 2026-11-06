using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.BusinessDocuments;

namespace FugaTES.Testes;

public sealed class PedidosCompraMmTests
{
    // Removing local approval validation, decimal precision or either paging loop must fail these tests.
    [Fact] public async Task Released_order_preserves_decimal_supplier_material_group_and_leading_zeros()
    {
        var handler=new Transport(_=>Page(Header()));
        using var http=new HttpClient(handler);
        var result=await Query(http).ConsultarAsync(default);
        Assert.True(result.Sucesso,result.Codigo);var item=Assert.Single(result.Itens);
        Assert.Equal("4455000001",item.Pedido);Assert.Equal("00010",item.Item);
        Assert.Equal("3000",item.Empresa);Assert.Equal("3011",item.Centro);
        Assert.Equal("0000200001",item.Fornecedor);Assert.Equal("00001000249",item.Material);
        Assert.Equal("L002",item.GrupoMaterial);Assert.Equal(50000.123m,item.Quantidade);
        Assert.Equal("KG",item.Unidade);Assert.Equal("05",item.EstadoSap);
        var query=Uri.UnescapeDataString(Assert.Single(handler.Uris).Query);
        Assert.Contains("CompanyCode eq '3000'",query);Assert.Contains("PurchasingProcessingStatus eq '05'",query);
        Assert.Contains("ReleaseIsNotCompleted eq false",query);Assert.Contains("PurchaseOrderDeletionCode eq ''",query);
        Assert.Contains("3011",query);Assert.Contains("3008",query);
        Assert.DoesNotContain("PurchaseOrderType eq",query); // all types, not just ZCOL
        var json=JsonNode.Parse(item.Json)!;Assert.Equal("0000200001",json["Supplier"]!.GetValue<string>());
        Assert.Equal("00010",json["_PurchaseOrderItem"]!["PurchaseOrderItem"]!.GetValue<string>());
    }
    [Theory]
    [InlineData("CompanyCode","4000")][InlineData("PurchasingProcessingStatus","02")]
    [InlineData("PurchasingProcessingStatus","03")][InlineData("PurchasingProcessingStatus","08")]
    [InlineData("PurchaseOrderDeletionCode","L")][InlineData("PurchaseOrderDeletionCode","S")]
    public async Task Api_ignoring_header_filter_never_leaks_ineligible_order(string property,string value)
    {var h=Header();h[property]=value;using var http=new HttpClient(new Transport(_=>Page(h)));var result=await Query(http).ConsultarAsync(default);Assert.True(result.Sucesso);Assert.Empty(result.Itens);}
    [Theory][InlineData("ReleaseIsNotCompleted")][InlineData("PurchasingCompletenessStatus")]
    public async Task Pending_release_or_held_order_is_not_approved(string field)
    {var h=Header();h[field]=true;using var http=new HttpClient(new Transport(_=>Page(h)));var result=await Query(http).ConsultarAsync(default);Assert.True(result.Sucesso);Assert.Empty(result.Itens);}
    [Theory][InlineData("Plant","4000")][InlineData("CompanyCode","4000")][InlineData("PurchasingDocumentDeletionCode","L")][InlineData("PurchasingDocumentDeletionCode","S")]
    public async Task Api_ignoring_item_filter_never_leaks_wrong_plant_company_or_deleted_item(string property,string value)
    {var h=Header();h["_PurchaseOrderItem"]![0]![property]=value;using var http=new HttpClient(new Transport(_=>Page(h)));var result=await Query(http).ConsultarAsync(default);Assert.True(result.Sucesso);Assert.Empty(result.Itens);}
    [Theory][InlineData("ReleaseIsNotCompleted")][InlineData("PurchasingProcessingStatus")][InlineData("Supplier")][InlineData("PurchaseOrderDeletionCode")]
    public async Task Missing_required_header_data_is_not_silent_empty_success(string field)
    {var h=Header();h.Remove(field);using var http=new HttpClient(new Transport(_=>Page(h)));var result=await Query(http).ConsultarAsync(default);Assert.False(result.Sucesso);Assert.Empty(result.Itens);Assert.Equal("MM_SAP_RESPONSE_INVALID",result.Codigo);}
    [Theory][InlineData("OrderQuantity")][InlineData("PurchaseOrderItem")][InlineData("MaterialGroup")]
    public async Task Missing_required_item_data_rejects_whole_snapshot(string field)
    {var h=Header();h["_PurchaseOrderItem"]![0]!.AsObject().Remove(field);using var http=new HttpClient(new Transport(_=>Page(h)));var result=await Query(http).ConsultarAsync(default);Assert.False(result.Sucesso);Assert.Empty(result.Itens);}
    [Fact] public async Task Both_plants_and_all_document_types_are_included()
    {var a=Header();var b=Header("4455000002");b["PurchaseOrderType"]="NB";b["_PurchaseOrderItem"]![0]!["Plant"]="3008";b["_PurchaseOrderItem"]![0]!["PurchaseOrder"]="4455000002";using var http=new HttpClient(new Transport(_=>Page(a,b)));var result=await Query(http).ConsultarAsync(default);Assert.True(result.Sucesso);Assert.Equal(2,result.Itens.Count);}
    [Fact] public async Task Header_and_nested_item_pages_are_fully_read()
    {
        var h=Header();h["_PurchaseOrderItem@odata.nextLink"]="PurchaseOrder('4455000001')/_PurchaseOrderItem?$skiptoken=items2";
        var p=JsonNode.Parse(Page(h))!.AsObject();p["@odata.nextLink"]="PurchaseOrder?$skiptoken=orders2";
        var extra=Item("00020");extra["Plant"]="3008";
        var transport=new Transport(uri=>uri.Contains("items2")?Page(extra):uri.Contains("orders2")?Page(Header("4455000002")):p.ToJsonString());
        using var http=new HttpClient(transport);var result=await Query(http).ConsultarAsync(default);
        Assert.True(result.Sucesso,result.Codigo);Assert.Equal(3,result.Itens.Count);Assert.Equal(3,result.Paginas);
        Assert.Contains(result.Itens,x=>x.Item=="00020"&&x.Centro=="3008");
    }
    [Theory]
    [InlineData("https://attacker.invalid/PurchaseOrder")]
    [InlineData("http://vhfufds4ci.sap.fugacouros.com.br:44300/PurchaseOrder")]
    [InlineData("PurchaseOrder?sap-client=999")]
    [InlineData("PurchaseOrder?sap-client=110&sap-client=110")]
    [InlineData("../PurchaseOrder")]
    [InlineData("PurchaseOrder?$skiptoken=2#fragment")]
    public async Task Forged_nextlink_is_rejected_before_credential_resolution(string next)
    {var p=JsonNode.Parse(Page(Header()))!;p["@odata.nextLink"]=next;var auth=new Auth();var handler=new Transport(_=>p.ToJsonString());using var http=new HttpClient(handler);var result=await Query(http,auth).ConsultarAsync(default);Assert.False(result.Sucesso);Assert.Equal(1,auth.Calls);Assert.Single(handler.Uris);Assert.Empty(result.Itens);}
    [Fact] public async Task Repeated_nextlink_cannot_loop_or_claim_complete_snapshot()
    {var p=JsonNode.Parse(Page(Header()))!;p["@odata.nextLink"]="PurchaseOrder?$skiptoken=loop";var handler=new Transport(_=>p.ToJsonString());using var http=new HttpClient(handler);var result=await Query(http).ConsultarAsync(default);Assert.False(result.Sucesso);Assert.InRange(handler.Uris.Count,1,2);Assert.Empty(result.Itens);}
    [Fact] public async Task Failure_on_later_page_never_returns_partial_orders()
    {var p=JsonNode.Parse(Page(Header()))!;p["@odata.nextLink"]="PurchaseOrder?$skiptoken=broken";var handler=new Transport(_=>p.ToJsonString(),uri=>uri.Contains("broken")?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK);using var http=new HttpClient(handler);var result=await Query(http).ConsultarAsync(default);Assert.False(result.Sucesso);Assert.Empty(result.Itens);Assert.Equal(503,result.Http);}
    [Fact] public async Task Item_identity_must_match_parent_order()
    {var h=Header();h["_PurchaseOrderItem"]![0]!["PurchaseOrder"]="4455000099";using var http=new HttpClient(new Transport(_=>Page(h)));var result=await Query(http).ConsultarAsync(default);Assert.False(result.Sucesso);Assert.Empty(result.Itens);}
    [Fact] public async Task Duplicate_item_identity_does_not_silently_replace_business_values()
    {var h=Header();h["_PurchaseOrderItem"]!.AsArray().Add(Item("00010"));using var http=new HttpClient(new Transport(_=>Page(h)));var result=await Query(http).ConsultarAsync(default);Assert.False(result.Sucesso);Assert.Empty(result.Itens);}
    [Theory][InlineData(401,"MM_SAP_AUTHENTICATION_REQUIRED")][InlineData(403,"MM_SAP_AUTHORIZATION_REQUIRED")][InlineData(400,"MM_SAP_QUERY_REJECTED")][InlineData(500,"MM_SAP_UNAVAILABLE")]
    public async Task Http_errors_are_sanitized_and_never_retried(int code,string expected)
    {var handler=new Transport(_=>"SECRET_DIAGNOSTIC_DO_NOT_EXPORT",_ =>(HttpStatusCode)code);using var http=new HttpClient(handler);var result=await Query(http).ConsultarAsync(default);Assert.False(result.Sucesso);Assert.Equal(expected,result.Codigo);Assert.Equal(code,result.Http);Assert.Single(handler.Uris);}
    [Fact] public async Task Malformed_decimal_is_not_coerced_to_zero()
    {var h=Header();h["_PurchaseOrderItem"]![0]!["OrderQuantity"]="1,234";using var http=new HttpClient(new Transport(_=>Page(h)));Assert.False((await Query(http).ConsultarAsync(default)).Sucesso);}
    [Fact] public async Task Canceled_collection_does_not_dispatch_get()
    {var transport=new Transport(_=>Page(Header()));using var http=new HttpClient(transport);using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Query(http).ConsultarAsync(cancel.Token));Assert.Empty(transport.Uris);}

    // O ambiente e explicito: Activator nao preenche parametro opcional, e um teste de QAS120 so
    // precisa passar o seu aqui.
    internal static IPedidosCompraMmConsulta Query(HttpClient http,IAutenticacaoSap? auth=null,FugaTES.Aplicacao.Configuracao.AmbienteSap? ambiente=null)
    {var type=typeof(ConsultaPedidoCompraSapAdapter).Assembly.GetType("FugaTES.Integracao.SAP.BusinessDocuments.PedidosCompraMmConsultaAdapter");Assert.NotNull(type);return Assert.IsAssignableFrom<IPedidosCompraMmConsulta>(Activator.CreateInstance(type,http,auth??new Auth(),ambiente??FugaTES.Aplicacao.Configuracao.AmbientesSap.Dev110));}
    internal static JsonObject Header(string po="4455000001")=>new(){["PurchaseOrder"]=po,["PurchaseOrderType"]="ZCOL",["CreationDate"]="2026-11-01",["CompanyCode"]="3000",["Supplier"]="0000200001",["PurchasingProcessingStatus"]="05",["PurchaseOrderDeletionCode"]="",["ReleaseIsNotCompleted"]=false,["PurchasingCompletenessStatus"]=false,["_PurchaseOrderItem"]=new JsonArray(Item("00010",po))};
    internal static JsonObject Item(string item="00010",string po="4455000001")=>new(){["PurchaseOrder"]=po,["PurchaseOrderItem"]=item,["CompanyCode"]="3000",["Plant"]="3011",["Material"]="00001000249",["MaterialGroup"]="L002",["OrderQuantity"]="50000.123",["PurchaseOrderQuantityUnit"]="KG",["PurchasingDocumentDeletionCode"]=""};
    internal static string Page(params JsonObject[] rows)=>new JsonObject{["value"]=new JsonArray(rows.Cast<JsonNode?>().ToArray())}.ToJsonString();
    private sealed class Auth:IAutenticacaoSap{public int Calls;public Task AplicarAsync(HttpRequestMessage request,CancellationToken ct){Calls++;request.Headers.TryAddWithoutValidation("Authorization","TEST_ONLY");return Task.CompletedTask;}}
    internal sealed class Transport(Func<string,string> body,Func<string,HttpStatusCode>? status=null):HttpMessageHandler
    {public List<Uri> Uris{get;}=[];protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){ct.ThrowIfCancellationRequested();Assert.Equal(HttpMethod.Get,request.Method);Uris.Add(request.RequestUri!);return Task.FromResult(new HttpResponseMessage(status?.Invoke(request.RequestUri!.ToString())??HttpStatusCode.OK){Content=new StringContent(body(request.RequestUri!.ToString()),Encoding.UTF8,"application/json")});}}
}
