using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;

namespace FugaTES.Infraestrutura.MotorIntegracao;

public sealed class IdentidadeWorkerRuntimeProvider : IIdentidadeWorkerProvider
{
    private readonly IdentidadeWorkerIntegracao identidade = new($"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}");

    public IdentidadeWorkerIntegracao Obter()
    {
        return identidade;
    }
}
