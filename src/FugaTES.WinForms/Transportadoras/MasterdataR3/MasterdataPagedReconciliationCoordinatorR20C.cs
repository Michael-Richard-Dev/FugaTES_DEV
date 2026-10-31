using FugaTES.Integracao.SAP.BusinessPartner;
namespace FugaTES.WinForms.Transportadoras.MasterdataR3;

public sealed class MasterdataPagedReconciliationCoordinatorR20C
{
    readonly OrquestradorCompartilhadoMasterdataR3 shared;
    readonly ISapRolePageReaderR20A pages;
    readonly ISapBpMasterReaderR20B bps;
    readonly SapBpComparisonPolicyR20B policy;
    readonly MasterdataReconciliationCycleLimitsR20C limits;
    readonly Func<MasterdataSapReadAuthorizationR20C?>? authorization;
    readonly TimeProvider clock;
    ScanState? scan;
    int restartRequested;
    volatile SapBpEnrichedSnapshotR20B? consolidated;
    volatile MasterdataReconciliationCycleResultR20C last = new(MasterdataReconciliationCycleStateR20C.Inactive,
        "RUNTIME_NOT_ACTIVATED",null,0,0,0,0,Array.Empty<SapBpChangeR20B>());
    public MasterdataPagedReconciliationCoordinatorR20C(OrquestradorCompartilhadoMasterdataR3 shared,
        ISapRolePageReaderR20A pages, ISapBpMasterReaderR20B bps, SapBpComparisonPolicyR20B policy,
        MasterdataReconciliationCycleLimitsR20C limits,
        Func<MasterdataSapReadAuthorizationR20C?>? authorization = null, TimeProvider? clock = null)
    {
        this.shared=shared??throw new ArgumentNullException(nameof(shared));
        this.pages=pages??throw new ArgumentNullException(nameof(pages));
        this.bps=bps??throw new ArgumentNullException(nameof(bps));
        this.policy=policy??throw new ArgumentNullException(nameof(policy));
        this.limits=limits??throw new ArgumentNullException(nameof(limits));
        this.authorization=authorization;this.clock=clock??TimeProvider.System;
    }
    public static TimeSpan Interval => OrquestradorCompartilhadoMasterdataR3.IntervaloAutomatico;
    public bool RuntimeActivation => false; // No lifecycle/timer registration occurs in this phase.
    public SapBpEnrichedSnapshotR20B? ConsolidatedSnapshot => consolidated;
    public OrquestradorCompartilhadoMasterdataR3 SharedOrchestrator => shared;

    public async Task<MasterdataReconciliationCycleResultR20C> ExecuteNextCycleAsync(
        CancellationToken cancellationToken = default, CancellationToken deadlineToken = default)
    {
        // Deadline belongs to the caller. This coordinator creates no timer or detached task.
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,deadlineToken);
        var protectedResult=await shared.ExecutarConsultaProtegidaAsync(
            ct=>ProcessProtectedCycleAsync(ct,deadlineToken),linked.Token).ConfigureAwait(false);
        if (protectedResult.Situacao is SituacaoConsultaProtegidaMasterdataR3.Cancelada or SituacaoConsultaProtegidaMasterdataR3.Encerrada)
            Interlocked.Exchange(ref restartRequested,1);
        return protectedResult.Situacao switch
        {
            SituacaoConsultaProtegidaMasterdataR3.Executada => protectedResult.Resultado!,
            SituacaoConsultaProtegidaMasterdataR3.Ocupada => StatusOnly(MasterdataReconciliationCycleStateR20C.Busy,"SHARED_LOCK_BUSY"),
            SituacaoConsultaProtegidaMasterdataR3.Encerrada => StatusOnly(MasterdataReconciliationCycleStateR20C.AppClosed,"APP_CLOSED"),
            _ => StatusOnly(deadlineToken.IsCancellationRequested?MasterdataReconciliationCycleStateR20C.TimedOut:
                MasterdataReconciliationCycleStateR20C.Canceled,deadlineToken.IsCancellationRequested?"CYCLE_DEADLINE_CANCELED":"CYCLE_CANCELED")
        };
    }
    MasterdataReconciliationCycleResultR20C StatusOnly(MasterdataReconciliationCycleStateR20C state,string code)
        => last with {State=state,Code=code,NewPages=0,NewBps=0,Candidates=Array.Empty<SapBpChangeR20B>()};
    public bool ReadAuthorizationEffective=>Authorized();
    bool Authorized()
    {
        try
        {
            var a=authorization?.Invoke();
            return a is {Approved:true,AppOpen:true,SapProfileVerified:true,Environment:"HML",SapClient:"110"} &&
                !string.IsNullOrWhiteSpace(a.ApprovalReference) && a.UntilUtc>clock.GetUtcNow();
        }
        catch { return false; }
    }
    async Task<MasterdataReconciliationCycleResultR20C> ProcessProtectedCycleAsync(CancellationToken ct,CancellationToken deadline)
    {
        CycleReader? reader=null;
        MasterdataReconciliationCycleResultR20C Finish(MasterdataReconciliationCycleStateR20C state,string code,
            bool discard=false,IReadOnlyList<SapBpChangeR20B>? candidates=null)
        {
            var s=scan;
            var result=new MasterdataReconciliationCycleResultR20C(state,code,s?.Id,reader?.NewPages??0,reader?.NewBps??0,
                s?.Pages.Count??0,s?.Bps.Count??0,candidates??Array.Empty<SapBpChangeR20B>());
            if(discard)scan=null;
            last=result;return result;
        }
        MasterdataReconciliationCycleResultR20C Cancel() => Finish(deadline.IsCancellationRequested?
            MasterdataReconciliationCycleStateR20C.TimedOut:MasterdataReconciliationCycleStateR20C.Canceled,
            deadline.IsCancellationRequested?"CYCLE_DEADLINE_CANCELED":"CYCLE_CANCELED",true);
        try
        {
            // A cancellation before lock acquisition also invalidates the old partial scan.
            if(Interlocked.Exchange(ref restartRequested,0)!=0)scan=null;
            if(ct.IsCancellationRequested)return Cancel();
            if(!Authorized())return Finish(authorization is null?MasterdataReconciliationCycleStateR20C.Inactive:
                MasterdataReconciliationCycleStateR20C.Blocked,"SAP_READ_AUTHORIZATION_NOT_EFFECTIVE",true);
            if(limits.NewPagesPerCycle<1||limits.NewBpsPerCycle<1||limits.ScanLimits is null)
                return Finish(MasterdataReconciliationCycleStateR20C.Failed,"CYCLE_LIMITS_INVALID",true);
            scan??=new ScanState(clock.GetUtcNow());
            reader=new CycleReader(scan,pages,bps,limits,Authorized);
            if(scan.Roles is null)
            {
                var roles=await new SapRolePagedEnumeratorR20A(reader,scan.Time)
                    .EnumerateAsync(limits.ScanLimits,cancellationToken:ct).ConfigureAwait(false);
                if(ct.IsCancellationRequested)return Cancel();
                if(reader.AuthorizationLost||!Authorized())return Finish(MasterdataReconciliationCycleStateR20C.Blocked,"SAP_READ_AUTHORIZATION_REVOKED",true);
                if(reader.TimedOut)return Finish(MasterdataReconciliationCycleStateR20C.TimedOut,"SAP_READ_TIMEOUT",true);
                if(!roles.Complete)
                    return reader.PageBudgetExhausted?Finish(MasterdataReconciliationCycleStateR20C.InProgress,"PAGE_BUDGET_PENDING"):
                        Finish(MasterdataReconciliationCycleStateR20C.Failed,"ROLE_SCAN_FAILED",true);
                scan.Roles=roles;
            }
            var enriched=await new SapBpSnapshotEnricherR20B(reader,scan.Time)
                .EnrichAsync(scan.Roles,policy,ct).ConfigureAwait(false);
            if(ct.IsCancellationRequested)return Cancel();
            if(reader.AuthorizationLost||!Authorized())return Finish(MasterdataReconciliationCycleStateR20C.Blocked,"SAP_READ_AUTHORIZATION_REVOKED",true);
            if(reader.TimedOut)return Finish(MasterdataReconciliationCycleStateR20C.TimedOut,"SAP_READ_TIMEOUT",true);
            if(!enriched.Complete)
                return reader.BpBudgetExhausted?Finish(MasterdataReconciliationCycleStateR20C.InProgress,"BP_BUDGET_PENDING"):
                    Finish(MasterdataReconciliationCycleStateR20C.Failed,"BP_ENRICHMENT_FAILED",true);
            if(!enriched.Valid)return Finish(MasterdataReconciliationCycleStateR20C.Failed,"ENRICHED_SNAPSHOT_INVALID",true);
            IReadOnlyList<SapBpChangeR20B> candidates=Array.Empty<SapBpChangeR20B>();
            if(consolidated is not null)
            {
                var comparison=new SapBpEnrichedSnapshotComparerR20B().Compare(consolidated,enriched);
                if(!comparison.CompleteAndValidPair)return Finish(MasterdataReconciliationCycleStateR20C.Failed,"SNAPSHOT_COMPARISON_BLOCKED",true);
                candidates=comparison.Results;
            }
            ct.ThrowIfCancellationRequested();
            if(!Authorized())return Finish(MasterdataReconciliationCycleStateR20C.Blocked,"SAP_READ_AUTHORIZATION_REVOKED",true);
            bool first=consolidated is null;
            consolidated=enriched; // Single reference swap, only inside the actual shared lock.
            return Finish(MasterdataReconciliationCycleStateR20C.Promoted,first?"INITIAL_SAP_BASELINE_CAPTURED":"SAP_SNAPSHOT_PROMOTED",true,candidates);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){return Cancel();}
        catch{return Finish(MasterdataReconciliationCycleStateR20C.Failed,"READONLY_RECONCILIATION_FAILED",true);}
    }
    sealed class FrozenTime(DateTimeOffset value):TimeProvider {public override DateTimeOffset GetUtcNow()=>value;}
    sealed class ScanState(DateTimeOffset reference)
    {
        public readonly Guid Id=Guid.NewGuid();
        public readonly TimeProvider Time=new FrozenTime(reference);
        public readonly Dictionary<int,SapRolePageR20A> Pages=[];
        public readonly Dictionary<string,SapBpReadResultR20B> Bps=new(StringComparer.Ordinal);
        public SapRoleSnapshotR20A? Roles;
    }
    sealed class BudgetExhaustedException:Exception;
    sealed class AuthorizationLostException:Exception;
    // Revalidate cached prefixes through the approved cores. Only uncached GETs consume the cycle quota.
    sealed class CycleReader(ScanState scan,ISapRolePageReaderR20A pages,ISapBpMasterReaderR20B bps,
        MasterdataReconciliationCycleLimitsR20C limits,Func<bool> authorized):ISapRolePageReaderR20A,ISapBpMasterReaderR20B
    {
        public int NewPages{get;private set;}public int NewBps{get;private set;}
        public bool PageBudgetExhausted{get;private set;}public bool BpBudgetExhausted{get;private set;}
        public bool AuthorizationLost{get;private set;}public bool TimedOut{get;private set;}
        void Check(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(!authorized()){AuthorizationLost=true;throw new AuthorizationLostException();}
        }
        public async Task<SapRolePageR20A> ReadAsync(SapRolePageRequestR20A request,CancellationToken ct)
        {
            Check(ct);
            if(scan.Pages.TryGetValue(request.Skip,out var cached))return cached;
            if(NewPages>=limits.NewPagesPerCycle){PageBudgetExhausted=true;throw new BudgetExhaustedException();}
            NewPages++;
            SapRolePageR20A page;
            try{page=await pages.ReadAsync(request,ct).ConfigureAwait(false);}
            catch(TimeoutException){TimedOut=true;throw;}
            Check(ct);
            if(page?.Rows is null)throw new InvalidOperationException("PAGE_INVALID");
            var copy=new SapRolePageR20A(Array.AsReadOnly(page.Rows.ToArray()));scan.Pages.Add(request.Skip,copy);return copy;
        }
        public async Task<SapBpReadResultR20B> ReadAsync(string bp,CancellationToken ct)
        {
            Check(ct);
            if(scan.Bps.TryGetValue(bp,out var cached))return cached;
            if(NewBps>=limits.NewBpsPerCycle){BpBudgetExhausted=true;throw new BudgetExhaustedException();}
            NewBps++;
            SapBpReadResultR20B data;
            try{data=await bps.ReadAsync(bp,ct).ConfigureAwait(false);}
            catch(TimeoutException){TimedOut=true;throw;}
            Check(ct);
            if(data is null)throw new InvalidOperationException("BP_READ_INVALID");
            if(!data.Complete)return data;
            var copy=data with{RawSapData=data.RawSapData.Clone()};scan.Bps.Add(bp,copy);return copy;
        }
    }
}
