using FugaTES.Aplicacao.Modelos.MotorIntegracao;

namespace FugaTES.Aplicacao.Contratos.MotorIntegracao;

public interface IIdentidadeWorkerProvider
{
    IdentidadeWorkerIntegracao Obter();
}
