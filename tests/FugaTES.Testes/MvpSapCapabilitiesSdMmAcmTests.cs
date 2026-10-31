using System.Net;
using System.Net.Http.Headers;
using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Aplicacao.Servicos.SAP;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using Xunit;

namespace FugaTES.Testes;

// Gate FUGATES-MVP-SAP-CAPABILITIES-SD-MM-ACM-R1. Nenhum teste faz write SAP real (handler fake).
public sealed class MvpSapCapabilitiesSdMmAcmTests
{
    [Fact]
    public async Task Sc_partner_read_usa_reference_BP_supplier_sem_confundir_SP_com_funcao_SC()
    {
        var handler = new SapHandler((_, _) => Json(HttpStatusCode.OK, """
            {"value":[{"SalesOrder":"3807","SoldToParty":"SOLD-TO",
            "_Partner":[{"PartnerFunction":"SC","PartnerFunctionInternalCode":"SP",
            "Supplier":"20000375","ReferenceBusinessPartner":"20000375"}],"_Item":[]}]}
            """));
        var result = await new ConsultaOrdemVendaSapAdapter(Exec(handler)).ConsultarAsync("3807", CancellationToken.None);
        Assert.Equal(StatusConsultaSap.Sucesso, result.Status);
        var carrier = Assert.Single(result.Dados!.Partners!);
        Assert.Equal("SC", carrier.PartnerFunction);
        Assert.Equal("20000375", carrier.BusinessPartner);
        Assert.Equal("SOLD-TO", result.Dados.SoldToParty);
        Assert.Contains("$expand=_Item,_Partner", handler.Requests[0].RequestUri!.Query);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task Sc_partner_identidades_divergentes_fail_closed()
    {
        var handler = new SapHandler((_, _) => Json(HttpStatusCode.OK, """
            {"value":[{"SalesOrder":"3807","_Partner":[{"PartnerFunction":"SC",
            "Supplier":"OTHER","ReferenceBusinessPartner":"20000375"}],"_Item":[]}]}
            """));
        var result = await new ConsultaOrdemVendaSapAdapter(Exec(handler)).ConsultarAsync("3807", CancellationToken.None);
        Assert.Equal(StatusConsultaSap.FalhaContrato, result.Status);
    }

    [Fact]
    public async Task Known_canonical_0080003354_preservado_somente_por_consulta_SAP_desse_documento()
    {
        var handler = new SapHandler((_, _) => Json(HttpStatusCode.OK, """
            {"d":{"DeliveryDocument":"80003354","ShippingPoint":"3008","OverallGoodsMovementStatus":"A"}}
            """));
        IRemessaSap adapter = new RemessaSapAdapter(Exec(handler));
        var result = await adapter.ObterPorDocumentoAsync("0080003354", CancellationToken.None);
        Assert.Equal(StatusConsultaSap.Sucesso, result.Status);
        Assert.Equal("0080003354", result.Dados!.DeliveryDocument);
        Assert.Contains("A_OutbDeliveryHeader('0080003354')", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Discovered_only_wire_80003354_nao_recebe_padding()
    {
        var handler = new SapHandler((_, i) => Json(HttpStatusCode.OK, i == 0 ? """
            {"d":{"results":[{"DeliveryDocument":"80003354","DeliveryDocumentItem":"10",
            "ReferenceSDDocument":"3807","Plant":"3008"}]}}
            """ : """
            {"d":{"results":[{"DeliveryDocument":"80003354","ShippingPoint":"3008","OverallGoodsMovementStatus":"A"}]}}
            """));
        var result = await new RemessaSapAdapter(Exec(handler)).ObterPorOrdemVendaAsync("3807", CancellationToken.None);
        Assert.Equal(StatusConsultaSap.Sucesso, result.Status);
        Assert.Equal("80003354", result.Dados!.DeliveryDocument);
        Assert.Equal("3807", Assert.Single(result.Dados.Items).ReferenceSDDocument);
        Assert.Equal("3008", result.Dados.ShippingPoint);
    }

    [Theory]
    [InlineData("80003355")]
    [InlineData("0080003354-OTHER")]
    public async Task Contexto_canonico_nao_mascara_documento_divergente_retornado_por_SAP(string wire)
    {
        var handler = new SapHandler((_, _) => Json(HttpStatusCode.OK,
            "{\"d\":{\"DeliveryDocument\":\"" + wire + "\"}}"));
        IRemessaSap adapter = new RemessaSapAdapter(Exec(handler));
        var result = await adapter.ObterPorDocumentoAsync("0080003354", CancellationToken.None);
        Assert.Equal(StatusConsultaSap.FalhaContrato, result.Status);
        Assert.Null(result.Dados);
    }

    [Fact]
    public async Task Partner_function_SP_nao_e_promovido_para_SC()
    {
        var handler = new SapHandler((_, _) => Json(HttpStatusCode.OK, """
            {"value":[{"SalesOrder":"3807","_Partner":[{"PartnerFunction":"SP",
            "Supplier":"20000375","ReferenceBusinessPartner":"20000375"}],"_Item":[]}]}
            """));
        var result = await new ConsultaOrdemVendaSapAdapter(Exec(handler)).ConsultarAsync("3807", CancellationToken.None);
        Assert.Equal(StatusConsultaSap.Sucesso, result.Status);
        Assert.Empty(result.Dados!.Partners!);
    }

    // ---------- SD: SalesOrder GET ----------

    [Theory]
    [InlineData("Material")]
    [InlineData("RequestedQuantityUnit")]
    public async Task SalesOrder_V4_Bio_runtime_mapeia_product_e_unidade_SAP(string campo)
    {
        var handler = new SapHandler((_, _) => Json(HttpStatusCode.OK, """
            {"SalesOrder":"3807","SoldToParty":"20000364","_Partner":[],
             "_Item":[{"SalesOrderItem":"10","Product":"4000095",
             "RequestedQuantity":20000.000,"RequestedQuantitySAPUnit":"KG",
             "Plant":"3008","ShippingPoint":"3008"}]}
            """));
        var result = await new ConsultaOrdemVendaSapAdapter(Exec(handler))
            .ConsultarAsync("3807", CancellationToken.None);
        Assert.Equal(StatusConsultaSap.Sucesso, result.Status);
        Assert.Equal("3807", result.Dados!.SalesOrder);
        Assert.Equal("20000364", result.Dados.SoldToParty);
        var item = Assert.Single(result.Dados.Items);
        Assert.Equal(20000.000m, item.RequestedQuantity);
        Assert.Equal("3008", item.Plant);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        if (campo == "Material")
            Assert.Equal("4000095", item.Material);
        else
            Assert.Equal("KG", item.RequestedQuantityUnit);
    }

    [Fact]
    public async Task SalesOrder_Get_ParseHeaderEItens()
    {
        var handler = new SapHandler((_, _) => Json(HttpStatusCode.OK, """
            {"value":[{"SalesOrder":"1001","SalesOrderType":"OR","SoldToParty":"P1","SalesOrganization":"1000",
             "OverallDeliveryStatus":"A",
             "_Item":[{"SalesOrderItem":"10","Product":"MAT1","RequestedQuantity":"5.000","RequestedQuantitySAPUnit":"KG","Plant":"1000"}]}]}
            """));
        var r = await new ConsultaOrdemVendaSapAdapter(Exec(handler)).ConsultarAsync("1001", CancellationToken.None);

        Assert.Equal(StatusConsultaSap.Sucesso, r.Status);
        Assert.Equal("1001", r.Dados!.SalesOrder);
        Assert.Equal("OR", r.Dados.SalesOrderType);
        var item = Assert.Single(r.Dados.Items);
        Assert.Equal("MAT1", item.Material);
        Assert.Equal(5.0m, item.RequestedQuantity);
        Assert.Equal("/sap/opu/odata4/sap/api_salesorder/srvd_a2x/sap/salesorder/0001/SalesOrder('1001')", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("$expand=_Item", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task SalesOrder_Get_404_NaoEncontrado()
    {
        var handler = new SapHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var r = await new ConsultaOrdemVendaSapAdapter(Exec(handler)).ConsultarAsync("x", CancellationToken.None);
        Assert.Equal(StatusConsultaSap.NaoEncontrado, r.Status);
    }

    [Fact]
    public async Task SalesOrder_Get_ConfiguracaoInvalida()
    {
        var exec = new SapODataHttpExecutor(new HttpClient(new SapHandler((_, _) => Json(HttpStatusCode.OK, "{}"))),
            new OpcoesSap { BaseUrl = new("https://sap.invalid/"), SapClient = null, Timeout = TimeSpan.FromSeconds(5) }, new NoAuth(), new CookieContainer());
        var r = await new ConsultaOrdemVendaSapAdapter(exec).ConsultarAsync("1", CancellationToken.None);
        Assert.Equal(StatusConsultaSap.FalhaConfiguracao, r.Status);
    }

    // ---------- SD: BusinessPartner GET ----------

    [Fact]
    public async Task BusinessPartner_Get_MapeiaNomeImpostoBloqueio()
    {
        var handler = new SapHandler((_, _) => Json(HttpStatusCode.OK, """
            {"d":{"results":[{"BusinessPartner":"P1","BusinessPartnerFullName":"ACME","BusinessPartnerCategory":"2","BusinessPartnerIsBlocked":"X",
             "to_BusinessPartnerTax":{"results":[{"BPTaxType":"BR1","BPTaxLongNumber":"12345678000199"}]}}]}}
            """));
        var r = await new ConsultaPartnerSapAdapter(Exec(handler)).ConsultarAsync("P1", CancellationToken.None);

        Assert.Equal(StatusConsultaSap.Sucesso, r.Status);
        Assert.Equal("ACME", r.Dados!.Name);
        Assert.Equal(2, r.Dados.Category);
        Assert.True(r.Dados.Blocked);
        Assert.Equal("12345678000199", Assert.Single(r.Dados.TaxNumbers).TaxNumber);
    }

    // ---------- SD: Product GET ----------

    [Fact]
    public async Task Product_Get_MapeiaDescricao()
    {
        var handler = new SapHandler((_, _) => Json(HttpStatusCode.OK, """
            {"d":{"results":[{"Product":"MAT1","ProductType":"HALB","BaseUnit":"KG","ProductGroup":"G1",
             "to_Description":{"results":[{"Language":"PT","ProductDescription":"Farelo"}]}}]}}
            """));
        var r = await new ConsultaMaterialSapAdapter(Exec(handler)).ConsultarAsync("MAT1", CancellationToken.None);

        Assert.Equal(StatusConsultaSap.Sucesso, r.Status);
        Assert.Equal("KG", r.Dados!.BaseUnit);
        Assert.Equal("Farelo", r.Dados.Description);
        Assert.Contains("/API_PRODUCT_SRV/A_Product", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    // ---------- SD: Outbound Delivery ----------

    [Fact]
    public async Task Delivery_GetPorOrdemVenda_Found_CapturaETag()
    {
        var handler = new SapHandler((_, i) => Json(HttpStatusCode.OK, i == 0 ? """
            {"d":{"results":[{"DeliveryDocument":"80001","DeliveryDocumentItem":"10","Material":"MAT1","ReferenceSDDocument":"1001"}]}}
            """ : """
            {"d":{"results":[{"DeliveryDocument":"80001","DeliveryDocumentType":"LF","ShippingPoint":"SP1","OverallGoodsMovementStatus":"A","__metadata":{"etag":"W/\"etag-1\""}}]}}
            """));
        var r = await new RemessaSapAdapter(Exec(handler)).ObterPorOrdemVendaAsync("1001", CancellationToken.None);

        Assert.Equal(StatusConsultaSap.Sucesso, r.Status);
        Assert.Equal("80001", r.Dados!.DeliveryDocument);
        Assert.Equal("W/\"etag-1\"", r.Dados.ETag);
        Assert.Contains("ReferenceSDDocument", handler.Requests[0].RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryItem", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryHeader", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("SP1", r.Dados.ShippingPoint);
        Assert.Equal("A", r.Dados.OverallGoodsMovementStatus);
        Assert.Equal("1001", Assert.Single(r.Dados.Items).ReferenceSDDocument);
    }

    [Fact]
    public async Task Delivery_GetPorOrdemVenda_ColecaoVazia_NaoEncontrado()
    {
        var handler = new SapHandler((_, _) => Json(HttpStatusCode.OK, """{"d":{"results":[]}}"""));
        var r = await new RemessaSapAdapter(Exec(handler)).ObterPorOrdemVendaAsync("1001", CancellationToken.None);
        Assert.Equal(StatusConsultaSap.NaoEncontrado, r.Status);
    }

    [Fact]
    public async Task Delivery_Candidatas_preserva_todos_headers_para_selecao_funcional()
    {
        var handler = new SapHandler((_, i) => Json(HttpStatusCode.OK, i == 0 ? """
            {"d":{"results":[{"DeliveryDocument":"80001","DeliveryDocumentItem":"10","ReferenceSDDocument":"1001"},
            {"DeliveryDocument":"80002","DeliveryDocumentItem":"20","ReferenceSDDocument":"1001"}]}}
            """ : i == 1 ? """
            {"d":{"results":[{"DeliveryDocument":"80001","ShippingPoint":"SP1","OverallGoodsMovementStatus":"C"}]}}
            """ : """
            {"d":{"results":[{"DeliveryDocument":"80002","ShippingPoint":"SP2","OverallGoodsMovementStatus":"A"}]}}
            """));
        IRemessaSap adapter = new RemessaSapAdapter(Exec(handler));
        var result = await adapter.ObterCandidatasPorOrdemVendaAsync("1001", CancellationToken.None);
        Assert.Equal(StatusConsultaSap.Sucesso, result.Status);
        Assert.Equal(new[] { "80001", "80002" }, result.Dados!.Select(d => d.DeliveryDocument));
        Assert.Equal(new[] { "C", "A" }, result.Dados!.Select(d => d.OverallGoodsMovementStatus));
        Assert.All(result.Dados!, d => Assert.Equal("1001", Assert.Single(d.Items).ReferenceSDDocument));
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Theory]
    [InlineData("{\"d\":{\"results\":[{\"DeliveryDocument\":\"80001\",\"ReferenceSDDocument\":\"OTHER\"}]}}")]
    [InlineData("{\"d\":{\"results\":[{\"DeliveryDocument\":\"80001\",\"ReferenceSDDocument\":\"1001\"}],\"__next\":\"https://sap.invalid/next\"}}")]
    public async Task Delivery_incompativel_ou_pagina_incompleta_nao_escolhe_primeira(string json)
    {
        var handler = new SapHandler((_, _) => Json(HttpStatusCode.OK, json));
        var result = await new RemessaSapAdapter(Exec(handler)).ObterPorOrdemVendaAsync("1001", CancellationToken.None);
        Assert.Equal(StatusConsultaSap.FalhaContrato, result.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Delivery_Create_ResourceNotProven_NaoExecutaHttp()
    {
        var handler = new SapHandler((req, i) => i == 0
            ? Csrf("tok-1")
            : Json(HttpStatusCode.Created, """{"DeliveryDocument":"80002","DeliveryDocumentType":"LF"}"""));
        var r = await new RemessaSapAdapter(Exec(handler)).CriarAsync(
            new CriarRemessaComando("1001", new Dictionary<string, object?> { ["ReferenceSDDocument"] = "1001" }), CancellationToken.None);

        Assert.Equal(StatusEscritaSap.FalhaConfiguracao, r.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Delivery_Patch_ResourceNotProven_NaoExecutaHttp()
    {
        var handler = new SapHandler((req, i) => i == 0 ? Csrf("tok-2") : NoContent("W/\"etag-2\""));
        var r = await new RemessaSapAdapter(Exec(handler)).AtualizarAsync("80002", "W/\"etag-1\"",
            new Dictionary<string, object?> { ["DeliveryDocumentBySupplier"] = "NF-1" }, CancellationToken.None);

        Assert.Equal(StatusEscritaSap.FalhaConfiguracao, r.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Delivery_Patch_SemETag_FailClosed()
    {
        var handler = new SapHandler((_, _) => Csrf("x"));
        var r = await new RemessaSapAdapter(Exec(handler)).AtualizarAsync("80002", "  ",
            new Dictionary<string, object?>(), CancellationToken.None);
        Assert.Equal(StatusEscritaSap.PreCondicaoFalhou, r.Status);
        Assert.Empty(handler.Requests); // nem tenta bater no SAP
    }

    [Fact]
    public async Task Delivery_Patch_ResourceNotProven_NaoFazFetchNemPatchMesmoCom412Disponivel()
    {
        var handler = new SapHandler((req, i) => i == 0 ? Csrf("t") : new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
        var r = await new RemessaSapAdapter(Exec(handler)).AtualizarAsync("80002", "W/\"old\"",
            new Dictionary<string, object?> { ["x"] = 1 }, CancellationToken.None);
        Assert.Equal(StatusEscritaSap.FalhaConfiguracao, r.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Delivery_Create_ResourceNotProven_NaoFazFetchNemPostMesmoCom409Disponivel()
    {
        var handler = new SapHandler((req, i) => i == 0 ? Csrf("t") : new HttpResponseMessage(HttpStatusCode.Conflict));
        var r = await new RemessaSapAdapter(Exec(handler)).CriarAsync(new CriarRemessaComando("1", new Dictionary<string, object?>()), CancellationToken.None);
        Assert.Equal(StatusEscritaSap.FalhaConfiguracao, r.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Delivery_Create_ResourceNotProven_NaoFazFetchNemPostMesmoCom5xxDisponivel()
    {
        var handler = new SapHandler((req, i) => i == 0 ? Csrf("t") : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var r = await new RemessaSapAdapter(Exec(handler)).CriarAsync(new CriarRemessaComando("1", new Dictionary<string, object?>()), CancellationToken.None);
        Assert.Equal(StatusEscritaSap.FalhaConfiguracao, r.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Delivery_Create_ResourceNotProven_NaoFazCsrfMesmoCom403Disponivel()
    {
        var handler = new SapHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var r = await new RemessaSapAdapter(Exec(handler)).CriarAsync(new CriarRemessaComando("1", new Dictionary<string, object?>()), CancellationToken.None);
        Assert.Equal(StatusEscritaSap.FalhaConfiguracao, r.Status);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("{\"d\":{\"results\":[]}}")]
    [InlineData("{\"d\":{\"results\":[{},{}]}}")]
    [InlineData("{\"d\":{\"results\":{}}}")]
    [InlineData("{\"value\":[{\"Product\":\"MAT1\"},{\"Product\":\"MAT2\"}]}")]
    public async Task Entidade_unica_colecao_vazia_ambigua_ou_incompativel_fail_closed(string json)
    {
        var handler = new SapHandler((_, _) => Json(HttpStatusCode.OK, json));
        var result = await new ConsultaMaterialSapAdapter(Exec(handler)).ConsultarAsync("MAT1", CancellationToken.None);
        Assert.Equal(StatusConsultaSap.FalhaContrato, result.Status);
    }

    [Theory]
    [InlineData("{\"d\":{\"Product\":\"MAT1\",\"BaseUnit\":\"KG\"}}")]
    [InlineData("{\"Product\":\"MAT1\",\"BaseUnit\":\"KG\"}")]
    public async Task V2_entidade_e_raiz_individual_continuam_suportadas(string json)
    {
        var handler = new SapHandler((_, _) => Json(HttpStatusCode.OK, json));
        var result = await new ConsultaMaterialSapAdapter(Exec(handler)).ConsultarAsync("MAT1", CancellationToken.None);
        Assert.Equal(StatusConsultaSap.Sucesso, result.Status);
        Assert.Equal("MAT1", result.Dados!.Product);
    }

    // ---------- MM: PurchaseOrder GET ----------

    [Fact]
    public async Task PurchaseOrder_Get_HeaderEItens()
    {
        var handler = new SapHandler((_, _) => Json(HttpStatusCode.OK, """
            {"value":[{"PurchaseOrder":"4500","PurchaseOrderType":"NB","Supplier":"F1","CompanyCode":"1000",
             "_PurchaseOrderItem":[{"PurchaseOrderItem":"10","Material":"MAT1","OrderQuantity":"10.000","PurchaseOrderQuantityUnit":"KG","Plant":"1000"}]}]}
            """));
        var r = await new ConsultaPedidoCompraSapAdapter(Exec(handler)).ConsultarAsync("4500", CancellationToken.None);

        Assert.Equal(StatusConsultaSap.Sucesso, r.Status);
        Assert.Equal("F1", r.Dados!.Supplier);
        Assert.Equal(10.0m, Assert.Single(r.Dados.Items).OrderQuantity);
        Assert.Equal("/sap/opu/odata4/sap/api_purchaseorder_2/srvd_a2x/sap/purchaseorder/0001/PurchaseOrder('4500')", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("$expand=_PurchaseOrderItem", handler.Requests[0].RequestUri!.Query);
    }

    // ---------- MM: Material Document 101 ----------

    [Fact]
    public async Task MaterialDocument101_Post_CapturaDocumentoEAno()
    {
        var handler = new SapHandler((req, i) => i == 0 ? Csrf("mm-tok") :
            Json(HttpStatusCode.Created, """{"d":{"MaterialDocument":"5000000123","MaterialDocumentYear":"2026"}}"""));
        var r = await new MovimentoMaterialSapAdapter(Exec(handler)).LancarAsync(
            new MovimentoMaterialComando(new Dictionary<string, object?> { ["GoodsMovementCode"] = "01" }), CancellationToken.None);

        Assert.Equal(StatusEscritaSap.Sucesso, r.Status);
        Assert.Equal("5000000123", r.Dados!.MaterialDocument);
        Assert.Equal("2026", r.Dados.MaterialDocumentYear);
        Assert.Equal("Fetch", handler.Requests[0].Headers.GetValues("X-CSRF-Token").Single());
        Assert.Equal("mm-tok", handler.Requests[1].Headers.GetValues("X-CSRF-Token").Single());
        Assert.Contains("GoodsMovementCode", handler.Bodies[1]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaterialDocument101_Post_400_FalhaContrato()
    {
        var handler = new SapHandler((req, i) => i == 0 ? Csrf("t") : new HttpResponseMessage(HttpStatusCode.BadRequest));
        var r = await new MovimentoMaterialSapAdapter(Exec(handler)).LancarAsync(new MovimentoMaterialComando(new Dictionary<string, object?>()), CancellationToken.None);
        Assert.Equal(StatusEscritaSap.FalhaContrato, r.Status);
    }

    [Fact]
    public async Task MaterialDocument101_Post_SucessoSemDocumento_FailClosed()
    {
        var handler = new SapHandler((req, i) => i == 0 ? Csrf("t") : Json(HttpStatusCode.Created, "{}"));
        var r = await new MovimentoMaterialSapAdapter(Exec(handler)).LancarAsync(new MovimentoMaterialComando(new Dictionary<string, object?>()), CancellationToken.None);
        Assert.Equal(StatusEscritaSap.FalhaContrato, r.Status);
    }

    // ---------- ACM: ReceberPesagem transport ----------

    [Fact]
    public async Task ReceberPesagem_CsrfEntaoPost_Sucesso()
    {
        var handler = new SapHandler((req, i) => i == 0 ? Csrf("acm-tok") : Json(HttpStatusCode.OK, """{"d":{"Status":"OK"}}"""));
        var comando = new SapWeighingCommand("/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem",
            new Dictionary<string, object?> { ["ContratoACM"] = "C-1", ["TipoOperacao"] = "E" });
        var r = await new ReceberPesagemSapAdapter(Exec(handler)).EnviarAsync(comando, CancellationToken.None);

        Assert.Equal(StatusEscritaSap.Sucesso, r.Status);
        Assert.Equal("Fetch", handler.Requests[0].Headers.GetValues("X-CSRF-Token").Single());
        Assert.Equal("acm-tok", handler.Requests[1].Headers.GetValues("X-CSRF-Token").Single());
        Assert.Contains("ReceberPesagem", handler.Requests[1].RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceberPesagem_Post500_FalhaTecnica()
    {
        var handler = new SapHandler((req, i) => i == 0 ? Csrf("t") : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var r = await new ReceberPesagemSapAdapter(Exec(handler)).EnviarAsync(
            new SapWeighingCommand("/sap/x/ReceberPesagem", new Dictionary<string, object?>()), CancellationToken.None);
        Assert.Equal(StatusEscritaSap.FalhaTecnica, r.Status);
    }

    [Fact]
    public async Task ReceberPesagem_ResourcePathVazio_FalhaConfiguracao()
    {
        var handler = new SapHandler((_, _) => Csrf("t"));
        var r = await new ReceberPesagemSapAdapter(Exec(handler)).EnviarAsync(
            new SapWeighingCommand("  ", new Dictionary<string, object?>()), CancellationToken.None);
        Assert.Equal(StatusEscritaSap.FalhaConfiguracao, r.Status);
        Assert.Empty(handler.Requests);
    }

    // ---------- Regras pendentes fail-closed (EXPLICIT_PENDING_DECISION) ----------

    [Fact]
    public void DecisaoRemessa_Pendente_FailClosed()
    {
        var d = new DecisaoRemessaSapPendente();
        var so = new SalesOrderSnapshot("1", null, null, null, null, []);
        var e1 = Assert.Throws<DecisaoFuncionalPendenteSapException>(() => d.DeveCriarRemessa(so));
        Assert.Equal(DecisoesFuncionaisPendentesSap.SdTriggerCriarRemessa, e1.Decisao);
        Assert.Throws<DecisaoFuncionalPendenteSapException>(() => d.DeveAtualizarRemessa(new DeliverySnapshot("80001", null, null, null, null, [])));
        Assert.Throws<DecisaoFuncionalPendenteSapException>(() => d.MapearCriacao(so));
    }

    [Fact]
    public void GatilhoMovimentoMaterial_Pendente_FailClosed()
    {
        var g = new GatilhoMovimentoMaterialSapPendente();
        var po = new PurchaseOrderSnapshot("4500", null, null, null, []);
        Assert.Throws<DecisaoFuncionalPendenteSapException>(() => g.DeveLancar(po));
        Assert.Throws<DecisaoFuncionalPendenteSapException>(() => g.Mapear(po));
    }

    [Fact]
    public void MapeadorPesagemAcm_Pendente_FailClosed()
    {
        var m = new MapeadorPesagemAcmSapPendente();
        var e = Assert.Throws<DecisaoFuncionalPendenteSapException>(() => m.Mapear(new Dictionary<string, object?>()));
        Assert.Equal(DecisoesFuncionaisPendentesSap.AcmTipoOperacaoSap, e.Decisao);
    }

    // ---------- infra de teste ----------

    private static SapODataHttpExecutor Exec(SapHandler handler) => new(
        new HttpClient(handler),
        new OpcoesSap { BaseUrl = new("https://sap.invalid/"), SapClient = "100", Timeout = TimeSpan.FromSeconds(5) },
        new NoAuth(),
        new CookieContainer());

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Csrf(string token)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        r.Headers.TryAddWithoutValidation("X-CSRF-Token", token);
        r.Headers.TryAddWithoutValidation("Set-Cookie", "SAP_SESSION=abc123; Path=/; HttpOnly");
        return r;
    }

    private static HttpResponseMessage NoContent(string etag)
    {
        var r = new HttpResponseMessage(HttpStatusCode.NoContent);
        r.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return r;
    }

    private sealed class NoAuth : IAutenticacaoSap
    {
        public Task AplicarAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SapHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var index = Requests.Count;
            Requests.Add(request);
            Bodies.Add(body);
            return responder(request, index);
        }
    }
}
