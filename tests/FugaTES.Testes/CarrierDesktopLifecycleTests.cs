using FugaTES.WinForms.Transportadoras;
using FugaTES.Aplicacao.Transportadoras;
namespace FugaTES.Testes;
public class CarrierDesktopLifecycleTests
{
    static async Task Until(Func<bool> condition){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!condition())await Task.Delay(10,timeout.Token);}
    static CarrierDesktopSession Session(Func<CancellationToken,Task> cycle,Func<ValueTask>? dispose=null)=>new(cycle,(_,_,_,_)=>Task.FromResult(new FugaTES.HostIntegracao.CarrierManualResponse(false,"CARRIER_REQUEST_NOT_AUTHORIZED")),dispose);
    [Fact] public async Task Open_starts_once_after_ready_and_runs_every_thirty_seconds()
    {
        var clock=new CarrierTestClock();int calls=0,starts=0;var lifecycle=new CarrierDesktopLifecycle(_=>{Interlocked.Increment(ref starts);return Task.FromResult(Session(_=>{Interlocked.Increment(ref calls);return Task.CompletedTask;}));},clock);
        lifecycle.Start();lifecycle.Start();await Until(()=>calls==1);Assert.True(lifecycle.Snapshot.Active);Assert.Equal(1,starts);
        clock.Advance(TimeSpan.FromSeconds(29));await Task.Delay(30);Assert.Equal(1,calls);clock.Advance(TimeSpan.FromSeconds(1));await Until(()=>calls==2);
        clock.Advance(TimeSpan.FromSeconds(30));await Until(()=>calls==3);await lifecycle.StopAsync();Assert.False(lifecycle.Snapshot.Active);
        clock.Advance(TimeSpan.FromSeconds(300));await Task.Delay(30);Assert.Equal(3,calls);
    }
    [Fact] public async Task Slow_cycle_never_overlaps_and_close_waits_for_cleanup()
    {
        var clock=new CarrierTestClock();int calls=0;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);bool disposed=false;
        var lifecycle=new CarrierDesktopLifecycle(_=>Task.FromResult(Session(async ct=>{Interlocked.Increment(ref calls);entered.SetResult();try{await Task.Delay(Timeout.Infinite,ct);}finally{await release.Task;}},()=>{disposed=true;return ValueTask.CompletedTask;})),clock);
        lifecycle.Start();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));clock.Advance(TimeSpan.FromSeconds(60));await Task.Delay(30);Assert.Equal(1,calls);
        var stop=lifecycle.StopAsync();Assert.False(stop.IsCompleted);Assert.False(disposed);release.SetResult();await stop.WaitAsync(TimeSpan.FromSeconds(3));Assert.True(disposed);Assert.False(lifecycle.Snapshot.Active);
    }
    [Fact] public async Task Close_during_bootstrap_prevents_first_cycle()
    {
        var gate=new TaskCompletionSource<CarrierDesktopSession>(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;bool disposed=false;
        var lifecycle=new CarrierDesktopLifecycle(_=>gate.Task,TimeProvider.System);lifecycle.Start();var stop=lifecycle.StopAsync();
        gate.SetResult(Session(_=>{calls++;return Task.CompletedTask;},()=>{disposed=true;return ValueTask.CompletedTask;}));await stop.WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(0,calls);Assert.True(disposed);
    }
    [Fact] public async Task Missing_HML_profile_blocks_cycles_and_sanitizes_error()
    {
        var lifecycle=new CarrierDesktopLifecycle(_=>throw new InvalidOperationException("password=SECRET"),TimeProvider.System);lifecycle.Start();await lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(lifecycle.Snapshot.Active);Assert.Equal("CARRIER_DESKTOP_STARTUP_FAILED",lifecycle.Snapshot.Code);Assert.DoesNotContain("SECRET",lifecycle.Snapshot.ToString());await lifecycle.StopAsync();
    }
    [Fact] public void Write_gate_does_not_disable_read_discovery_or_controlled_scope()
    {
        var clock=new CarrierTestClock();var authorization=new CarrierSyncAuthorization("HML",true,true,clock.GetUtcNow().AddMinutes(5),["0001"],[],true){WritesEnabled=false};var policy=new CarrierSyncPolicy(authorization,clock);
        Assert.False(policy.PodeEscrever("0001"));Assert.True(policy.IncrementalReleased);Assert.Equal("0001",Assert.Single(policy.ControlledBps));
    }
}