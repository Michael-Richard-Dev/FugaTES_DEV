using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.Monitor;
using FugaTES.Persistencia.MotorIntegracao;
using Npgsql;

namespace FugaTES.WinForms.Monitor;

internal static class MonitorComposition
{
    public static FrmMonitorMvp Criar()
    {
        NpgsqlDataSource? source = null;
        IMonitorReadRepository repository = new Desconectado();
        if (Runtime.UnifiedProfileGuard.RuntimeLegadoPermitido() is { } runtime)
        {
            source = runtime.CreateReadSource();
            repository = new MonitorPostgreSqlReadRepository(source);
        }
        return new(new MonitorOperacionalConsulta(repository),
            Runtime.DesktopRuntime.EnvironmentDisplay, source);
    }

    private sealed class Desconectado : IMonitorReadRepository
    {
        public Task<MonitorSnapshot> ConsultarAsync(MonitorFiltro filtro, CancellationToken ct)
            => throw new InvalidOperationException("Banco indisponivel.");
        public Task<MonitorDetalhe> DetalharAsync(Guid id, CancellationToken ct)
            => throw new InvalidOperationException("Banco indisponivel.");
    }
}
