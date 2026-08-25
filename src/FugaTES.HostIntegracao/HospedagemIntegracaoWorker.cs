namespace FugaTES.HostIntegracao;

public sealed class HospedagemIntegracaoWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }
}
