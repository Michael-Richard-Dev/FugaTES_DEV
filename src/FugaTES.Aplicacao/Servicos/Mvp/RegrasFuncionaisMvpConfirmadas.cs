using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Servicos.SAP;

namespace FugaTES.Aplicacao.Servicos.Mvp;

public enum OperacaoContratoSap
{
    CompraRegular = 10, CompraTriangular = 20, CompraEntregaFutura = 30,
    VendaRegular = 40, VendaContaEOrdem = 50, VendaEntregaFutura = 60, VendaExportacao = 70
}

public enum TipoOperacaoGuardian { Recebimento = 1, Expedicao = 2 }

public static class RegrasFuncionaisMvpConfirmadas
{
    public static string ResolverEmissor(SalesOrderSnapshot ordem)
        => ExigirIdentidade(ordem.SoldToParty, "SD_SOLDTO_MISSING");

    public static string ResolverTransportadora(SalesOrderSnapshot ordem)
    {
        var partners = ordem.Partners?.Where(p => p.PartnerFunction == "SC").ToArray();
        if (partners is not { Length: 1 }) throw new DecisaoFuncionalPendenteSapException("SD_PARTNER_SC_MISSING_OR_AMBIGUOUS");
        return ExigirIdentidade(partners[0].BusinessPartner, "SD_PARTNER_SC_MISSING");
    }

    public static void ValidarTransportadora(string codigo, PartnerSnapshot partner)
    {
        if (partner.BusinessPartner != codigo || partner.Blocked || partner.Roles?.Contains("CRM010", StringComparer.Ordinal) is not true)
            throw new DecisaoFuncionalPendenteSapException("SD_TRANSPORTADORA_CRM010_REQUIRED");
    }

    public static DeliverySnapshot? SelecionarRemessa(SalesOrderSnapshot ordem,
        IReadOnlyList<DeliverySnapshot> candidatas, IReadOnlyDictionary<string, string> shippingPointsPorCentro)
    {
        var centros = ordem.Items.Select(i => i.Plant).Distinct(StringComparer.Ordinal).ToArray();
        if (centros.Length != 1 || string.IsNullOrWhiteSpace(centros[0]) ||
            !shippingPointsPorCentro.TryGetValue(centros[0]!, out var shippingPoint) || string.IsNullOrWhiteSpace(shippingPoint))
            throw new DecisaoFuncionalPendenteSapException("SD_CENTRO_SHIPPING_POINT_REQUIRED");
        var elegiveis = candidatas.Where(d => d.ShippingPoint == shippingPoint && d.OverallGoodsMovementStatus == "A" &&
            d.Items.Any(i => i.ReferenceSDDocument == ordem.SalesOrder)).ToArray();
        if (elegiveis.Length > 1) throw new DecisaoFuncionalPendenteSapException("SD_DELIVERY_AMBIGUOUS");
        return elegiveis.SingleOrDefault();
    }

    public static string ResolverCodigoContrato(SalesOrderSnapshot ordem)
        => ordem.PossuiContratoAcm is false ? ExigirIdentidade(ordem.SalesOrder, "SD_SALES_ORDER_MISSING")
            : throw new DecisaoFuncionalPendenteSapException("SD_CODIGO_CONTRATO_ACM_PENDING");

    public static string ResolverCodigoContrato(PurchaseOrderSnapshot pedido)
        => pedido.PossuiContratoAcm is false ? ExigirIdentidade(pedido.PurchaseOrder, "MM_PURCHASE_ORDER_MISSING")
            : throw new DecisaoFuncionalPendenteSapException("MM_CODIGO_CONTRATO_ACM_PENDING");

    public static TipoOperacaoGuardian MapearTipoOperacao(int operacaoContrato) => operacaoContrato switch
    {
        10 or 20 or 30 => TipoOperacaoGuardian.Recebimento,
        40 or 50 or 60 or 70 => TipoOperacaoGuardian.Expedicao,
        _ => throw new DecisaoFuncionalPendenteSapException("GUARDIAN_OPERACAO_CONTRATO_UNKNOWN")
    };

    private static string ExigirIdentidade(string? value, string decision)
        => !string.IsNullOrWhiteSpace(value) && value == value.Trim() ? value
            : throw new DecisaoFuncionalPendenteSapException(decision);
}

public sealed class DecisoesMvpConfirmadas : IDecisoesFluxoMvp
{
    public void Exigir(string decisao)
    {
        if (decisao is "SD_PARTNER_MAPPING" or "SD_CODIGO_CONTRATO" or "MM_CODIGO_CONTRATO" or
            "GUARDIAN_ID_CALLBACK_EQUIVALENCE" or "GUARDIAN_TICKET_SEQUENCIAL_EQUIVALENCE") return;
        throw new DecisaoFuncionalPendenteSapException(decisao);
    }
}

public sealed class PreparacaoMvpRegrasConfirmadas(IReadOnlyDictionary<string, string>? shippingPointsPorCentro = null)
    : IPreparacaoSaidaSdMvp, IPreparacaoEntradaMmMvp
{
    public DeliverySnapshot? SelecionarRemessa(SalesOrderSnapshot ordem, IReadOnlyList<DeliverySnapshot> candidatas)
        => RegrasFuncionaisMvpConfirmadas.SelecionarRemessa(ordem, candidatas,
            shippingPointsPorCentro ?? new Dictionary<string, string>());
    public string ResolverPartner(SalesOrderSnapshot ordem) => RegrasFuncionaisMvpConfirmadas.ResolverEmissor(ordem);
    public string? ResolverMaterial(SalesOrderSnapshot ordem) => throw new DecisaoFuncionalPendenteSapException("SD_PRODUCT_MAPPING");
    public IReadOnlyDictionary<string, object?> MapearPatch(SalesOrderSnapshot ordem, DeliverySnapshot remessa)
        => throw new DecisaoFuncionalPendenteSapException("SD_PATCH_REMESSA");
    public PreCadastroRecebimentoGuardianSet26 MapearGuardian(SalesOrderSnapshot ordem, PartnerSnapshot partner, ProductSnapshot? material)
    {
        _ = RegrasFuncionaisMvpConfirmadas.ResolverCodigoContrato(ordem);
        throw new DecisaoFuncionalPendenteSapException("SD_NUMERO_REFERENCIA");
    }
    public string ResolverSupplier(PurchaseOrderSnapshot pedido) => throw new DecisaoFuncionalPendenteSapException("MM_PARTNER_MAPPING");
    public string ResolverMaterial(PurchaseOrderSnapshot pedido) => throw new DecisaoFuncionalPendenteSapException("MM_PRODUCT_MAPPING");
    public PreCadastroRecebimentoGuardianSet26 MapearGuardian(PurchaseOrderSnapshot pedido, PartnerSnapshot supplier, ProductSnapshot material)
    {
        _ = RegrasFuncionaisMvpConfirmadas.ResolverCodigoContrato(pedido);
        throw new DecisaoFuncionalPendenteSapException("MM_NUMERO_REFERENCIA");
    }
}
