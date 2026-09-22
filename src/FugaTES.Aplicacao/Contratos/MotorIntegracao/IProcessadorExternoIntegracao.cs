using FugaTES.Aplicacao.Modelos.MotorIntegracao;

namespace FugaTES.Aplicacao.Contratos.MotorIntegracao;

public interface IProcessadorExternoIntegracao
{
    Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim, CancellationToken cancellationToken);
}
