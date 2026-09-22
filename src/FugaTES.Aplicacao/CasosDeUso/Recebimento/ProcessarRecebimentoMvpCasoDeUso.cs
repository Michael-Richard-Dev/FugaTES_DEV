using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Aplicacao.CasosDeUso.Recebimento;

public sealed class ProcessarRecebimentoMvpCasoDeUso(IPreCadastroGuardianSet26Gateway guardian, IRepositorioCorrelacaoRecebimento correlacoes, GuardianSet26Options opcoes)
{
    public async Task<ResultadoRecebimentoMvp> ExecutarAsync(RecebimentoSapCpiMvp entrada, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        Validar(entrada, opcoes);
        var snapshot = await correlacoes.RegistrarSnapshotAsync(entrada, cancellationToken);
        if (snapshot.Status == StatusRegistroSnapshotMvp.Conflito) throw new InvalidOperationException("Conflicting receipt snapshot was rejected.");
        if (!string.IsNullOrWhiteSpace(entrada.ContratoAcmSap))
        {
            var contratoAcm = await correlacoes.AssociarContratoAcmAsync(entrada.DocumentoLogisticoReferencia, entrada.ContratoAcmSap, cancellationToken);
            if (contratoAcm.Status == StatusAssociacaoContratoAcmMvp.Conflito) throw new InvalidOperationException("Conflicting ACM contract was rejected.");
            snapshot = snapshot with { Correlacao = contratoAcm.Correlacao };
        }
        if (snapshot.Status == StatusRegistroSnapshotMvp.Idempotente && !string.IsNullOrWhiteSpace(snapshot.Correlacao.ProtocoloGuardian))
            return new(entrada.DocumentoLogisticoReferencia, snapshot.Correlacao.ProtocoloGuardian);
        var preCadastro = new PreCadastroRecebimentoGuardianSet26(0, "FLX_RECEBIMENTO", entrada.Material, entrada.BusinessPartnerEmissor, entrada.BusinessPartnerTransportadora, entrada.Contrato, 1, entrada.DocumentoLogisticoReferencia, opcoes.UrlParaRetorno, [new(1000, entrada.TipoContrato), new(1002, entrada.Centro), new(1003, entrada.OperacaoContrato)]);
        var protocolo = await guardian.EnviarAsync(preCadastro, cancellationToken);
        if (string.IsNullOrWhiteSpace(protocolo.Valor)) throw new InvalidOperationException("Guardian protocol is absent.");
        var associacao = await correlacoes.AssociarProtocoloAsync(entrada.DocumentoLogisticoReferencia, protocolo.Valor, cancellationToken);
        if (associacao.Status == StatusAssociacaoProtocoloMvp.Conflito) throw new InvalidOperationException("Conflicting Guardian protocol was rejected.");
        return new(entrada.DocumentoLogisticoReferencia, protocolo.Valor);
    }

    private static void Validar(RecebimentoSapCpiMvp entrada, GuardianSet26Options opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        var obrigatorios = new[] { entrada.Processo, entrada.CodigoFluxo, entrada.Material, entrada.BusinessPartnerEmissor, entrada.BusinessPartnerTransportadora, entrada.Contrato, entrada.DocumentoLogisticoReferencia, entrada.TipoContrato, entrada.Centro, entrada.OperacaoContrato, entrada.TipoDocumento, opcoes.EndpointEspecifico, opcoes.UrlParaRetorno };
        if (obrigatorios.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Required receipt field is absent.");
        if (entrada.Processo != "RECEBIMENTO" || entrada.CodigoFluxo != "FLX_RECEBIMENTO") throw new ArgumentException("Receipt process or flow is invalid.");
    }
}
