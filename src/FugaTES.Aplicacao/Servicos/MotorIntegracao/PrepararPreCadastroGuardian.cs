using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Aplicacao.Servicos.MotorIntegracao;

public sealed class PrepararPreCadastroGuardian(
    IRepositorioCorrelacaoRecebimento correlacoes,
    IPreCadastroGuardianSet26Gateway guardian)
{
    public async Task<StatusAssociacaoProtocoloMvp> ExecutarAsync(
        CorrelacaoRecebimentoMvp correlacao, CancellationToken cancellationToken)
    {
        var entrada = correlacao.Entrada;
        if (new[] { entrada.Material, entrada.BusinessPartnerEmissor, entrada.BusinessPartnerTransportadora,
                entrada.Contrato, entrada.DocumentoLogisticoReferencia, entrada.TipoContrato, entrada.Centro,
                entrada.OperacaoContrato, entrada.UrlParaRetorno }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Guardian pre-registration context is incomplete.", nameof(correlacao));

        var preCadastro = new PreCadastroRecebimentoGuardianSet26(0, "FLX_RECEBIMENTO",
            entrada.Material, entrada.BusinessPartnerEmissor, entrada.BusinessPartnerTransportadora,
            entrada.Contrato, 1, entrada.DocumentoLogisticoReferencia, entrada.UrlParaRetorno!,
            [new(1000, entrada.TipoContrato), new(1002, entrada.Centro), new(1003, entrada.OperacaoContrato)]);

        var protocolo = await guardian.EnviarAsync(preCadastro, cancellationToken);
        if (string.IsNullOrWhiteSpace(protocolo.Valor) || protocolo.DataCadastro is null)
            throw new InvalidOperationException("Guardian pre-registration protocol is incomplete.");
        var associado = await correlacoes.AssociarProtocoloAsync(
            correlacao.DocumentoSap, protocolo.Valor, cancellationToken, protocolo.DataCadastro.Value);
        return associado.Status;
    }
}
