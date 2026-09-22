using FugaTES.Aplicacao.Modelos.MotorIntegracao;

namespace FugaTES.Aplicacao.Contratos.MotorIntegracao;

// Validacao estrutural pre-claim (S3). Offline e deterministica: nao consulta SAP/Toledo,
// nao aplica regra empresarial externa, nao inventa enum fechado de operacoes.
public interface IValidadorIntegracao
{
    ResultadoValidacaoIntegracao Validar(IntegracaoMotor integracao);
}
