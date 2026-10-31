using FugaTES.HostIntegracao;
namespace FugaTES.Testes;
public class CarrierSyncWorkerTests
{
    [Fact] public void Dedicated_interval_is_30_seconds() => Assert.Equal(TimeSpan.FromSeconds(30),CarrierPollLoop.Interval);
    [Fact] public async Task Concurrent_tick_does_not_overlap()
    {
        var entered=new TaskCompletionSource();var release=new TaskCompletionSource();var calls=0;
        var loop=new CarrierPollLoop(async ct=>{Interlocked.Increment(ref calls);entered.SetResult();await release.Task.WaitAsync(ct);},TimeProvider.System);
        var first=loop.TryRunAsync(default);await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.False(await loop.TryRunAsync(default));release.SetResult();Assert.True(await first);Assert.Equal(1,calls);
    }
    [Fact] public async Task Next_cycle_runs_after_completion()
    {var calls=0;var loop=new CarrierPollLoop(_=>{calls++;return Task.CompletedTask;},TimeProvider.System);Assert.True(await loop.TryRunAsync(default));Assert.True(await loop.TryRunAsync(default));Assert.Equal(2,calls);}
    [Fact] public async Task Cancellation_does_not_leave_busy_flag()
    {var fail=true;var loop=new CarrierPollLoop(ct=>{if(fail)throw new OperationCanceledException();return Task.CompletedTask;},TimeProvider.System);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>loop.TryRunAsync(default));fail=false;Assert.True(await loop.TryRunAsync(default));}
}