using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Modelos.SAP;

namespace FugaTES.Aplicacao.Contratos.Mvp;

public interface IDecisoesFluxoMvp
{
    void Exigir(string decisao);
}

// Projecao duravel dos resultados; o claim/fencing e a execucao pertencem ao INC005.
public interface IResultadoFluxoMvpStore
{
    Task ExigirDisponibilidadeAsync(CancellationToken cancellationToken);
    Task<ResultadoFluxoMvp?> ObterAsync(ClaimIntegracaoMotor claim, CancellationToken cancellationToken);
    Task<ResultadoFluxoMvp?> ObterPorIntegracaoAsync(Guid integrationId, string correlationId, string operation, CancellationToken ct)
        => throw new NotSupportedException("MVP_RESULT_READ_ONLY_LOOKUP_UNSUPPORTED");
    Task<bool> PersistirAsync(ClaimIntegracaoMotor claim, ResultadoFluxoMvp resultado, CancellationToken cancellationToken);
    // Atomic fenced compare-and-set; unsupported stores must fail closed, never emulate a durable reservation.
    Task<bool> ReservarEtapaAsync(ClaimIntegracaoMotor claim,string? etapaAnterior,ResultadoFluxoMvp preparado,CancellationToken ct)
        => Task.FromResult(false);
}

public interface IPreparacaoSaidaSdMvp
{
    DeliverySnapshot? SelecionarRemessa(SalesOrderSnapshot ordem, IReadOnlyList<DeliverySnapshot> candidatas)
        => throw new FugaTES.Aplicacao.Servicos.SAP.DecisaoFuncionalPendenteSapException("SD_CENTRO_SHIPPING_POINT_REQUIRED");

    string ResolverPartner(SalesOrderSnapshot ordem);
    string? ResolverMaterial(SalesOrderSnapshot ordem);
    IReadOnlyDictionary<string, object?> MapearPatch(SalesOrderSnapshot ordem, DeliverySnapshot remessa);
    PreCadastroRecebimentoGuardianSet26 MapearGuardian(SalesOrderSnapshot ordem, PartnerSnapshot partner, ProductSnapshot? material);
}

public interface IPreparacaoEntradaMmMvp
{
    string ResolverSupplier(PurchaseOrderSnapshot pedido);
    string ResolverMaterial(PurchaseOrderSnapshot pedido);
    PreCadastroRecebimentoGuardianSet26 MapearGuardian(PurchaseOrderSnapshot pedido, PartnerSnapshot supplier, ProductSnapshot material);
}

public interface IMapeadorContratoAcmGuardian
{
    ContratoGuardianSet26 Mapear(ContratoSapToledoSnapshot contrato);
    ContratoGuardianSet26 Mapear(VersaoContratoSapToledo versao) => Mapear(versao.Snapshot);
}

public static class DecisoesFluxoMvp
{
    public static IReadOnlyList<string> Para(FluxoMvp fluxo) => fluxo switch
    {
        FluxoMvp.SdOut => ["SD_TRIGGER", "SD_PARTNER_MAPPING", "SD_DELIVERY_ACTION", "SD_NUMERO_REFERENCIA", "SD_CODIGO_CONTRATO", "SD_GUARDIAN_FLOW_LITERAL"],
        FluxoMvp.MmInPreCadastro => ["MM_PARTNER_MAPPING", "MM_NUMERO_REFERENCIA", "MM_CODIGO_CONTRATO", "MM_GUARDIAN_FLOW_LITERAL"],
        FluxoMvp.MmIn101 => ["MM_TRIGGER_MOVIMENTO_101", "MM_MOVIMENTO_101_MAPPING"],
        FluxoMvp.AcmInContrato => ["ACM_ESTADO", "ACM_TIPO_LANCAMENTO", "ACM_VALOR_PROXIMIDADE_FIM", "ACM_CREATE_UPDATE_BEHAVIOR", "ACM_CONTRATO_GUARDIAN_MAPPING"],
        FluxoMvp.AcmInPesagem => ["ACM_TIPO_OPERACAO_SAP", "ACM_CONTRATO_ACM_TRANSFORMATION"],
        FluxoMvp.AcmOut => ["API7"],
        _ => throw new ArgumentOutOfRangeException(nameof(fluxo))
    };
}
