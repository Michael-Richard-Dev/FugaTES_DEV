using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Aplicacao.Servicos.MotorIntegracao;

public sealed class ProcessadorPreCadastroGuardianMotor(
    IRepositorioCorrelacaoRecebimento correlacoes,
    IPreCadastroGuardianSet26Gateway guardian) : IProcessadorExternoIntegracao
{
    private readonly PrepararPreCadastroGuardian preparar = new(correlacoes, guardian);

    public async Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim, CancellationToken cancellationToken)
    {
        if (claim.Operacao != GuardianPreCadastroMotorWork.Operacao || claim.Origem != "SAP" ||
            claim.Destino != "GUARDIAN" ||
            claim.FingerprintSha256 != GuardianPreCadastroMotorWork.Fingerprint(claim.ChaveNegocio))
            return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);

        CorrelacaoRecebimentoMvp? correlacao;
        try
        {
            correlacao = await correlacoes.ObterPorDocumentoAsync(claim.ChaveNegocio, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ClassificacaoFalhaIntegracao.FalhaTransitoriaSegura);
        }

        if (correlacao is null || correlacao.DocumentoSap != claim.ChaveNegocio)
            return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);
        if (!string.IsNullOrWhiteSpace(correlacao.ProtocoloGuardian) || correlacao.ProtocoloCriadoEm is not null)
            return !string.IsNullOrWhiteSpace(correlacao.ProtocoloGuardian) && correlacao.ProtocoloCriadoEm is not null
                ? ResultadoProcessamentoExterno.Sucesso
                : new(ClassificacaoFalhaIntegracao.ResultadoIndeterminado);

        var entrada = correlacao.Entrada;
        if (new[] { entrada.Material, entrada.BusinessPartnerEmissor, entrada.BusinessPartnerTransportadora,
                entrada.Contrato, entrada.DocumentoLogisticoReferencia, entrada.TipoContrato, entrada.Centro,
                entrada.OperacaoContrato, entrada.UrlParaRetorno }.Any(string.IsNullOrWhiteSpace))
            return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);

        try
        {
            var status = await preparar.ExecutarAsync(correlacao, cancellationToken);
            return status is StatusAssociacaoProtocoloMvp.Associado or StatusAssociacaoProtocoloMvp.Idempotente
                ? ResultadoProcessamentoExterno.Sucesso
                : new(ClassificacaoFalhaIntegracao.ResultadoIndeterminado);
        }
        catch (Exception)
        {
            return new(ClassificacaoFalhaIntegracao.ResultadoIndeterminado);
        }
    }
}
