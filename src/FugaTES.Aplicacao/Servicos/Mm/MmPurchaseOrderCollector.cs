using System.Threading.Channels;
using FugaTES.Aplicacao.Modelos.Mm;
namespace FugaTES.Aplicacao.Servicos.Mm;
public sealed record MmGuardianReadback(string State,IReadOnlyList<RegistroProtocoloMm>? Records);
public interface IMmGuardianDispatcher
{
    Task<MmGuardianReadback> ProcessarAsync(IReadOnlyList<PedidoCompraMmLeitura> rows,CancellationToken ct,Func<bool> admission);
}
public class MmPurchaseOrderCollector
{
    private readonly IPedidosCompraMmConsulta source;private readonly IMmSnapshotStore store;private readonly IMmGuardianDispatcher? guardian;
    private readonly Channel<(long Version,IReadOnlyList<PedidoCompraMmLeitura> Rows)> queue=Channel.CreateBounded<(long,IReadOnlyList<PedidoCompraMmLeitura>)>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true,SingleWriter=true});
    private int busy;private long generation;
    private readonly Func<bool> admission;
    public MmPurchaseOrderCollector(IPedidosCompraMmConsulta source,IMmSnapshotStore store):this(source,store,null){}
    public MmPurchaseOrderCollector(IPedidosCompraMmConsulta source,IMmSnapshotStore store,IMmGuardianDispatcher? guardian,Func<bool>? admission=null)
    {this.source=source;this.store=store;this.guardian=guardian;this.admission=admission??(()=>true);}
    public async Task<bool> CycleAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();if(Interlocked.CompareExchange(ref busy,1,0)!=0)return false;
        var version=Interlocked.Increment(ref generation);
        try
        {
            var now=DateTimeOffset.UtcNow;
            store.Update(previous=>previous with{LastAttemptUtc=now,State=previous.State is "MM_NOT_STARTED" or "MM_SAP_COMPLETE"?"MM_COLLECTING":previous.State,NextPollUtc=now.Add(MmCollectionPolicy.Interval)});
            var result=await source.ConsultarAsync(ct);ct.ThrowIfCancellationRequested();
            store.Update(previous=>
            {
                var state=previous with{LastAttemptUtc=now,NextPollUtc=now.Add(MmCollectionPolicy.Interval),State=result.Codigo,Http=result.Http,Pages=result.Paginas,Cycles=previous.Cycles+1};
                if(!result.Sucesso)return state;
                var old=previous.Items.ToDictionary(x=>x.Pedido+"/"+x.Item,StringComparer.Ordinal);
                return state with{LastSuccessUtc=DateTimeOffset.UtcNow,Items=result.Itens.ToArray(),NewItems=result.Itens.Count(x=>!old.ContainsKey(x.Pedido+"/"+x.Item)),ChangedItems=result.Itens.Count(x=>old.TryGetValue(x.Pedido+"/"+x.Item,out var before)&&before!=x)};
            });
            if(result.Sucesso&&guardian is not null)queue.Writer.TryWrite((version,result.Itens.ToArray()));
            return true;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch
        {store.Update(previous=>previous with{LastAttemptUtc=DateTimeOffset.UtcNow,State="MM_COLLECTOR_UNAVAILABLE",NextPollUtc=DateTimeOffset.UtcNow.Add(MmCollectionPolicy.Interval)});return true;}
        finally{Volatile.Write(ref busy,0);}
    }
    public async Task RunAsync(TimeSpan interval,CancellationToken ct)
    {
        if(interval<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(interval));
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sender=guardian is null?Task.CompletedTask:ConsumeAsync(lifetime.Token);
        try
        {
            while(true)
            {ct.ThrowIfCancellationRequested();var started=DateTimeOffset.UtcNow;await CycleAsync(ct);var delay=interval-(DateTimeOffset.UtcNow-started);if(delay<TimeSpan.FromMilliseconds(10))delay=interval;await Task.Delay(delay,ct);}
        }
        finally
        {lifetime.Cancel();queue.Writer.TryComplete();try{await sender;}catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}}
    }
    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach(var work in queue.Reader.ReadAllAsync(ct))
        {
            bool Current()=>admission()&&work.Version==Volatile.Read(ref generation)&&store.Read().IsFresh(DateTimeOffset.UtcNow);
            if(!Current())continue;
            MmGuardianReadback result;
            try{result=await guardian!.ProcessarAsync(work.Rows,ct,Current);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch{result=new("MM_GUARDIAN_INDISPONIVEL",null);}
            store.Update(previous=>previous with{GuardianState=result.State,GuardianRecords=result.Records??previous.GuardianRecords,GuardianRevision=previous.GuardianRevision+1,RegistryReadUtc=result.Records is null?previous.RegistryReadUtc:DateTimeOffset.UtcNow});
        }
    }
}
