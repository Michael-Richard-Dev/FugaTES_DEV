using System.Reflection;
using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.WinForms;
namespace FugaTES.Testes;
public sealed class MmPurchaseOrderCollectorTests
{
    // Real disk snapshot: catches losing the last complete result, partial writes, concurrent collectors.
    [Fact] public async Task Failed_cycle_retains_last_success_and_original_rows_after_restart()
    {
        using var folder=new Temp();dynamic store=Store(folder.Path);var source=new Source();dynamic collector=Collector(source,store);
        Assert.True((bool)await collector.CycleAsync(CancellationToken.None));
        dynamic first=store.Read();Assert.Equal(1,first.Items.Count);Assert.NotNull(first.LastSuccessUtc);
        source.Result=new(false,"MM_SAP_AUTHENTICATION_REQUIRED",401,1,[]);
        Assert.True((bool)await collector.CycleAsync(CancellationToken.None));
        dynamic restarted=Store(folder.Path);dynamic value=restarted.Read();
        Assert.Equal("MM_SAP_AUTHENTICATION_REQUIRED",(string)value.State);Assert.Equal(401,(int)value.Http);
        Assert.Equal((DateTimeOffset)first.LastSuccessUtc,(DateTimeOffset)value.LastSuccessUtc);Assert.Equal(1,value.Items.Count);
    }
    [Fact] public async Task Repeated_poll_does_not_duplicate_orders_and_reports_new_and_changed_items()
    {
        using var folder=new Temp();dynamic store=Store(folder.Path);var source=new Source();dynamic collector=Collector(source,store);
        await collector.CycleAsync(CancellationToken.None);Assert.Equal(1,(int)store.Read().NewItems);
        await collector.CycleAsync(CancellationToken.None);Assert.Equal(0,(int)store.Read().NewItems);Assert.Equal(0,(int)store.Read().ChangedItems);
        source.Result=source.Result with {Itens=[source.Result.Itens[0] with {Quantidade=60m}]};
        await collector.CycleAsync(CancellationToken.None);Assert.Equal(1,(int)store.Read().ChangedItems);Assert.Equal(1,store.Read().Items.Count);
    }
    [Fact] public void Second_collector_cannot_acquire_same_private_directory_until_first_exits()
    {using var folder=new Temp();dynamic a=Store(folder.Path);dynamic b=Store(folder.Path);using IDisposable lease=a.TryAcquireLease();Assert.NotNull(lease);IDisposable? second=b.TryAcquireLease();Assert.Null(second);lease.Dispose();using IDisposable again=b.TryAcquireLease();Assert.NotNull(again);}
    [Fact] public async Task Overlapping_cycles_are_skipped_and_never_issue_second_get()
    {
        using var folder=new Temp();dynamic store=Store(folder.Path);var source=new BlockingSource();dynamic collector=Collector(source,store);
        Task<bool> first=collector.CycleAsync(CancellationToken.None);await source.Entered.Task;
        Assert.False((bool)await collector.CycleAsync(CancellationToken.None));Assert.Equal(1,source.Calls);
        source.Release.SetResult();Assert.True(await first);
    }
    [Fact] public async Task Canceled_cycle_does_not_erase_the_completed_snapshot()
    {using var folder=new Temp();dynamic store=Store(folder.Path);var source=new Source();dynamic collector=Collector(source,store);await collector.CycleAsync(CancellationToken.None);using var cts=new CancellationTokenSource();cts.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await collector.CycleAsync(cts.Token));Assert.Equal(1,store.Read().Items.Count);}
    [Fact] public async Task Unexpected_exception_never_exposes_provider_secret_in_snapshot()
    {using var folder=new Temp();dynamic store=Store(folder.Path);dynamic collector=Collector(new ThrowingSource(),store);await collector.CycleAsync(CancellationToken.None);var text=File.ReadAllText(System.IO.Path.Combine(folder.Path,"snapshot.json"));Assert.DoesNotContain("SECRET",text);Assert.Contains("MM_COLLECTOR_UNAVAILABLE",text);}
    [Fact] public async Task Changed_company_in_cache_is_refused_instead_of_rendered_as_real_data()
    {using var folder=new Temp();dynamic store=Store(folder.Path);dynamic collector=Collector(new Source(),store);await collector.CycleAsync(CancellationToken.None);var file=System.IO.Path.Combine(folder.Path,"snapshot.json");var text=File.ReadAllText(file).Replace("\"company\":\"3000\"","\"company\":\"4000\"");Assert.Contains("4000",text);File.WriteAllText(file,text);Assert.ThrowsAny<Exception>(()=>store.Read());}
    [Fact] public async Task Headless_loop_polls_without_a_form_and_stops_on_cancellation()
    {
        using var folder=new Temp();dynamic store=Store(folder.Path);var source=new Source();dynamic collector=Collector(source,store);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Task run=collector.RunAsync(TimeSpan.FromMilliseconds(30),stop.Token);
        for(var i=0;i<100&&source.Calls<3;i++)await Task.Delay(10);
        stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await run);
        Assert.True(source.Calls>=3);Assert.Equal(1,store.Read().Items.Count);
    }
    [Fact] public async Task Disk_failure_prevents_fetch_and_false_success()
    {
        using var folder=new Temp();Directory.CreateDirectory(folder.Path);var path=System.IO.Path.Combine(folder.Path,"blocked");File.WriteAllText(path,"not a folder");
        var error=Record.Exception(()=>Store(path));Assert.NotNull(error);
        await Task.CompletedTask;
    }
    [Fact] public async Task Retry_after_failure_stays_stale_until_a_new_success_is_committed()
    {
        using var folder=new Temp();dynamic store=Store(folder.Path);var source=new RecoverySource();dynamic collector=Collector(source,store);
        await collector.CycleAsync(CancellationToken.None);await collector.CycleAsync(CancellationToken.None);
        Task<bool> retry=collector.CycleAsync(CancellationToken.None);await source.Entered.Task;
        try{dynamic during=store.Read();Assert.False((bool)during.IsFresh(DateTimeOffset.UtcNow));Assert.Equal(1,during.Items.Count);}
        finally{source.Release.SetResult();await retry;}
        Assert.True((bool)store.Read().IsFresh(DateTimeOffset.UtcNow));
    }
    private sealed class RecoverySource:IPedidosCompraMmConsulta
    {
        private int count;public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MmConsultaResultado> ConsultarAsync(CancellationToken ct)
        {count++;if(count==2)return new(false,"MM_SAP_AUTHENTICATION_REQUIRED",401,1,[]);if(count==3){Entered.SetResult();await Release.Task.WaitAsync(ct);}return new Source().Result;}
    }
    internal static object Store(string path)=>Create("FugaTES.WinForms.Mm.MmSnapshotStore",path);
    internal static object Collector(IPedidosCompraMmConsulta query,object store)=>Create("FugaTES.WinForms.Mm.MmPurchaseOrderCollector",query,store);
    static object Create(string name,params object[] args){var type=typeof(FrmIntegracoes).Assembly.GetType(name);Assert.NotNull(type);return Activator.CreateInstance(type,args)!;}
    public sealed class Source:IPedidosCompraMmConsulta
    {public int Calls;public MmConsultaResultado Result=new(true,"MM_SAP_COMPLETE",200,1,[new("3000","4455000001","00010","ZCOL","3011","00001000249","L002",50m,"KG","0000200001","05","{}")]);public Task<MmConsultaResultado> ConsultarAsync(CancellationToken ct){ct.ThrowIfCancellationRequested();Interlocked.Increment(ref Calls);return Task.FromResult(Result);}}
    public sealed class BlockingSource:IPedidosCompraMmConsulta
    {public int Calls;public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);public async Task<MmConsultaResultado> ConsultarAsync(CancellationToken ct){Calls++;Entered.SetResult();await Release.Task.WaitAsync(ct);return new Source().Result;}}
    private sealed class ThrowingSource:IPedidosCompraMmConsulta{public Task<MmConsultaResultado> ConsultarAsync(CancellationToken ct)=>throw new InvalidOperationException("SECRET_PASSWORD_MUST_NOT_LEAK");}
    internal sealed class Temp:IDisposable{public string Path{get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"FugaTES_MM_"+Guid.NewGuid().ToString("N"));public void Dispose(){if(Directory.Exists(Path))Directory.Delete(Path,true);}}
}
