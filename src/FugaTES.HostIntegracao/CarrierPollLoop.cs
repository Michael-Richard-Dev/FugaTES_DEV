namespace FugaTES.HostIntegracao;
public sealed class CarrierPollLoop(Func<CancellationToken,Task> cycle,TimeProvider time,Action<string>? diagnostic=null)
{
    int running;
    public static TimeSpan Interval => TimeSpan.FromSeconds(30);
    public async Task<bool> TryRunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(Interlocked.CompareExchange(ref running,1,0)!=0)return false;
        try {await cycle(ct);return true;}finally{Volatile.Write(ref running,0);}
    }
    async Task SafeTickAsync(CancellationToken ct)
    {
        try {await TryRunAsync(ct);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){}
        catch {diagnostic?.Invoke("CARRIER_CYCLE_FAILED");}
    }
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer=new PeriodicTimer(Interval,time);
        Task active=SafeTickAsync(ct);
        try
        {
            while(await timer.WaitForNextTickAsync(ct))
                if(active.IsCompleted)active=SafeTickAsync(ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){}
        finally {await active;}
    }
}