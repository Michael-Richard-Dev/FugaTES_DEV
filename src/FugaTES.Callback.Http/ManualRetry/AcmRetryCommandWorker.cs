using System.Threading.Channels;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.HostIntegracao;

namespace FugaTES.Callback.Http.ManualRetry;

public sealed record CommittedAcmRetry(Guid CommandId, AcmRetryApproval Approval);
public sealed class AcmRetryDispatchQueue
{
    readonly Channel<CommittedAcmRetry> queue = Channel.CreateBounded<CommittedAcmRetry>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    public void Schedule(CommittedAcmRetry command)
    {
        if (!queue.Writer.TryWrite(command)) throw new InvalidOperationException("ACM_COMMAND_WAKE_UNVERIFIED_NO_RETRY");
    }
    public IAsyncEnumerable<CommittedAcmRetry> ReadAllAsync(CancellationToken ct) => queue.Reader.ReadAllAsync(ct);
}

// Only a committed, matching QUEUED receipt may wake the one-shot worker.
public sealed class CommittedAcmRetryStore(IAcmManualRetryStore inner, AcmRetryDispatchQueue queue) : IAcmManualRetryStore
{
    public Task<AcmRetrySnapshot?> ReadAsync(Guid id, CancellationToken ct) => inner.ReadAsync(id, ct);
    public async Task<AcmRetryAdmission> AdmitAsync(AcmRetryRequest r, AcmRetryActor a, AcmRetryApproval p, DateTimeOffset now, CancellationToken ct)
    {
        var receipt = await inner.AdmitAsync(r, a, p, now, ct);
        if (receipt.Accepted && receipt.CommandId == r.CommandId && receipt.IntegrationId == r.IntegrationId)
            queue.Schedule(new(r.CommandId, p));
        return receipt;
    }
    public Task<AcmRetryWork?> StartAsync(Guid id, AcmRetryApproval a, string worker, DateTimeOffset now, CancellationToken ct) => inner.StartAsync(id, a, worker, now, ct);
    public Task CompleteAsync(AcmRetryWork w, string result, DateTimeOffset now, CancellationToken ct) => inner.CompleteAsync(w, result, now, ct);
    public Task<Guid?> PendingCommandAsync(AcmRetryApproval a, CancellationToken ct) => inner.PendingCommandAsync(a, ct);
}

public interface IAcmApprovedRetryExecutor
{
    Task<string> ExecuteAsync(CommittedAcmRetry command, CancellationToken ct);
}
public sealed class AcmApprovedRetryExecutor(IServiceScopeFactory scopes, AcmManualRetryCommandProfile profile) : IAcmApprovedRetryExecutor
{
    public async Task<string> ExecuteAsync(CommittedAcmRetry command, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var worker = new AcmManualRetryWorker(scope.ServiceProvider.GetRequiredService<IAcmManualRetryStore>(),
            scope.ServiceProvider.GetRequiredService<MotorIntegracaoServico>(), scope.ServiceProvider.GetRequiredService<TimeProvider>(), globalWorkerEnabled: false);
        return await worker.ExecuteApprovedAsync(command.CommandId, command.Approval, profile.WorkerIdentity, ct);
    }
}

public sealed class AcmRetryCommandWorker(AcmRetryDispatchQueue queue, IAcmApprovedRetryExecutor executor, ILogger<AcmRetryCommandWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var command in queue.ReadAllAsync(stoppingToken))
            {
                try { var outcome = await executor.ExecuteAsync(command, stoppingToken); logger.LogInformation("Dedicated ACM retry finished: {Outcome}", outcome); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch { logger.LogError("ACM_COMMAND_DISPATCH_UNVERIFIED_NO_RETRY"); }
                // No polling, backlog recovery, replay, or second dispatch after any outcome.
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
