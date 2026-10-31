using FugaTES.Aplicacao.Modelos.Mm;

namespace FugaTES.WinForms.Mm;

public sealed class MmPurchaseOrderCollector(IPedidosCompraMmConsulta source,MmSnapshotStore store)
{
    private int busy;
    public async Task<bool> CycleAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(Interlocked.CompareExchange(ref busy,1,0)!=0)return false;
        MmPurchaseOrderSnapshot previous=new();
        try
        {
            previous=store.Read();
            var now=DateTimeOffset.UtcNow;
            // Persist attempt before GET. Failure here prevents a false successful publication.
            store.Write(previous with {LastAttemptUtc=now,State=previous.State is "MM_NOT_STARTED" or "MM_SAP_COMPLETE" ? "MM_COLLECTING" : previous.State,NextPollUtc=now.AddSeconds(30)});
            var result=await source.ConsultarAsync(ct);
            ct.ThrowIfCancellationRequested();
            var state=previous with {LastAttemptUtc=now,NextPollUtc=now.AddSeconds(30),State=result.Codigo,Http=result.Http,Pages=result.Paginas,Cycles=previous.Cycles+1};
            if(result.Sucesso)
            {
                var old=previous.Items.ToDictionary(x=>x.Pedido+"/"+x.Item,StringComparer.Ordinal);
                state=state with {LastSuccessUtc=DateTimeOffset.UtcNow,Items=result.Itens.ToArray(),
                    NewItems=result.Itens.Count(x=>!old.ContainsKey(x.Pedido+"/"+x.Item)),
                    ChangedItems=result.Itens.Count(x=>old.TryGetValue(x.Pedido+"/"+x.Item,out var before)&&before!=x)};
            }
            store.Write(state);return true;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch
        {
            // No exception text or SAP body enters the cache. Keep last complete business values.
            store.Write(previous with {LastAttemptUtc=DateTimeOffset.UtcNow,State="MM_COLLECTOR_UNAVAILABLE",NextPollUtc=DateTimeOffset.UtcNow.AddSeconds(30)});
            return true;
        }
        finally{Volatile.Write(ref busy,0);}
    }
    public async Task RunAsync(TimeSpan interval,CancellationToken ct)
    {
        if(interval<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(interval));
        // One sequential loop independent of any form; no queued overlapping work.
        while(true)
        {
            ct.ThrowIfCancellationRequested();var started=DateTimeOffset.UtcNow;
            await CycleAsync(ct);
            var delay=interval-(DateTimeOffset.UtcNow-started);
            if(delay<TimeSpan.FromMilliseconds(10))delay=interval;
            await Task.Delay(delay,ct);
        }
    }
}
