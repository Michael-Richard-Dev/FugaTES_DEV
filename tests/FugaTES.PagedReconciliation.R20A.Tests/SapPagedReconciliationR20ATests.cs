using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Aplicacao.Transportadoras;
namespace FugaTES.PagedReconciliation.R20A.Tests;
public class SapPagedReconciliationR20ATests
{
    static readonly DateTimeOffset Now = new(2026,10,11,12,0,0,TimeSpan.Zero);
    sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    static string Wire(DateTimeOffset d) => "/Date(" + d.ToUnixTimeMilliseconds() + ")/";
    static SapRoleRowR20A Row(string bp, int start = -30, int end = 30) => new(bp,"CRM010",Wire(Now.AddDays(start)),Wire(Now.AddDays(end)));
    sealed class Reader(params SapRoleRowR20A[][] pages) : ISapRolePageReaderR20A
    {
        public readonly List<SapRolePageRequestR20A> Requests = [];
        public int FailAt = -1; public Action? OnRead;
        public Task<SapRolePageR20A> ReadAsync(SapRolePageRequestR20A r, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Requests.Add(r); OnRead?.Invoke();
            if(r.PageNumber == FailAt) throw new InvalidOperationException("DO_NOT_EXPOSE_PRIVATE_ERROR");
            return Task.FromResult(new SapRolePageR20A(r.PageNumber < pages.Length ? pages[r.PageNumber] : []));
        }
    }
    sealed class Progress : IProgress<SapRoleScanProgressR20A> { public List<SapRoleScanProgressR20A> Values=[]; public void Report(SapRoleScanProgressR20A v)=>Values.Add(v); }
    static Task<SapRoleSnapshotR20A> Scan(params SapRoleRowR20A[] rows) => new SapRolePagedEnumeratorR20A(new Reader(rows),new Clock(Now)).EnumerateAsync(new(10,10,100));
    static async Task<SapRoleCandidateR20A> Compare(SapRoleRowR20A[] a, SapRoleRowR20A[] b, DateTimeOffset? at=null) => Assert.Single(new SapRoleSnapshotComparerR20A().Compare(await Scan(a),await Scan(b),at??Now).Candidates);

    [Fact] public async Task PAGINATION_MULTIPAGE()
    {
        var r=new Reader([Row("0001"),Row("0002")],[Row("0003")]); var p=new Progress();
        var s=await new SapRolePagedEnumeratorR20A(r,new Clock(Now)).EnumerateAsync(new(2,10,100),p);
        Assert.True(s.Complete);Assert.True(s.ConsolidationCandidate);Assert.Equal(new[]{"0001","0002","0003"},s.Rows.Select(x=>x.BusinessPartner));
        Assert.Equal(new[]{0,2,4},r.Requests.Select(x=>x.Skip));Assert.Equal(3,s.PagesRead);Assert.Equal(3,p.Values.Last().RowsRead);
        Assert.All(r.Requests,x=>{Assert.Contains("A_BusinessPartnerRole",x.RelativeUri);Assert.Contains("CRM010",x.RelativeUri);Assert.Contains("$orderby=BusinessPartner%20asc",x.RelativeUri);Assert.DoesNotContain("delta",x.RelativeUri);Assert.DoesNotContain("Center",x.RelativeUri);});
        Assert.False(s.TransactionalConsistencyProven);Assert.False(s.CorporateCompletenessProven);
    }
    [Fact] public async Task DUPLICATE_KEY_DETECTED()
    {
        var s=await new SapRolePagedEnumeratorR20A(new Reader([Row("0001")],[Row("0001",-20,20)])).EnumerateAsync(new(1,10,100));
        Assert.False(s.Complete);Assert.Equal("DUPLICATE_KEY",s.Reason);Assert.Single(s.Rows);
    }
    [Fact] public async Task REPEATED_PAGE_DETECTED()
    {
        var s=await new SapRolePagedEnumeratorR20A(new Reader([Row("0001")],[Row("0001")])).EnumerateAsync(new(1,10,100));
        Assert.Equal("REPEATED_PAGE",s.Reason);Assert.False(s.ConsolidationCandidate);
    }
    [Theory][InlineData(true)][InlineData(false)] public async Task ORDERING_REGRESSION_DETECTED(bool within)
    {
        var r=within?new Reader([Row("0002"),Row("0001")]):new Reader([Row("0002")],[Row("0001")]);
        var s=await new SapRolePagedEnumeratorR20A(r).EnumerateAsync(new(within?2:1,10,100));Assert.Equal("ORDERING_REGRESSION",s.Reason);Assert.False(s.Complete);
    }
    [Fact] public async Task INCOMPLETE_SCAN_NOT_COMMITTED()
    {
        var r=new Reader([Row("0001")]){FailAt=1}; var partial=await new SapRolePagedEnumeratorR20A(r).EnumerateAsync(new(1,10,100));
        Assert.Equal("PAGE_READ_FAILED",partial.Reason);Assert.False(partial.ConsolidationCandidate);
        var comparison=new SapRoleSnapshotComparerR20A().Compare(await Scan(Row("0002")),partial,Now);
        Assert.False(comparison.CompleteEnumerationPair);Assert.Empty(comparison.Candidates);
    }
    [Fact] public async Task INTERRUPTED_SCAN_RESTART()
    {
        bool stop=false;var r=new Reader([Row("0001")]){OnRead=()=>stop=true};var e=new SapRolePagedEnumeratorR20A(r);
        var partial=await e.EnumerateAsync(new(1,10,100),interrupted:()=>stop);Assert.False(partial.Complete);
        r.OnRead=null;stop=false;var full=await e.EnumerateAsync(new(1,10,100),interrupted:()=>stop);
        Assert.True(full.Complete);Assert.Equal(new[]{0,0,1},r.Requests.Select(x=>x.Skip));
    }
    [Fact] public async Task CANCELLATION_RETURNS_PARTIAL()
    {
        using var cts=new CancellationTokenSource();var r=new Reader([Row("0001")]){OnRead=()=>cts.Cancel()};
        var s=await new SapRolePagedEnumeratorR20A(r).EnumerateAsync(new(1,10,100),cancellationToken:cts.Token);
        Assert.False(s.Complete);Assert.Equal("CANCELED",s.Reason);Assert.Single(r.Requests);
    }
    [Theory][InlineData(1,100,"PAGE_LIMIT")][InlineData(10,1,"ROW_LIMIT")]
    public async Task LIMITS_RETURN_PARTIAL(int pages,int rows,string reason)
    {
        var s=await new SapRolePagedEnumeratorR20A(new Reader([Row("0001")],[Row("0002")])).EnumerateAsync(new(1,pages,rows));
        Assert.False(s.Complete);Assert.Equal(reason,s.Reason);
    }
    [Theory][InlineData(0,1,1)][InlineData(1,0,1)][InlineData(1,1,0)][InlineData(int.MaxValue,2,1)]
    public async Task INVALID_LIMITS_FAIL_BEFORE_READ(int top,int pages,int rows)
    {
        var r=new Reader();await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>new SapRolePagedEnumeratorR20A(r).EnumerateAsync(new(top,pages,rows)));Assert.Empty(r.Requests);
    }
    [Theory][InlineData("","CRM010")][InlineData("0001","OTHER")]
    public async Task INVALID_ROWS_NOT_COMPLETE(string bp,string role)
    {
        var s=await Scan(Row(bp) with{BusinessPartnerRole=role});Assert.False(s.Complete);Assert.Equal("INVALID_ROLE_ROW",s.Reason);
    }
    [Fact] public async Task OVERSIZED_PAGE_FAIL_CLOSED()
    {
        var s=await new SapRolePagedEnumeratorR20A(new Reader([Row("1"),Row("2")])).EnumerateAsync(new(1,10,100));Assert.Equal("PAGE_SIZE_EXCEEDED",s.Reason);
    }
    [Fact] public async Task NEW_CRM010_DETECTED()
    {
        var c=await Compare([], [Row("0001")]);Assert.Equal(SapRoleCandidateKindR20A.NEW_ELIGIBLE_BP,c.Kind);Assert.True(c.RequiresBpMasterRead);Assert.False(c.GuardianWriteAuthorized);
    }
    [Fact] public async Task ROLE_VALIDITY_CHANGED()
    {
        var c=await Compare([Row("0001")],[Row("0001",-20,40)]);Assert.Equal(SapRoleCandidateKindR20A.ROLE_VALIDITY_CHANGED,c.Kind);Assert.True(c.RequiresBpMasterRead);
    }
    [Fact] public async Task ROLE_EXPIRATION_REEVALUATED()
    {
        var c=await Compare([Row("0001",-30,3)],[Row("0001",-30,3)],Now.AddDays(5));Assert.Equal(SapRoleCandidateKindR20A.ELIGIBILITY_EXPIRED,c.Kind);Assert.False(c.GuardianDeleteOrDeactivate);
    }
    [Fact] public async Task ROLE_BECOMES_ELIGIBLE_WITHOUT_WIRE_CHANGE()
    {
        var c=await Compare([Row("0001",3,30)],[Row("0001",3,30)],Now.AddDays(5));Assert.Equal(SapRoleCandidateKindR20A.CHANGED_BP_ROLE,c.Kind);
    }
    [Fact] public async Task MISSING_ROLE_NO_DELETE()
    {
        var c=await Compare([Row("0001")],[]);Assert.Equal(SapRoleCandidateKindR20A.REMOVED_ROLE_CANDIDATE,c.Kind);Assert.False(c.GuardianDeleteOrDeactivate);Assert.False(c.GuardianWriteAuthorized);
    }
    [Theory][InlineData("bad")][InlineData("/Date(0+0199)/")][InlineData(null)]
    public async Task DATE_AMBIGUITY_FAIL_CLOSED(string? date)
    {
        var c=await Compare([], [Row("0001") with{ValidFrom=date}]);Assert.Equal(SapRoleCandidateKindR20A.INDETERMINATE,c.Kind);
    }
    [Fact] public async Task TEMPORAL_BOUNDARY_FAIL_CLOSED()
    {
        var c=await Compare([], [Row("0001",-30,0)]);Assert.Equal(SapRoleCandidateKindR20A.INDETERMINATE,c.Kind);
    }
    [Fact] public async Task NO_CHANGE_IS_ROLE_ONLY_AND_STILL_REQUIRES_MASTER_REQUERY()
    {
        var c=await Compare([Row("0001")],[Row("0001")]);Assert.Equal(SapRoleCandidateKindR20A.NO_CHANGE,c.Kind);Assert.True(c.RequiresBpMasterRead);
    }
    [Fact] public async Task NO_REAL_HTTP_OR_DB()
    {
        var r=new Reader([Row("0001")]);var s=await new SapRolePagedEnumeratorR20A(r,new Clock(Now)).EnumerateAsync(new(10,10,100));
        var result=new SapRoleSnapshotComparerR20A().Compare(await Scan(),s,Now);Assert.True(result.CompleteEnumerationPair);Assert.False(result.BpMasterEnrichmentImplemented);Assert.Equal(2,r.Requests.Count);
        Assert.All(result.Candidates,c=>Assert.False(c.GuardianWriteAuthorized));
    }
    sealed class ManualSap:ISapCarrierManualReadOnly { public Task<CarrierManualSap> ConsultarBpAsync(string bp,CancellationToken ct)=>Task.FromResult(new CarrierManualSap(bp,new Dictionary<string,string?>(),[new("CRM010",Now.AddDays(-30),Now.AddDays(30))])); }
    sealed class ManualGuardian:IGuardianCarrierManualReadOnly { public Task<CarrierManualGuardian> ConsultarCodigoAsync(CarrierManualIdentity id,CancellationToken ct)=>Task.FromResult(new CarrierManualGuardian(CarrierLookupState.Absent,null,new Dictionary<string,string?>(),true)); }
    [Fact] public async Task MANUAL_R15_PRESERVED()
    {
        var manual=new ConsultarTransportadoraManualReadOnlyCasoDeUso(new ManualSap(),new ManualGuardian(),_=>true,_=>new("OFFLINE_CODE","OFFLINE_SIGNATURE",true,"OFFLINE_PROOF"),new([],false,"OFFLINE"),new Clock(Now));
        Assert.Equal(DecisaoCarrierManual.CREATE_CANDIDATE,(await manual.ExecutarAsync("0001",default)).Decisao);
        await Scan(Row("0001"));Assert.Equal(DecisaoCarrierManual.CREATE_CANDIDATE,(await manual.ExecutarAsync("0001",default)).Decisao);
    }
    [Fact] public async Task PRE_CANCELED_SCAN_DOES_NOT_READ()
    {
        using var cts=new CancellationTokenSource();cts.Cancel();var r=new Reader();
        var s=await new SapRolePagedEnumeratorR20A(r).EnumerateAsync(new(),cancellationToken:cts.Token);
        Assert.Equal("CANCELED",s.Reason);Assert.Empty(r.Requests);Assert.Empty(s.Rows);
    }
    [Fact] public async Task EMPTY_TERMINAL_PAGE_WITHIN_EXACT_LIMIT_IS_COMPLETE()
    {
        var s=await new SapRolePagedEnumeratorR20A(new Reader(),new Clock(Now)).EnumerateAsync(new(1,1,1));
        Assert.True(s.Complete);Assert.Empty(s.Rows);Assert.Equal(1,s.PagesRead);
    }
    [Fact] public async Task INCOMPLETE_PREVIOUS_SCAN_ALSO_BLOCKS_COMPARISON()
    {
        var previous=await new SapRolePagedEnumeratorR20A(new Reader([Row("0001")]),new Clock(Now)).EnumerateAsync(new(1,1,10));
        var c=new SapRoleSnapshotComparerR20A().Compare(previous,await Scan(),Now);
        Assert.False(c.CompleteEnumerationPair);Assert.Empty(c.Candidates);
    }
    [Fact] public async Task EVALUATION_BEFORE_SCAN_FAILS_CLOSED()
    {
        var c=new SapRoleSnapshotComparerR20A().Compare(await Scan(),await Scan(Row("0001")),Now.AddSeconds(-1));
        Assert.False(c.CompleteEnumerationPair);Assert.Equal("EVALUATION_PRECEDES_SCAN",c.Reason);
    }
    [Fact] public async Task REVERSED_VALIDITY_IS_INDETERMINATE()
    {
        var c=await Compare([], [Row("0001",30,-30)]);Assert.Equal(SapRoleCandidateKindR20A.INDETERMINATE,c.Kind);
    }
    [Fact] public async Task SNAPSHOT_DOES_NOT_RETAIN_MUTABLE_PAGE_ARRAY()
    {
        var page=new[]{Row("0001")};var s=await new SapRolePagedEnumeratorR20A(new Reader(page),new Clock(Now)).EnumerateAsync(new(10,10,100));
        page[0]=Row("OTHER");Assert.Equal("0001",Assert.Single(s.Rows).BusinessPartner);
    }

}
