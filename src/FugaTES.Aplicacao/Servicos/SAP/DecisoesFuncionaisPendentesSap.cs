using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Modelos.SAP;

namespace FugaTES.Aplicacao.Servicos.SAP;

// Regras funcionais Ceccon ainda NAO confirmadas. Existem como pontos de decisao explicitos e
// fail-closed: as implementacoes padrao LANCAM excecao (nunca defaultam silenciosamente).

public sealed class DecisaoFuncionalPendenteSapException(string decisao)
    : Exception("EXPLICIT_PENDING_DECISION: " + decisao)
{
    public string Decisao { get; } = decisao;
}

public static class DecisoesFuncionaisPendentesSap
{
    public const string SdTriggerCriarRemessa = "SD_TRIGGER_CRIAR_REMESSA";
    public const string SdTriggerAtualizarRemessa = "SD_TRIGGER_ATUALIZAR_REMESSA";
    public const string SdPartnerMappingFuncional = "SD_PARTNER_MAPPING_FUNCIONAL";
    public const string SdMmCodigoContratoNumeroReferencia = "SD_MM_CODIGO_CONTRATO_NUMERO_REFERENCIA";
    public const string MmTriggerMovimento101 = "MM_TRIGGER_MOVIMENTO_101";
    public const string AcmTipoOperacaoSap = "ACM_TIPO_OPERACAO_SAP";
    public const string AcmContratoTransformation = "ACM_CONTRATO_ACM_TRANSFORMATION";
    public const string Api7 = "API7";

    public static IReadOnlyList<string> Todas { get; } =
    [
        SdTriggerCriarRemessa,
        SdTriggerAtualizarRemessa,
        SdPartnerMappingFuncional,
        SdMmCodigoContratoNumeroReferencia,
        MmTriggerMovimento101,
        AcmTipoOperacaoSap,
        AcmContratoTransformation,
        Api7
    ];
}

// ---- SD: decisao/orquestracao de remessa (quando criar/atualizar + mapeamento funcional) ----
public interface IDecisaoRemessaSap
{
    bool DeveCriarRemessa(SalesOrderSnapshot ordemVenda);

    bool DeveAtualizarRemessa(DeliverySnapshot remessa);

    CriarRemessaComando MapearCriacao(SalesOrderSnapshot ordemVenda);
}

public sealed class DecisaoRemessaSapPendente : IDecisaoRemessaSap
{
    public bool DeveCriarRemessa(SalesOrderSnapshot ordemVenda)
        => throw new DecisaoFuncionalPendenteSapException(DecisoesFuncionaisPendentesSap.SdTriggerCriarRemessa);

    public bool DeveAtualizarRemessa(DeliverySnapshot remessa)
        => throw new DecisaoFuncionalPendenteSapException(DecisoesFuncionaisPendentesSap.SdTriggerAtualizarRemessa);

    public CriarRemessaComando MapearCriacao(SalesOrderSnapshot ordemVenda)
        => throw new DecisaoFuncionalPendenteSapException(DecisoesFuncionaisPendentesSap.SdPartnerMappingFuncional);
}

// ---- MM: gatilho e mapeamento do movimento 101 ----
public interface IGatilhoMovimentoMaterialSap
{
    bool DeveLancar(PurchaseOrderSnapshot pedidoCompra);

    MovimentoMaterialComando Mapear(PurchaseOrderSnapshot pedidoCompra);
}

public sealed class GatilhoMovimentoMaterialSapPendente : IGatilhoMovimentoMaterialSap
{
    public bool DeveLancar(PurchaseOrderSnapshot pedidoCompra)
        => throw new DecisaoFuncionalPendenteSapException(DecisoesFuncionaisPendentesSap.MmTriggerMovimento101);

    public MovimentoMaterialComando Mapear(PurchaseOrderSnapshot pedidoCompra)
        => throw new DecisaoFuncionalPendenteSapException(DecisoesFuncionaisPendentesSap.SdMmCodigoContratoNumeroReferencia);
}

// ---- ACM: mapeamento funcional para SapWeighingCommand (TipoOperacao, ContratoACM) ----
public interface IMapeadorPesagemAcmSap
{
    SapWeighingCommand Mapear(IReadOnlyDictionary<string, object?> pesagemDominio);
}

public sealed class MapeadorPesagemAcmSapPendente : IMapeadorPesagemAcmSap
{
    public SapWeighingCommand Mapear(IReadOnlyDictionary<string, object?> pesagemDominio)
        => throw new DecisaoFuncionalPendenteSapException(DecisoesFuncionaisPendentesSap.AcmTipoOperacaoSap);
}
