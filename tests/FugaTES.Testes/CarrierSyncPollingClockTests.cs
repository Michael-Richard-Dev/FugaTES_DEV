using FugaTES.HostIntegracao;
namespace FugaTES.Testes;
public sealed class CarrierSyncPollingClockTests
{
    [Fact] public async Task Immediate_first_cycle_then_exact_thirty_second_ticks()
    {
        var clock=new CarrierTestClock();using var stop=new CancellationTokenSource();var signals=new System.Threading.Channels.UnboundedChannelOptions{SingleReader=true};
        var ticks=System.Threading.Channels.Channel.CreateUnbounded<DateTimeOffset>(signals);
        var loop=new CarrierPollLoop(_=>{ticks.Writer.TryWrite(clock.GetUtcNow());return Task.CompletedTask;},clock);
        var task=loop.RunAsync(stop.Token);Assert.Equal(clock.GetUtcNow(),await Read(ticks.Reader));
        clock.Advance(TimeSpan.FromSeconds(29));Assert.False(ticks.Reader.TryRead(out _));
        clock.Advance(TimeSpan.FromSeconds(1));Assert.Equal(clock.GetUtcNow(),await Read(ticks.Reader));
        clock.Advance(TimeSpan.FromSeconds(30));Assert.Equal(clock.GetUtcNow(),await Read(ticks.Reader));
        stop.Cancel();await task.WaitAsync(TimeSpan.FromSeconds(3));
    }
    [Fact]public async Task Long_cycle_skips_ticks_without_overlap_or_backlog()
    {
        var clock=new CarrierTestClock();using var stop=new CancellationTokenSource();int calls=0,current=0,max=0;
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFinished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop=new CarrierPollLoop(async ct=>
        {
            var index=Interlocked.Increment(ref calls);max=Math.Max(max,Interlocked.Increment(ref current));
            if(index==1){await release.Task.WaitAsync(ct);Interlocked.Decrement(ref current);firstFinished.TrySetResult();}
            else{Interlocked.Decrement(ref current);secondStarted.TrySetResult();}
        },clock);
        var task=loop.RunAsync(stop.Token);Assert.Equal(1,calls);
        clock.Advance(TimeSpan.FromSeconds(30));await DrainContinuations();Assert.Equal(1,calls);
        clock.Advance(TimeSpan.FromSeconds(30));await DrainContinuations();Assert.Equal(1,calls);
        release.TrySetResult();await firstFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));await DrainContinuations();
        Assert.Equal(1,calls);clock.Advance(TimeSpan.FromSeconds(30));await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2,calls);Assert.Equal(1,max);stop.Cancel();await task.WaitAsync(TimeSpan.FromSeconds(3));
    }
    [Fact]public async Task Failed_cycle_is_diagnosed_once_and_next_tick_continues()
    {
        var clock=new CarrierTestClock();using var stop=new CancellationTokenSource();int calls=0;var diagnostics=new List<string>();
        var second=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop=new CarrierPollLoop(_=>{if(++calls==1)throw new InvalidOperationException("secret-like-message");second.TrySetResult();return Task.CompletedTask;},clock,diagnostics.Add);
        var task=loop.RunAsync(stop.Token);Assert.Equal("CARRIER_CYCLE_FAILED",Assert.Single(diagnostics));
        clock.Advance(TimeSpan.FromSeconds(30));await second.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(2,calls);
        stop.Cancel();await task.WaitAsync(TimeSpan.FromSeconds(3));
    }
    private static async Task<DateTimeOffset> Read(System.Threading.Channels.ChannelReader<DateTimeOffset> reader)=>await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    private static Task DrainContinuations()=>Task.Delay(30);
}
internal sealed class CarrierTestClock:TimeProvider
    {
        readonly List<VirtualTimer> timers=[];DateTimeOffset now=DateTimeOffset.Parse("2026-10-07T12:00:00Z");
        public override DateTimeOffset GetUtcNow()=>now;
        public override long GetTimestamp()=>now.UtcTicks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
        {
            var timer=new VirtualTimer(this,callback,state);timers.Add(timer);timer.Change(dueTime,period);return timer;
        }
        public void Advance(TimeSpan amount)
        {
            now+=amount;foreach(var timer in timers.ToArray())timer.FireDue(now.UtcTicks);
        }
        private sealed class VirtualTimer(CarrierTestClock clock,TimerCallback callback,object? state):ITimer
        {
            long next=long.MaxValue,periodTicks;bool disposed;
            public bool Change(TimeSpan dueTime,TimeSpan period)
            {
                if(disposed)return false;next=dueTime==Timeout.InfiniteTimeSpan?long.MaxValue:clock.now.UtcTicks+dueTime.Ticks;
                periodTicks=period==Timeout.InfiniteTimeSpan?0:period.Ticks;return true;
            }
            public void FireDue(long timestamp)
            {
                if(disposed||next>timestamp)return;
                while(!disposed&&next<=timestamp)
                {
                    next=periodTicks>0?next+periodTicks:long.MaxValue;callback(state);
                }
            }
            public void Dispose(){disposed=true;next=long.MaxValue;}
            public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
        }
    }

