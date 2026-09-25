using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Aplicacao.Servicos.MotorIntegracao;

public sealed class ProcessadorPesagemSapMotor(
    IRepositorioCorrelacaoRecebimento correlacoes,
    IPesagemSapMvpGateway sap) : IProcessadorExternoIntegracao
{
    public async Task<ResultadoProcessamentoExterno> ProcessarAsync(
        ClaimIntegracaoMotor claim, CancellationToken cancellationToken)
    {
        if (claim.Operacao != GuardianPesagemSapMotorWork.Operacao || claim.Origem != "GUARDIAN" ||
            claim.Destino != "SAP" || string.IsNullOrWhiteSpace(claim.ChaveNegocio))
            return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);

        CorrelacaoRecebimentoMvp? correlacao;
        try
        {
            correlacao = await correlacoes.ObterPorProtocoloAsync(claim.ChaveNegocio, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ClassificacaoFalhaIntegracao.FalhaTransitoriaSegura);
        }

        if (correlacao is null || correlacao.ProtocoloGuardian != claim.ChaveNegocio ||
            string.IsNullOrWhiteSpace(correlacao.CallbackFingerprint) ||
            claim.FingerprintSha256 != GuardianPesagemSapMotorWork.Fingerprint(
                claim.ChaveNegocio, correlacao.CallbackFingerprint) ||
            correlacao.Pesos is null || string.IsNullOrWhiteSpace(correlacao.TicketGuardian) ||
            string.IsNullOrWhiteSpace(correlacao.Entrada.ContratoAcmSap) ||
            string.IsNullOrWhiteSpace(correlacao.Entrada.Centro) ||
            string.IsNullOrWhiteSpace(correlacao.Entrada.Material) ||
            string.IsNullOrWhiteSpace(correlacao.Entrada.BusinessPartnerTransportadora) ||
            string.IsNullOrWhiteSpace(correlacao.Entrada.BusinessPartnerEmissor))
            return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);

        var entrada = correlacao.Entrada;
        var pesos = correlacao.Pesos;
        var pesagem = new PesagemSapMvp("Toledo", entrada.Centro, "E", correlacao.TicketGuardian, 1,
            entrada.BusinessPartnerTransportadora, entrada.Material, entrada.BusinessPartnerEmissor,
            pesos.PesoBruto, pesos.Tara, pesos.PesoLiquido, entrada.TipoDocumento,
            entrada.PedidoCompras, entrada.DocumentoLogisticoReferencia, entrada.TipoOperacaoSap,
            entrada.ContratoAcmSap!, correlacao.ProtocoloGuardian, correlacao.ProtocoloCriadoEm);

        try
        {
            await sap.EnviarAsync(pesagem, cancellationToken);
            return ResultadoProcessamentoExterno.Sucesso;
        }
        catch (FalhaPesagemSapMvpException exception)
        {
            return new(exception.Classificacao);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ClassificacaoFalhaIntegracao.ResultadoIndeterminado);
        }
    }
}
