using FugaTES.WinForms.Transportadoras;
using FugaTES.WinForms.Transportadoras.MasterdataR3;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Aplicacao.Transportadoras.SnapshotsR24;
using System.Text.Json;
namespace FugaTES.Masterdata.R28.Tests;
public class MasterdataWiringR28Tests
{
    [Fact] public async Task AUTO_RUNTIME_DEFAULT_OFF()
    {
        var lifecycle=CarrierDesktopComposition.Create();lifecycle.Start();await lifecycle.Completion;
        Assert.False(lifecycle.Snapshot.Active);Assert.Equal("MASTERDATA_POSTGRESQL_ADAPTER_NOT_APPROVED",lifecycle.Snapshot.Code);
        await lifecycle.StopAsync();
    }

    static readonly DateTimeOffset Now=new(2026,10,12,15,0,0,TimeSpan.Zero);
    sealed class Clock:TimeProvider
    {
        public DateTimeOffset Value=Now;public int Timers;public TimerCallback? Tick;public object? State;
        public override DateTimeOffset GetUtcNow()=>Value;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan due,TimeSpan period)
        {Timers++;Assert.Equal(TimeSpan.FromSeconds(30),period);Tick=callback;State=state;return new Timer();}
        public void Advance(){Value+=TimeSpan.FromSeconds(30);Tick!(State);}
        sealed class Timer:ITimer{public bool Change(TimeSpan due,TimeSpan period)=>true;public void Dispose(){}public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
    }
    sealed class Pages:ISapRolePageReaderR20A
    {
        public int Calls;public bool Wait,Incomplete,Missing,Boundary,Expired;public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SapRolePageR20A> ReadAsync(SapRolePageRequestR20A r,CancellationToken ct)
        {
            Calls++;Entered.TrySetResult();if(Wait)await Task.Delay(Timeout.Infinite,ct);
            if(Incomplete)throw new InvalidOperationException("OFFLINE_INTERRUPTION");
            static string Wire(DateTimeOffset x)=>"/Date("+x.ToUnixTimeMilliseconds()+")/";
            return new(r.Skip==0?[new SapRoleRowR20A("0000000042","CRM010",Missing?null:Wire(Now.AddDays(Boundary?0:-2)),Wire(Now.AddDays(Expired?-1:2)))]:[]);
        }
    }
    sealed class Bps:ISapBpMasterReaderR20B
    {
        public Task<SapBpReadResultR20B> ReadAsync(string bp,CancellationToken ct)=>Task.FromResult(new SapBpReadResultR20B(bp,JsonSerializer.SerializeToElement(new{BusinessPartner=bp})));
    }
    sealed class Harness
    {
        public Clock Clock=new();public Pages Pages=new();public CarrierDesktopLifecycle Life;public int LegacyFactory,ManualCalls;public bool Approval=true,SapApproved=true;
        public TaskCompletionSource ManualEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);public bool ManualWait;
        public Harness(int pageQuota=5)
        {
            Life=new(_=>{LegacyFactory++;throw new Exception("LEGACY_MUST_NOT_START");},Clock,
                async(bp,ct)=>{ManualCalls++;ManualEntered.TrySetResult();if(ManualWait)await Task.Delay(Timeout.Infinite,ct);return new ResultadoCarrierManual(bp,DecisaoCarrierManual.INDETERMINATE,"OFFLINE_ONLY");},
                shared=>new(shared,Pages,new Bps(),new([SapBpFieldR20B.BusinessPartner],true,true,"OFFLINE_SCOPE",temporalContract:SapRoleTemporalContractR20B.R25TBusinessDate),
                    new(pageQuota,5,new(1,10,100)),()=>new(Approval,true,SapApproved,"HML","110","OFFLINE_READ_APPROVAL",Now.AddDays(1)),()=>Approval,Clock));
        }
    }
    static async Task Until(Func<bool> condition)
    {for(int n=0;n<200&&!condition();n++)await Task.Delay(10);Assert.True(condition());}
    [Fact] public async Task MANUAL_PRESERVED()
    {var h=new Harness();var r=await h.Life.ConsultarManualReadOnlyAsync("0000000042",default);Assert.Equal("0000000042",r.BusinessPartner);Assert.Equal(1,h.ManualCalls);Assert.Equal(0,h.LegacyFactory);await h.Life.StopAsync();}
    [Fact] public async Task ONE_SHARED_LOCK()
    {
        var h=new Harness();h.Pages.Wait=true;var cycle=h.Life.ConsultarCadastrosAsync();await h.Pages.Entered.Task;
        var manual=await h.Life.ConsultarManualReadOnlyAsync("0000000042",default);Assert.Equal(0,h.ManualCalls);
        Assert.Equal("SHARED_LOCK_BUSY",manual.Motivo);Assert.Same(h.Life.Masterdata,h.Life.Reconciliation!.Coordinator.SharedOrchestrator);
        await h.Life.StopAsync();Assert.True(cycle.IsCompleted);
    }
    [Fact] public async Task MANUAL_LOCK_BLOCKS_AUTOMATIC()
    {
        var h=new Harness();h.ManualWait=true;var manual=h.Life.ConsultarManualReadOnlyAsync("0000000042",default);await h.ManualEntered.Task;
        Assert.Equal(MasterdataReconciliationCycleStateR20C.Busy,(await h.Life.ConsultarCadastrosAsync()).State);Assert.Equal(0,h.Pages.Calls);
        await h.Life.StopAsync();Assert.True(manual.IsCompleted);
    }
    [Fact] public async Task CYCLE_30S_CONTRACT_AND_ONE_TIMER()
    {
        var h=new Harness();h.Life.Start();await Until(()=>h.Life.LastReconciliation is not null&&h.Clock.Timers==1);
        Assert.Equal(MasterdataReconciliationCycleStateR20C.Promoted,h.Life.LastReconciliation!.State);Assert.Equal(1,h.Clock.Timers);Assert.Equal(0,h.LegacyFactory);
        h.Clock.Advance();await Until(()=>h.Pages.Calls>=4);Assert.Equal(1,h.Clock.Timers);await h.Life.StopAsync();Assert.False(h.Life.Snapshot.Active);
    }
    [Fact] public async Task APP_CLOSE_CANCEL()
    {var h=new Harness();h.Pages.Wait=true;h.Life.Start();await h.Pages.Entered.Task;await h.Life.StopAsync();Assert.False(h.Life.Snapshot.Active);Assert.False(h.Life.Snapshot.Cycling);Assert.Null(h.Life.Reconciliation!.Coordinator.ConsolidatedSnapshot);Assert.Equal(0,h.LegacyFactory);}
    [Fact] public async Task INCOMPLETE_SNAPSHOT_BLOCKED()
    {var h=new Harness(1);var r=await h.Life.ConsultarCadastrosAsync();Assert.Equal(MasterdataReconciliationCycleStateR20C.InProgress,r.State);Assert.Null(h.Life.Reconciliation!.Coordinator.ConsolidatedSnapshot);Assert.Empty(h.Life.Reconciliation.Observation);await h.Life.StopAsync();}
    [Fact] public async Task INTERRUPTED_SNAPSHOT_BLOCKED()
    {var h=new Harness();h.Pages.Incomplete=true;Assert.Equal(MasterdataReconciliationCycleStateR20C.Failed,(await h.Life.ConsultarCadastrosAsync()).State);Assert.Null(h.Life.Reconciliation!.Coordinator.ConsolidatedSnapshot);await h.Life.StopAsync();}
    [Fact] public async Task TEMPORAL_POLICY_R25T_AND_NO_WRITES()
    {
        var h=new Harness();var r=await h.Life.ConsultarCadastrosAsync();Assert.Equal(MasterdataReconciliationCycleStateR20C.Promoted,r.State);
        var s=h.Life.Reconciliation!.Coordinator.ConsolidatedSnapshot!;Assert.Equal(Now,s.ReferenceAt);Assert.Equal(SapRoleTemporalContractR20B.R25TBusinessDate,s.Policy.TemporalContract);
        var row=Assert.Single(h.Life.Reconciliation.Observation);Assert.Null(row.ApprovedFullNameNormalized);Assert.Equal(SnapshotFingerprintR24.Row(VersoesSnapshotR24.AprovadaR25T,row),row.RowFingerprint);
        Assert.False(r.GuardianWritesAuthorized);Assert.False(r.DurableCheckpoint);Assert.Equal(0,h.LegacyFactory);await h.Life.StopAsync();
    }
    [Fact] public async Task INDETERMINATE_DATE_NOT_PROMOTED()
    {var h=new Harness();h.Pages.Missing=true;Assert.Equal(MasterdataReconciliationCycleStateR20C.Failed,(await h.Life.ConsultarCadastrosAsync()).State);Assert.Null(h.Life.Reconciliation!.Coordinator.ConsolidatedSnapshot);await h.Life.StopAsync();}
    [Fact] public async Task APPROVAL_MISSING_NO_READERS()
    {var h=new Harness();h.Approval=false;h.Life.Start();await h.Life.Completion;Assert.Equal(0,h.Pages.Calls);Assert.Equal(0,h.Clock.Timers);Assert.Equal(0,h.LegacyFactory);await h.Life.StopAsync();}
    [Fact] public async Task MANUAL_TRIGGER_SAME_PIPELINE()
    {var h=new Harness();var r=await h.Life.ConsultarCadastrosAsync();Assert.Equal(MasterdataReconciliationCycleStateR20C.Promoted,r.State);Assert.Same(r,h.Life.LastReconciliation);Assert.Equal(0,h.Clock.Timers);await h.Life.StopAsync();}
    [Fact] public async Task R25T_INCLUSIVE_BOUNDARY_IN_ENRICHMENT()
    {var h=new Harness();h.Pages.Boundary=true;Assert.Equal(MasterdataReconciliationCycleStateR20C.Promoted,(await h.Life.ConsultarCadastrosAsync()).State);await h.Life.StopAsync();}
    [Fact] public async Task REFERENCE_FIXED_ACROSS_CYCLES()
    {
        var h=new Harness(1);await h.Life.ConsultarCadastrosAsync();h.Clock.Value+=TimeSpan.FromMinutes(5);
        Assert.Equal(MasterdataReconciliationCycleStateR20C.Promoted,(await h.Life.ConsultarCadastrosAsync()).State);
        Assert.Equal(Now,h.Life.Reconciliation!.Coordinator.ConsolidatedSnapshot!.ReferenceAt);await h.Life.StopAsync();
    }
    [Fact] public async Task REVOKED_PARTIAL_SCAN_RESTARTS()
    {
        var h=new Harness(1);await h.Life.ConsultarCadastrosAsync();Assert.Equal(1,h.Pages.Calls);
        h.Approval=false;Assert.Equal(MasterdataReconciliationCycleStateR20C.Blocked,(await h.Life.ConsultarCadastrosAsync()).State);
        h.Approval=true;Assert.Equal(MasterdataReconciliationCycleStateR20C.InProgress,(await h.Life.ConsultarCadastrosAsync()).State);
        Assert.Equal(2,h.Pages.Calls);Assert.Null(h.Life.Reconciliation!.Coordinator.ConsolidatedSnapshot);await h.Life.StopAsync();
    }
    [Fact] public async Task ALL_ACTIVE_CRM010_WITHOUT_CENTER_FILTER()
    {
        var h=new Harness();h.Pages.Expired=true;await h.Life.ConsultarCadastrosAsync();
        Assert.Empty(h.Life.Reconciliation!.EligibleBusinessPartners);
        Assert.Equal(AvaliacaoCrm010R24.EVALUATED_INVALID,Assert.Single(h.Life.Reconciliation.Observation).Crm010TemporalEvaluation);
        await h.Life.StopAsync();
    }
    [Fact] public async Task SAP_APPROVAL_REQUIRED_BEFORE_TIMER()
    {var h=new Harness();h.SapApproved=false;h.Life.Start();await h.Life.Completion;Assert.Equal(0,h.Clock.Timers);Assert.Equal(0,h.Pages.Calls);await h.Life.StopAsync();}
}
