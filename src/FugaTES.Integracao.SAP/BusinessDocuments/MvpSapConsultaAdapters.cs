using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Integracao.SAP.Http;

namespace FugaTES.Integracao.SAP.BusinessDocuments;

internal static class SapKeyPath
{
    public static string Single(string servico, string entitySet, string chave)
        => servico + "/" + entitySet + "('" + Uri.EscapeDataString((chave ?? string.Empty).Replace("'", "''", StringComparison.Ordinal)) + "')";
}

// SD: SalesOrder GET (API_SalesOrder, V4).
public sealed class ConsultaOrdemVendaSapAdapter(SapODataHttpExecutor executor)
    : SapConsultaAdapterBase(executor), IConsultaOrdemVendaSap
{
    private const string Servico = "/sap/opu/odata4/sap/api_salesorder/srvd_a2x/sap/salesorder/0001";

    public Task<ResultadoConsultaSap<SalesOrderSnapshot>> ConsultarAsync(string salesOrder, CancellationToken cancellationToken)
        => ConsultarAsync(SapKeyPath.Single(Servico, "SalesOrder", salesOrder), "$expand=_Item,_Partner", Mapear, cancellationToken);

    private static SalesOrderSnapshot? Mapear(JsonElement e)
    {
        var id = Texto(e, "SalesOrder");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var itens = Colecao(e, "_Item")
            .Select(i => new SalesOrderItemSnapshot(
                Texto(i, "SalesOrderItem") ?? string.Empty,
                Texto(i, "Product"),
                Decimal(i, "RequestedQuantity"),
                Texto(i, "RequestedQuantitySAPUnit"),
                Texto(i, "Plant")))
            .ToArray();

        var partners = new List<SalesOrderPartnerSnapshot>();
        foreach (var partner in Colecao(e, "_Partner"))
        {
            if (Texto(partner, "PartnerFunction") != "SC") continue;
            var reference = Texto(partner, "ReferenceBusinessPartner");
            var supplier = Texto(partner, "Supplier");
            if (!string.IsNullOrWhiteSpace(reference) && !string.IsNullOrWhiteSpace(supplier) && reference != supplier)
                return null;
            var businessPartner = !string.IsNullOrWhiteSpace(reference) ? reference : supplier;
            if (string.IsNullOrWhiteSpace(businessPartner)) return null;
            partners.Add(new("SC", businessPartner));
        }

        return new SalesOrderSnapshot(
            id,
            Texto(e, "SalesOrderType"),
            Texto(e, "SoldToParty"),
            Texto(e, "SalesOrganization"),
            Texto(e, "OverallDeliveryStatus"),
            itens, partners);
    }
}

// SD: BusinessPartner GET (API_BUSINESS_PARTNER) -> PartnerSnapshot normalizado.
public sealed class ConsultaPartnerSapAdapter(SapODataHttpExecutor executor)
    : SapConsultaAdapterBase(executor), IConsultaPartnerSap
{
    private const string Servico = "/sap/opu/odata/sap/API_BUSINESS_PARTNER";

    public Task<ResultadoConsultaSap<PartnerSnapshot>> ConsultarAsync(string businessPartner, CancellationToken cancellationToken)
        => ConsultarAsync(SapKeyPath.Single(Servico, "A_BusinessPartner", businessPartner), "$expand=to_BusinessPartnerTax", Mapear, cancellationToken);

    private static PartnerSnapshot? Mapear(JsonElement e)
    {
        var id = Texto(e, "BusinessPartner");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var nome = Texto(e, "BusinessPartnerFullName") ?? Texto(e, "OrganizationBPName1");
        var impostos = Colecao(e, "to_BusinessPartnerTax")
            .Select(t => (tipo: Texto(t, "BPTaxType"), numero: Texto(t, "BPTaxLongNumber") ?? Texto(t, "BPTaxNumber")))
            .Where(t => !string.IsNullOrWhiteSpace(t.tipo) && !string.IsNullOrWhiteSpace(t.numero))
            .Select(t => new PartnerTaxSnapshot(t.tipo!, t.numero!))
            .ToArray();

        var bloqueio = Texto(e, "BusinessPartnerIsBlocked");
        var bloqueado = string.Equals(bloqueio, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bloqueio, "X", StringComparison.OrdinalIgnoreCase);

        return new PartnerSnapshot(id, nome, Inteiro(e, "BusinessPartnerCategory"), impostos, bloqueado);
    }
}

// SD: Product GET (API_PRODUCT_SRV).
public sealed class ConsultaMaterialSapAdapter(SapODataHttpExecutor executor)
    : SapConsultaAdapterBase(executor), IConsultaMaterialSap
{
    private const string Servico = "/sap/opu/odata/sap/API_PRODUCT_SRV";

    public Task<ResultadoConsultaSap<ProductSnapshot>> ConsultarAsync(string product, CancellationToken cancellationToken)
        => ConsultarAsync(SapKeyPath.Single(Servico, "A_Product", product), "$expand=to_Description", Mapear, cancellationToken);

    private static ProductSnapshot? Mapear(JsonElement e)
    {
        var id = Texto(e, "Product");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var descricao = Texto(e, "ProductDescription")
            ?? Colecao(e, "to_Description").Select(d => Texto(d, "ProductDescription")).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        return new ProductSnapshot(
            id,
            Texto(e, "ProductType"),
            Texto(e, "BaseUnit"),
            Texto(e, "ProductGroup"),
            descricao);
    }
}

// MM: PurchaseOrder GET (API_PURCHASEORDER_2, V4).
public sealed class ConsultaPedidoCompraSapAdapter(SapODataHttpExecutor executor)
    : SapConsultaAdapterBase(executor), IConsultaPedidoCompraSap
{
    private const string Servico = "/sap/opu/odata4/sap/api_purchaseorder_2/srvd_a2x/sap/purchaseorder/0001";

    public Task<ResultadoConsultaSap<PurchaseOrderSnapshot>> ConsultarAsync(string purchaseOrder, CancellationToken cancellationToken)
        => ConsultarAsync(SapKeyPath.Single(Servico, "PurchaseOrder", purchaseOrder), "$expand=_PurchaseOrderItem", Mapear, cancellationToken);

    private static PurchaseOrderSnapshot? Mapear(JsonElement e)
    {
        var id = Texto(e, "PurchaseOrder");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var itens = Colecao(e, "_PurchaseOrderItem")
            .Select(i => new PurchaseOrderItemSnapshot(
                Texto(i, "PurchaseOrderItem") ?? string.Empty,
                Texto(i, "Material"),
                Decimal(i, "OrderQuantity"),
                Texto(i, "PurchaseOrderQuantityUnit"),
                Texto(i, "Plant")))
            .ToArray();

        return new PurchaseOrderSnapshot(
            id,
            Texto(e, "PurchaseOrderType"),
            Texto(e, "Supplier"),
            Texto(e, "CompanyCode"),
            itens);
    }
}
