using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;

namespace FugaTES.HostIntegracao;

public sealed class ProcessadorExternoIntegracaoRoteado(
    ProcessadorPreCadastroGuardianMotor guardian, ProcessadorPesagemSapMotor sap)
    : IProcessadorExternoIntegracao
{
    public Task<ResultadoProcessamentoExterno> ProcessarAsync(
        ClaimIntegracaoMotor claim, CancellationToken cancellationToken) =>
        claim.Operacao switch
        {
            GuardianPreCadastroMotorWork.Operacao => guardian.ProcessarAsync(claim, cancellationToken),
            GuardianPesagemSapMotorWork.Operacao => sap.ProcessarAsync(claim, cancellationToken),
            _ => Task.FromResult(new ResultadoProcessamentoExterno(ClassificacaoFalhaIntegracao.FalhaDefinitiva))
        };
}
