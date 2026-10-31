using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.WinForms.Transportadoras;
using FugaTES.WinForms.Transportadoras.MasterdataR3;
namespace FugaTES.PagedCoordinator.R20C.Tests;
public class MasterdataPagedCoordinatorR20CTests
{
    static readonly DateTimeOffset Now=new(2026,10,11,12,0,0,TimeSpan.Zero);
    sealed class Clock:TimeProvider
    {
        public DateTimeOffset NowUtc=Now;public int Timers;
        public override DateTimeOffset GetUtcNow()=>NowUtc;
        public override ITimer CreateTimer(TimerCallback cb,object? state,TimeSpan due,TimeSpan period){Timers++;throw new Exception("NO_TIMER_ALLOWED");}
    }
    static string Wire(DateTimeOffset d)=>"/Date("+d.ToUnixTimeMilliseconds()+")/";
    static SapRoleRowR20A Row(string bp,int to=30)=>new(bp,"CRM010",Wire(Now.AddDays(-30)),Wire(Now.AddDays(to)));
    sealed class Pages:ISapRolePageReaderR20A
    {
        public List<SapRoleRowR20A> Rows=[];public List<int> Skips=[];public bool ThrowOnce,Wait;
        public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CancellationToken,Task>? Hook;
        public async Task<SapRolePageR20A> ReadAsync(SapRolePageRequestR20A r,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Skips.Add(r.Skip);Entered.TrySetResult();
            if(Hook is not null)await Hook(ct);
            if(Wait)await Task.Delay(Timeout.Infinite,ct);
            if(ThrowOnce){ThrowOnce=false;throw new Exception("DO_NOT_EXPOSE_PRIVATE_ERROR");}
            return new(Rows.Skip(r.Skip).Take(r.Top).ToArray());
        }
    }
    sealed class Bps:ISapBpMasterReaderR20B
    {
        public List<string> Calls=[];public string Name="Original";public bool FailOnce;
        public Func<CancellationToken,Task>? Hook;
        public async Task<SapBpReadResultR20B> ReadAsync(string bp,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Calls.Add(bp);if(Hook is not null)await Hook(ct);
            if(FailOnce){FailOnce=false;throw new Exception("DO_NOT_EXPOSE_PRIVATE_ERROR");}
            return new(bp,JsonSerializer.SerializeToElement(new{BusinessPartner=bp,BusinessPartnerName=Name,BusinessPartnerFullName="Full "+Name}));
        }
    }
    sealed class Harness
    {
        public Clock Clock=new();public Pages Pages=new();public Bps Bps=new();public CarrierDesktopLifecycle Lifecycle;
        public MasterdataPagedReconciliationCoordinatorR20C Coordinator;
        public bool Approved=true,AppOpen=true;public int FactoryCalls,ManualCalls;
        public Harness(bool authorization=true,int pageQuota=1,int bpQuota=1)
        {
            Lifecycle=new(_=>{FactoryCalls++;throw new Exception("FACTORY_MUST_NOT_START");},Clock,
                (bp,ct)=>{ManualCalls++;return Task.FromResult(new ResultadoCarrierManual(bp,DecisaoCarrierManual.INDETERMINATE,"OFFLINE_READ_ONLY"));});
            var policy=new SapBpComparisonPolicyR20B([SapBpFieldR20B.BusinessPartner,SapBpFieldR20B.BusinessPartnerName,SapBpFieldR20B.BusinessPartnerFullName],true,true,"OFFLINE_SCOPE",temporalContract:SapRoleTemporalContractR20B.EdmDateTimeOffset);
            Coordinator=new(Lifecycle.Masterdata,Pages,Bps,policy,new(pageQuota,bpQuota,new(1,30,100)),
                authorization?()=>new(Approved,AppOpen,true,"HML","110","OFFLINE_HERA_APPROVAL",Now.AddYears(1)):null,Clock);
        }
        public Task<ResultadoCarrierManual> Manual()=>Lifecycle.ConsultarManualReadOnlyAsync("0001",default);
        public async Task<MasterdataReconciliationCycleResultR20C> Finish()
        {
            for(int n=0;n<40;n++)
            {
                var r=await Coordinator.ExecuteNextCycleAsync();
                if(r.State!=MasterdataReconciliationCycleStateR20C.InProgress)return r;
            }
            throw new Exception("SCAN_DID_NOT_COMPLETE");
        }
    }
    [Fact] public async Task MULTI_CYCLE_COMPLETION()
    {
        var h=new Harness();h.Pages.Rows=[Row("0001"),Row("0002"),Row("0003")];
        var first=await h.Coordinator.ExecuteNextCycleAsync();Assert.Equal(MasterdataReconciliationCycleStateR20C.InProgress,first.State);
        var id=first.ScanId;Assert.Null(h.Coordinator.ConsolidatedSnapshot);
        var second=await h.Coordinator.ExecuteNextCycleAsync();Assert.Equal(id,second.ScanId);
        var done=await h.Finish();Assert.Equal(MasterdataReconciliationCycleStateR20C.Promoted,done.State);
        Assert.Equal(new[]{0,1,2,3},h.Pages.Skips);Assert.Equal(3,h.Coordinator.ConsolidatedSnapshot!.Entries.Count);
    }
    [Fact] public async Task BOUNDED_PAGE_PROCESSING()
    {
        var h=new Harness(pageQuota:2,bpQuota:1);h.Pages.Rows=Enumerable.Range(1,5).Select(n=>Row("000"+n)).ToList();
        for(int i=0;i<12;i++)
        {
            int pages=h.Pages.Skips.Count,bps=h.Bps.Calls.Count;var r=await h.Coordinator.ExecuteNextCycleAsync();
            Assert.InRange(h.Pages.Skips.Count-pages,0,2);Assert.InRange(h.Bps.Calls.Count-bps,0,1);
            Assert.InRange(r.NewPages,0,2);Assert.InRange(r.NewBps,0,1);
            if(r.State==MasterdataReconciliationCycleStateR20C.Promoted)break;
        }
        Assert.NotNull(h.Coordinator.ConsolidatedSnapshot);
    }
    [Fact] public async Task SINGLE_ACTIVE_SCAN()
    {
        var h=new Harness();h.Pages.Wait=true;var first=h.Coordinator.ExecuteNextCycleAsync();
        await h.Pages.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var busy=await h.Coordinator.ExecuteNextCycleAsync();Assert.Equal(MasterdataReconciliationCycleStateR20C.Busy,busy.State);Assert.Single(h.Pages.Skips);
        await h.Lifecycle.StopAsync();await first;
    }
    [Fact] public async Task MANUAL_AUTOMATIC_SHARED_LOCK()
    {
        var h=new Harness();h.Pages.Wait=true;using var cts=new CancellationTokenSource();var automatic=h.Coordinator.ExecuteNextCycleAsync(cts.Token);
        await h.Pages.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Same(h.Lifecycle.Masterdata,h.Coordinator.SharedOrchestrator);
        Assert.Equal("SHARED_LOCK_BUSY",(await h.Manual()).Motivo);Assert.Equal(0,h.ManualCalls);
        cts.Cancel();await automatic;Assert.Equal("OFFLINE_READ_ONLY",(await h.Manual()).Motivo);
    }
    [Fact] public async Task MANUAL_OWNERSHIP_BLOCKS_AUTOMATIC()
    {
        var h=new Harness();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manual=h.Lifecycle.Masterdata.ConsultarReadOnlyAsync("0001",async ct=>{entered.SetResult();await release.Task.WaitAsync(ct);return new("0001",DecisaoCarrierManual.INDETERMINATE,"OFFLINE_READ_ONLY");},default);
        await entered.Task;Assert.Equal(MasterdataReconciliationCycleStateR20C.Busy,(await h.Coordinator.ExecuteNextCycleAsync()).State);Assert.Empty(h.Pages.Skips);
        release.SetResult();await manual;
    }
    [Fact] public async Task LOCK_RELEASE_ON_EXCEPTION()
    {
        var h=new Harness();h.Pages.ThrowOnce=true;
        var result=await h.Coordinator.ExecuteNextCycleAsync();Assert.Equal(MasterdataReconciliationCycleStateR20C.Failed,result.State);
        Assert.DoesNotContain("PRIVATE",result.ToString());Assert.Equal("OFFLINE_READ_ONLY",(await h.Manual()).Motivo);
    }
    [Fact] public async Task LOCK_RELEASE_ON_CANCEL()
    {
        var h=new Harness();h.Pages.Wait=true;using var cts=new CancellationTokenSource();var task=h.Coordinator.ExecuteNextCycleAsync(cts.Token);
        await h.Pages.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));cts.Cancel();var result=await task;
        Assert.Equal(MasterdataReconciliationCycleStateR20C.Canceled,result.State);Assert.Equal("OFFLINE_READ_ONLY",(await h.Manual()).Motivo);
    }
    [Fact] public async Task LOCK_RELEASE_ON_TIMEOUT()
    {
        var h=new Harness();h.Pages.Wait=true;using var deadline=new CancellationTokenSource();var task=h.Coordinator.ExecuteNextCycleAsync(deadlineToken:deadline.Token);
        await h.Pages.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));deadline.Cancel();var result=await task;
        Assert.Equal(MasterdataReconciliationCycleStateR20C.TimedOut,result.State);Assert.Equal("OFFLINE_READ_ONLY",(await h.Manual()).Motivo);
    }
    [Fact] public async Task APP_CLOSE_CANCELLATION()
    {
        var h=new Harness();h.Pages.Wait=true;var task=h.Coordinator.ExecuteNextCycleAsync();
        await h.Pages.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));await h.Lifecycle.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));await task;
        Assert.True(task.IsCompleted);var calls=h.Pages.Skips.Count;
        Assert.Equal(MasterdataReconciliationCycleStateR20C.AppClosed,(await h.Coordinator.ExecuteNextCycleAsync()).State);
        Assert.Equal(calls,h.Pages.Skips.Count);Assert.Equal(0,h.FactoryCalls);
    }
    [Fact] public async Task INCOMPLETE_SNAPSHOT_NOT_PROMOTED()
    {
        var h=new Harness();h.Pages.Rows=[Row("0001")];await h.Finish();var original=h.Coordinator.ConsolidatedSnapshot;
        h.Pages.Rows.Add(Row("0002"));var partial=await h.Coordinator.ExecuteNextCycleAsync();
        Assert.Equal(MasterdataReconciliationCycleStateR20C.InProgress,partial.State);Assert.Same(original,h.Coordinator.ConsolidatedSnapshot);
        Assert.Empty(partial.Candidates);Assert.Equal("OFFLINE_READ_ONLY",(await h.Manual()).Motivo);
    }
    [Fact] public async Task COMPLETE_SNAPSHOT_PROMOTION()
    {
        var h=new Harness();h.Pages.Rows=[Row("0001")];await h.Finish();var first=h.Coordinator.ConsolidatedSnapshot;
        h.Bps.Name="Changed";h.Clock.NowUtc=Now.AddMinutes(1);var done=await h.Finish();
        Assert.NotSame(first,h.Coordinator.ConsolidatedSnapshot);Assert.Contains(SapBpChangeKindR20B.BP_MASTERDATA_CHANGED,Assert.Single(done.Candidates).Changes);
        Assert.Equal("OFFLINE_READ_ONLY",(await h.Manual()).Motivo);
    }
    [Fact] public async Task FIRST_COMPLETE_SCAN_ESTABLISHES_BASELINE_WITHOUT_INVENTED_PRIOR_SNAPSHOT()
    {
        var h=new Harness();h.Pages.Rows=[Row("0001")];var done=await h.Finish();Assert.Empty(done.Candidates);Assert.NotNull(h.Coordinator.ConsolidatedSnapshot);
    }
    [Fact] public async Task ENRICHMENT_FAILURE_FAIL_CLOSED()
    {
        var h=new Harness();h.Pages.Rows=[Row("0001")];await h.Finish();var previous=h.Coordinator.ConsolidatedSnapshot;
        h.Bps.FailOnce=true;var failed=await h.Finish();Assert.Equal(MasterdataReconciliationCycleStateR20C.Failed,failed.State);Assert.Same(previous,h.Coordinator.ConsolidatedSnapshot);
        Assert.Equal("OFFLINE_READ_ONLY",(await h.Manual()).Motivo);
    }
    [Fact] public async Task NEXT_CYCLE_AFTER_FAILURE()
    {
        var h=new Harness();h.Pages.ThrowOnce=true;Assert.Equal(MasterdataReconciliationCycleStateR20C.Failed,(await h.Coordinator.ExecuteNextCycleAsync()).State);
        Assert.Equal(MasterdataReconciliationCycleStateR20C.Promoted,(await h.Finish()).State);Assert.Equal(new[]{0,0},h.Pages.Skips);
    }
    [Fact] public async Task ROLE_EXPIRATION_REEVALUATED()
    {
        var h=new Harness();h.Pages.Rows=[Row("0001",to:1)];await h.Finish();h.Clock.NowUtc=Now.AddDays(2);
        Assert.Contains(SapBpChangeKindR20B.ELIGIBILITY_EXPIRED,Assert.Single((await h.Finish()).Candidates).Changes);
    }
    [Fact] public async Task GUARDIAN_WRITE_ZERO()
    {
        var h=new Harness();await h.Finish();h.Pages.Rows=[Row("0001")];h.Clock.NowUtc=Now.AddMinutes(1);
        var done=await h.Finish();Assert.False(done.GuardianWritesAuthorized);
        Assert.Contains(SapBpChangeKindR20B.NEW_ELIGIBLE_BP,Assert.Single(done.Candidates).Changes);
        Assert.All(done.Candidates,x=>{Assert.False(x.GuardianWriteAuthorized);Assert.False(x.GuardianDeleteOrDeactivate);});
    }
    [Fact] public async Task DEFAULT_RUNTIME_INACTIVE()
    {
        var h=new Harness(authorization:false);Assert.False(h.Coordinator.RuntimeActivation);
        Assert.Equal(MasterdataReconciliationCycleStateR20C.Inactive,(await h.Coordinator.ExecuteNextCycleAsync()).State);
        Assert.Empty(h.Pages.Skips);Assert.Empty(h.Bps.Calls);
    }
    [Fact] public async Task NO_SECOND_TIMER_OR_WORKER()
    {
        var h=new Harness();Assert.Equal(TimeSpan.FromSeconds(30),MasterdataPagedReconciliationCoordinatorR20C.Interval);
        await h.Finish();Assert.Equal(0,h.Clock.Timers);Assert.Equal(0,h.FactoryCalls);Assert.False(h.Lifecycle.Snapshot.Active);
    }
    [Fact] public async Task NO_REAL_HTTP_OR_SQL()
    {
        var h=new Harness();h.Pages.Rows=[Row("0001")];var done=await h.Finish();Assert.Equal(MasterdataReconciliationCycleStateR20C.Promoted,done.State);
        Assert.Equal(2,h.Pages.Skips.Count);Assert.Single(h.Bps.Calls);Assert.False(done.DurableCheckpoint);Assert.False(done.GuardianWritesAuthorized);
    }
    [Fact] public async Task AUTHORIZATION_REVOKED_WITHIN_CYCLE_BLOCKS_PROMOTION()
    {
        var h=new Harness(pageQuota:2);h.Pages.Hook=_=>{h.Approved=false;return Task.CompletedTask;};
        var result=await h.Coordinator.ExecuteNextCycleAsync();Assert.Equal(MasterdataReconciliationCycleStateR20C.Blocked,result.State);Assert.Null(h.Coordinator.ConsolidatedSnapshot);
    }
    [Fact] public async Task INVALID_SCAN_DUPLICATE_FAILS_CLOSED()
    {
        var h=new Harness();h.Pages.Rows=[Row("0001"),Row("0001")];var failed=await h.Finish();Assert.Equal(MasterdataReconciliationCycleStateR20C.Failed,failed.State);Assert.Null(h.Coordinator.ConsolidatedSnapshot);
    }
    [Fact] public async Task LOCK_BRIDGE_RELEASES_ON_THROWN_CALLBACK()
    {
        var h=new Harness();await Assert.ThrowsAsync<InvalidOperationException>(()=>h.Lifecycle.Masterdata.ExecutarConsultaProtegidaAsync<int>(_=>throw new InvalidOperationException("OFFLINE_EXCEPTION")));
        Assert.Equal("OFFLINE_READ_ONLY",(await h.Manual()).Motivo);
    }
    [Fact] public async Task PRE_CANCELED_PENDING_SCAN_RESTARTS_FROM_FIRST_PAGE()
    {
        var h=new Harness();h.Pages.Rows=[Row("0001")];
        Assert.Equal(MasterdataReconciliationCycleStateR20C.InProgress,(await h.Coordinator.ExecuteNextCycleAsync()).State);
        using var cts=new CancellationTokenSource();cts.Cancel();
        Assert.Equal(MasterdataReconciliationCycleStateR20C.Canceled,(await h.Coordinator.ExecuteNextCycleAsync(cts.Token)).State);
        Assert.Equal(MasterdataReconciliationCycleStateR20C.Promoted,(await h.Finish()).State);
        Assert.Equal(new[]{0,0,1},h.Pages.Skips);
    }
    [Fact] public async Task APP_CLOSE_DURING_BP_GET_AWAITS_CLEANUP()
    {
        var h=new Harness(pageQuota:2);h.Pages.Rows=[Row("0001")];var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);bool cleanup=false;
        h.Bps.Hook=async ct=>{entered.SetResult();try{await Task.Delay(Timeout.Infinite,ct);}finally{cleanup=true;}};
        var work=h.Coordinator.ExecuteNextCycleAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await h.Lifecycle.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));await work;
        Assert.True(cleanup);Assert.True(work.IsCompleted);Assert.Null(h.Coordinator.ConsolidatedSnapshot);Assert.Single(h.Bps.Calls);
    }
    [Fact] public async Task READ_TIMEOUT_EXCEPTION_RELEASES_LOCK_AND_RESTARTS()
    {
        var h=new Harness();h.Pages.Hook=_=>throw new TimeoutException();
        Assert.Equal(MasterdataReconciliationCycleStateR20C.TimedOut,(await h.Coordinator.ExecuteNextCycleAsync()).State);
        Assert.Equal("OFFLINE_READ_ONLY",(await h.Manual()).Motivo);h.Pages.Hook=null;
        Assert.Equal(MasterdataReconciliationCycleStateR20C.Promoted,(await h.Finish()).State);Assert.Equal(new[]{0,0},h.Pages.Skips);
    }
    [Fact] public async Task COMPLETE_MISSING_ROLE_NEVER_PRODUCES_GUARDIAN_DELETE()
    {
        var h=new Harness();h.Pages.Rows=[Row("0001")];await h.Finish();h.Pages.Rows=[];h.Clock.NowUtc=Now.AddMinutes(1);
        var removed=Assert.Single((await h.Finish()).Candidates);Assert.Contains(SapBpChangeKindR20B.REMOVED_ROLE_CANDIDATE,removed.Changes);Assert.False(removed.GuardianDeleteOrDeactivate);
    }

}
