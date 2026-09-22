using FugaTES.Aplicacao.Modelos.MotorIntegracao;

namespace FugaTES.Aplicacao.Contratos.MotorIntegracao;

public interface IPoliticaTimeoutProcessamentoIntegracao
{
    TimeSpan ObterTimeout(ClaimIntegracaoMotor claim);
}
