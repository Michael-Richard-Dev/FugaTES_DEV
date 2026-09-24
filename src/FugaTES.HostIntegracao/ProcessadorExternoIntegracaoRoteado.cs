using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;

namespace FugaTES.HostIntegracao;

public sealed class ProcessadorExternoIntegracaoRoteado(ProcessadorPreCadastroGuardianMotor guardian)
    : IProcessadorExternoIntegracao
{
    public Task<ResultadoProcessamentoExterno> ProcessarAsync(
        ClaimIntegracaoMotor claim, CancellationToken cancellationToken) =>
        claim.Operacao == GuardianPreCadastroMotorWork.Operacao
            ? guardian.ProcessarAsync(claim, cancellationToken)
            : Task.FromResult(new ResultadoProcessamentoExterno(ClassificacaoFalhaIntegracao.FalhaDefinitiva));
}
