using System.Net;
using System.Text.Json;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Integracao.SAP.Http;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;
namespace FugaTES.BpEnrichment.R20B.Tests;
public class SapBpEnrichmentR20BTests
{
    static readonly DateTimeOffset Now = new(2026,10,11,12,0,0,TimeSpan.Zero);
    sealed class Clock(DateTimeOffset now):TimeProvider { public int Calls;public override DateTimeOffset GetUtcNow(){Calls++;return now.AddHours(Calls-1);} }
    static string Date(DateTimeOffset d)=>"/Date("+d.ToUnixTimeMilliseconds()+")/";
    static SapRoleRowR20A Role(string bp="0001",int from=-30,int to=30)=>new(bp,"CRM010",Date(Now.AddDays(from)),Date(Now.AddDays(to)));
    static JsonElement Raw(string bp="0001",string name="Original")=>JsonSerializer.SerializeToElement(new {
        BusinessPartner=bp,BusinessPartnerName=name,BusinessPartnerFullName="Full "+name,
        to_BusinessPartnerTax=new{results=new[]{new{BPTaxType="OFFLINE_TYPE",BPTaxNumber="OFFLINE_VALUE",BPTaxLongNumber=""}}},
        to_BusinessPartnerAddress=new{results=new[]{new{AddressID="OFFLINE_ID",StreetName="Street",CityName="City",Region="XX",PostalCode="OFFLINE",Country="XX",
            to_AddressUsage=new{results=Array.Empty<object>()},to_PhoneNumber=new{results=new[]{new{PhoneNumber="OFFLINE_PHONE"}}},to_EmailAddress=new{results=new[]{new{EmailAddress="offline@example.invalid"}}}}}}
    });
    static SapBpComparisonPolicyR20B Policy(SapBpNormalizationR20B normalization=SapBpNormalizationR20B.ExactOrdinal,bool full=true,SapRoleTemporalContractR20B temporal=SapRoleTemporalContractR20B.EdmDateTimeOffset)=>
        new([SapBpFieldR20B.BusinessPartner,SapBpFieldR20B.BusinessPartnerName,SapBpFieldR20B.BusinessPartnerFullName],true,full,"OFFLINE_FIELDS_SCOPE",normalization,temporal);
    sealed class Pages(params SapRoleRowR20A[] rows):ISapRolePageReaderR20A
    {public Task<SapRolePageR20A> ReadAsync(SapRolePageRequestR20A r,CancellationToken ct)=>Task.FromResult(new SapRolePageR20A(r.Skip==0?rows:[]));}
    static Task<SapRoleSnapshotR20A> Scan(params SapRoleRowR20A[] rows)=>new SapRolePagedEnumeratorR20A(new Pages(rows),new Clock(Now)).EnumerateAsync(new(10,10,100));
    sealed class Reader(Func<string,SapBpReadResultR20B> read):ISapBpMasterReaderR20B
    {public List<string> Calls=[];public Action? OnRead;public Task<SapBpReadResultR20B> ReadAsync(string bp,CancellationToken ct){ct.ThrowIfCancellationRequested();Calls.Add(bp);OnRead?.Invoke();return Task.FromResult(read(bp));}}
    static async Task<SapBpEnrichedSnapshotR20B> Snapshot(JsonElement raw,SapRoleRowR20A? role=null,SapBpComparisonPolicyR20B? policy=null,DateTimeOffset? at=null)=>
        await new SapBpSnapshotEnricherR20B(new Reader(bp=>new(bp,raw)),new Clock(at??Now.AddHours(3))).EnrichAsync(await Scan(role??Role()),policy??Policy());
    static SapBpChangeR20B Single(SapBpSnapshotComparisonR20B c)=>Assert.Single(c.Results);

    [Fact] public async Task BP_ENRICHMENT_SUCCESS()
    {
        var r=new Reader(bp=>new(bp,Raw(bp)));var clock=new Clock(Now.AddHours(3));
        var s=await new SapBpSnapshotEnricherR20B(r,clock).EnrichAsync(await Scan(Role()),Policy());
        Assert.True(s.Complete);Assert.True(s.Valid);Assert.True(s.ConsolidationCandidate);Assert.Equal(1,clock.Calls);
        var item=Assert.Single(s.Entries);Assert.Equal("Original",item.RawSapData.GetProperty("BusinessPartnerName").GetString());
        Assert.Equal("\"Original\"",item.NormalizedSapData["BusinessPartnerName"]);Assert.Equal(SapBpComparisonStatusR20B.Complete,item.ComparisonStatus);
    }
    [Fact] public async Task BP_ENRICHMENT_FAILURE()
    {
        var r=new Reader(_=>throw new Exception("PRIVATE_DATA_NOT_FOR_LOGS"));
        var s=await new SapBpSnapshotEnricherR20B(r,new Clock(Now.AddHours(3))).EnrichAsync(await Scan(Role()),Policy());
        Assert.False(s.Complete);Assert.False(s.ConsolidationCandidate);Assert.Equal("BP_READ_FAILED",s.Reason);Assert.DoesNotContain("PRIVATE",s.Reason);
    }
    [Fact] public async Task BP_CANONICAL_ID_PRESERVED()
    {
        var r=new Reader(bp=>new(bp,Raw(bp)));var s=await new SapBpSnapshotEnricherR20B(r,new Clock(Now.AddHours(3))).EnrichAsync(await Scan(Role("0000000042")),Policy());
        Assert.Equal("0000000042",Assert.Single(r.Calls));Assert.Equal("0000000042",Assert.Single(s.Entries).BusinessPartner);
    }
    [Theory][InlineData(true)][InlineData(false)] public async Task BP_IDENTITY_MISMATCH_BLOCKED(bool rawMismatch)
    {
        var r=new Reader(bp=>new(rawMismatch?bp:"OTHER",Raw(rawMismatch?"OTHER":bp)));
        var s=await new SapBpSnapshotEnricherR20B(r,new Clock(Now.AddHours(3))).EnrichAsync(await Scan(Role()),Policy());
        Assert.False(s.Complete);Assert.Equal("BP_IDENTITY_MISMATCH",s.Reason);
    }
    [Fact] public async Task BP_MASTERDATA_CHANGE_DETECTED()
    {
        var c=new SapBpEnrichedSnapshotComparerR20B().Compare(await Snapshot(Raw()),await Snapshot(Raw(name:"Changed")));
        Assert.True(c.CompleteAndValidPair);Assert.Equal(new[]{SapBpChangeKindR20B.BP_MASTERDATA_CHANGED},Single(c).Changes);
    }
    [Fact] public async Task ROLE_CHANGE_SEPARATE_FROM_BP_CHANGE()
    {
        var c=new SapBpEnrichedSnapshotComparerR20B().Compare(await Snapshot(Raw()),await Snapshot(Raw(),Role(from:-20,to:40)));
        Assert.Equal(new[]{SapBpChangeKindR20B.ROLE_VALIDITY_CHANGED},Single(c).Changes);
        var both=new SapBpEnrichedSnapshotComparerR20B().Compare(await Snapshot(Raw()),await Snapshot(Raw(name:"Changed"),Role(from:-20,to:40)));
        Assert.Contains(SapBpChangeKindR20B.BP_MASTERDATA_CHANGED,Single(both).Changes);Assert.Contains(SapBpChangeKindR20B.ROLE_VALIDITY_CHANGED,Single(both).Changes);
    }
    [Fact] public async Task UNCHANGED_BP_NO_CHANGE()
    {
        var c=new SapBpEnrichedSnapshotComparerR20B().Compare(await Snapshot(Raw()),await Snapshot(Raw()));Assert.Equal(new[]{SapBpChangeKindR20B.NO_CHANGE},Single(c).Changes);
    }
    [Fact] public async Task EXACT_COMPARISON_DOES_NOT_TRIM_OR_FOLD_CASE()
    {
        var c=new SapBpEnrichedSnapshotComparerR20B().Compare(await Snapshot(Raw(name:"Name")),await Snapshot(Raw(name:" name ")));
        Assert.Contains(SapBpChangeKindR20B.BP_MASTERDATA_CHANGED,Single(c).Changes);
    }
    [Theory][InlineData("missing")][InlineData("null")][InlineData("blank")]
    public async Task INCOMPLETE_ENRICHMENT_FAIL_CLOSED(string mode)
    {
        var raw=JsonSerializer.SerializeToElement(new Dictionary<string,object?>{{"BusinessPartner","0001"},{"BusinessPartnerName","Original"}});
        if(mode!="missing")raw=JsonSerializer.SerializeToElement(new{BusinessPartner="0001",BusinessPartnerName="Original",BusinessPartnerFullName=mode=="null"?null:""});
        var bad=await Snapshot(raw);Assert.False(bad.ConsolidationCandidate);
        var c=new SapBpEnrichedSnapshotComparerR20B().Compare(await Snapshot(Raw()),bad);
        Assert.False(c.CompleteAndValidPair);Assert.Equal(new[]{SapBpChangeKindR20B.INDETERMINATE},Single(c).Changes);
    }
    [Fact] public async Task INCOMPLETE_SNAPSHOT_NOT_PROMOTED()
    {
        var r=new Reader(bp=>bp=="0002"?throw new Exception("offline failure"):new(bp,Raw(bp)));
        var s=await new SapBpSnapshotEnricherR20B(r,new Clock(Now.AddHours(3))).EnrichAsync(await Scan(Role(),Role("0002")),Policy());
        Assert.False(s.Complete);Assert.Single(s.Entries);Assert.False(s.ConsolidationCandidate);
        var c=new SapBpEnrichedSnapshotComparerR20B().Compare(await Snapshot(Raw()),s);
        Assert.All(c.Results,x=>Assert.Equal(new[]{SapBpChangeKindR20B.INDETERMINATE},x.Changes));
    }
    [Theory][InlineData(null)][InlineData("invalid")][InlineData("/Date(0+0199)/")]
    public async Task ROLE_DATE_AMBIGUITY_INDETERMINATE(string? from)
    {
        var s=await Snapshot(Raw(),Role() with{ValidFrom=from});Assert.False(s.ConsolidationCandidate);
        var c=new SapBpEnrichedSnapshotComparerR20B().Compare(await Snapshot(Raw()),s);Assert.Contains(SapBpChangeKindR20B.INDETERMINATE,Single(c).Changes);
    }
    [Fact] public async Task UNKNOWN_TEMPORAL_CONTRACT_INDETERMINATE()
    {
        var s=await Snapshot(Raw(),policy:Policy(temporal:SapRoleTemporalContractR20B.Unknown));Assert.False(s.Valid);
    }
    [Fact] public async Task ROLE_DATE_BOUNDARY_INDETERMINATE_WITHOUT_BOUNDARY_RULE()
    {
        var role=Role() with{ValidTo=Date(Now.AddHours(3))};var s=await Snapshot(Raw(),role);Assert.False(s.Valid);
    }
    [Fact] public async Task ROLE_REFERENCE_FIXED_WITHOUT_END_OF_DAY_EXTENSION()
    {
        var c=new SapBpEnrichedSnapshotComparerR20B().Compare(await Snapshot(Raw(),Role(to:1)),await Snapshot(Raw(),Role(to:1),at:Now.AddDays(2)));
        Assert.Contains(SapBpChangeKindR20B.ELIGIBILITY_EXPIRED,Single(c).Changes);
    }
    [Fact] public async Task CRM010_TRANSITION_DETECTED()
    {
        var c=new SapBpEnrichedSnapshotComparerR20B().Compare(await Snapshot(Raw(),Role(from:1)),await Snapshot(Raw(),Role(from:1),at:Now.AddDays(2)));
        Assert.Contains(SapBpChangeKindR20B.CRM010_ROLE_CHANGED,Single(c).Changes);
    }
    [Fact] public async Task DUPLICATE_BP_CONFLICT()
    {
        var roles=await Scan(Role(),Role(to:20));var reader=new Reader(bp=>new(bp,Raw(bp)));
        var s=await new SapBpSnapshotEnricherR20B(reader,new Clock(Now.AddHours(3))).EnrichAsync(roles,Policy());
        Assert.False(s.Complete);Assert.Empty(reader.Calls);Assert.False(s.ConsolidationCandidate);
    }
    [Fact] public async Task UNAPPROVED_NORMALIZATION_INDETERMINATE()
    {
        var s=await Snapshot(Raw(),policy:Policy(SapBpNormalizationR20B.Unapproved));Assert.False(s.Valid);
        Assert.Contains(SapBpChangeKindR20B.INDETERMINATE,Single(new SapBpEnrichedSnapshotComparerR20B().Compare(s,s)).Changes);
    }
    [Fact] public async Task INCOMPLETE_COMPARISON_SCOPE_NEVER_NO_CHANGE()
    {
        var s=await Snapshot(Raw(),policy:Policy(full:false));Assert.False(s.ConsolidationCandidate);
        Assert.Contains(SapBpChangeKindR20B.INDETERMINATE,Single(new SapBpEnrichedSnapshotComparerR20B().Compare(s,s)).Changes);
    }
    [Fact] public async Task NEW_ELIGIBLE_BP_AND_REMOVED_ROLE_NO_DELETE()
    {
        var empty=await new SapBpSnapshotEnricherR20B(new Reader(bp=>new(bp,Raw(bp))),new Clock(Now.AddHours(3))).EnrichAsync(await Scan(),Policy());
        var full=await Snapshot(Raw());var added=Single(new SapBpEnrichedSnapshotComparerR20B().Compare(empty,full));
        Assert.Contains(SapBpChangeKindR20B.NEW_ELIGIBLE_BP,added.Changes);
        var removed=Single(new SapBpEnrichedSnapshotComparerR20B().Compare(full,empty));Assert.Contains(SapBpChangeKindR20B.REMOVED_ROLE_CANDIDATE,removed.Changes);Assert.False(removed.GuardianDeleteOrDeactivate);
    }
    [Fact] public async Task NO_GUARDIAN_WRITE()
    {
        var c=new SapBpEnrichedSnapshotComparerR20B().Compare(await Snapshot(Raw()),await Snapshot(Raw(name:"Changed")));
        Assert.All(c.Results,x=>{Assert.False(x.GuardianWriteAuthorized);Assert.False(x.GuardianDeleteOrDeactivate);});Assert.False(c.GuardianMappingApproved);
    }
    sealed class Auth:IAutenticacaoSap {public int Calls;public Task AplicarAsync(HttpRequestMessage r,CancellationToken ct){Calls++;return Task.CompletedTask;}}
    sealed class Handler(string body):HttpMessageHandler
    {
        public List<(string Method,Uri Uri)> Requests=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        { Requests.Add((r.Method.Method,r.RequestUri!));return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)}); }
    }
    static SapBpReadAdapterR20B Adapter(Handler h,Auth auth)=>new(new ClienteHttpParceiroNegocioSap(new HttpClient(h),new OpcoesSap{BaseUrl=new("https://sap-hml.example.invalid/sap/opu/odata/sap/API_BUSINESS_PARTNER/"),SapClient="110",Timeout=TimeSpan.FromSeconds(3)},auth));
    [Fact] public async Task NO_REAL_HTTP_OR_DATABASE()
    {
        var h=new Handler(JsonSerializer.Serialize(new{d=Raw()}));var auth=new Auth();
        var s=await new SapBpSnapshotEnricherR20B(Adapter(h,auth),new Clock(Now.AddHours(3))).EnrichAsync(await Scan(Role()),Policy());
        Assert.True(s.ConsolidationCandidate);Assert.Equal(1,auth.Calls);var call=Assert.Single(h.Requests);
        Assert.Equal("GET",call.Method);Assert.Contains("A_BusinessPartner('0001')",Uri.UnescapeDataString(call.Uri.AbsolutePath));Assert.Contains("sap-client=110",call.Uri.Query);
        Assert.DoesNotContain("delta",call.Uri.Query);Assert.DoesNotContain("Center",call.Uri.Query);
    }
    [Fact] public async Task DOCUMENTED_TAX_ADDRESS_CONTACT_FIELDS_PRESERVED()
    {
        var policy=new SapBpComparisonPolicyR20B([SapBpFieldR20B.BusinessPartner,SapBpFieldR20B.BusinessPartnerTax,SapBpFieldR20B.BusinessPartnerAddress],true,true,"OFFLINE_COLLECTION_SCOPE",temporalContract:SapRoleTemporalContractR20B.EdmDateTimeOffset);
        var s=await Snapshot(Raw(),policy:policy);Assert.True(s.Valid);var item=Assert.Single(s.Entries);
        Assert.Contains("OFFLINE_PHONE",item.NormalizedSapData["BusinessPartnerAddress"]);Assert.Contains("City",item.NormalizedSapData["BusinessPartnerAddress"]);
        Assert.Contains("OFFLINE_VALUE",item.NormalizedSapData["BusinessPartnerTax"]);
    }
    [Fact] public async Task PAGED_NAVIGATION_NOT_PROMOTED()
    {
        var raw=Raw().GetRawText().Replace("\"to_BusinessPartnerTax\":{\"results\":","\"to_BusinessPartnerTax\":{\"__next\":\"offline-next\",\"results\":");
        var h=new Handler("{\"d\":"+raw+"}");var result=await Adapter(h,new Auth()).ReadAsync("0001",default);
        Assert.False(result.Complete);Assert.Equal("BP_NAVIGATION_INCOMPLETE",result.Reason);
    }
    [Fact] public async Task DUPLICATE_JSON_IDENTITY_CONFLICT()
    {
        var h=new Handler("{\"d\":{\"BusinessPartner\":\"0001\",\"BusinessPartner\":\"OTHER\"}}");
        var result=await Adapter(h,new Auth()).ReadAsync("0001",default);Assert.False(result.Complete);Assert.Equal("BP_DUPLICATE_FIELD_CONFLICT",result.Reason);
    }
    [Fact] public async Task CANCELED_ENRICHMENT_NO_PARTIAL_PROMOTION()
    {
        using var cts=new CancellationTokenSource();var r=new Reader(bp=>new(bp,Raw(bp))){OnRead=()=>cts.Cancel()};
        var s=await new SapBpSnapshotEnricherR20B(r,new Clock(Now.AddHours(3))).EnrichAsync(await Scan(Role(),Role("0002")),Policy(),cts.Token);
        Assert.False(s.Complete);Assert.Equal("CANCELED",s.Reason);Assert.Single(r.Calls);
    }
    [Theory][InlineData(true)][InlineData(false)] public async Task EMPTY_SNAPSHOT_STILL_REQUIRES_APPROVED_SEMANTICS(bool normalization)
    {
        var policy=normalization?Policy(SapBpNormalizationR20B.Unapproved):Policy(temporal:SapRoleTemporalContractR20B.Unknown);
        var s=await new SapBpSnapshotEnricherR20B(new Reader(bp=>new(bp,Raw(bp))),new Clock(Now.AddHours(3))).EnrichAsync(await Scan(),policy);
        Assert.False(s.ConsolidationCandidate);Assert.False(s.Valid);
    }
    [Theory][InlineData(true)][InlineData(false)] public async Task ZERO_OR_REVERSED_ROLE_INTERVAL_INDETERMINATE(bool zero)
    {
        var s=await Snapshot(Raw(),zero?Role(from:3,to:3):Role(from:30,to:-30));Assert.False(s.Valid);
    }
    static SapBpComparisonPolicyR20B CollectionPolicy()=>new([SapBpFieldR20B.BusinessPartner,SapBpFieldR20B.BusinessPartnerTax,SapBpFieldR20B.BusinessPartnerAddress],true,true,"OFFLINE_COLLECTION_SCOPE",temporalContract:SapRoleTemporalContractR20B.EdmDateTimeOffset);
    [Theory][InlineData("City")][InlineData("Street")][InlineData("OFFLINE_PHONE")][InlineData("OFFLINE_VALUE")][InlineData("offline@example.invalid")]
    public async Task DOCUMENTED_NESTED_ATTRIBUTE_CHANGE_DETECTED(string old)
    {
        using var doc=JsonDocument.Parse(Raw().GetRawText().Replace(JsonSerializer.Serialize(old),JsonSerializer.Serialize(old+"_CHANGED"),StringComparison.Ordinal));
        var c=new SapBpEnrichedSnapshotComparerR20B().Compare(await Snapshot(Raw(),policy:CollectionPolicy()),await Snapshot(doc.RootElement.Clone(),policy:CollectionPolicy()));
        Assert.True(c.CompleteAndValidPair);Assert.Equal(new[]{SapBpChangeKindR20B.BP_MASTERDATA_CHANGED},Single(c).Changes);
    }
    [Fact] public async Task MISSING_DOCUMENTED_ADDRESS_FIELD_INDETERMINATE()
    {
        using var doc=JsonDocument.Parse(Raw().GetRawText().Replace("\"CityName\":\"City\",",""));
        var s=await Snapshot(doc.RootElement.Clone(),policy:CollectionPolicy());Assert.False(s.Valid);Assert.False(s.ConsolidationCandidate);
    }
    [Fact] public async Task REFERENCE_TIME_FIXED_FOR_MULTIPLE_BPS()
    {
        var clock=new Clock(Now.AddHours(3));var s=await new SapBpSnapshotEnricherR20B(new Reader(bp=>new(bp,Raw(bp))),clock).EnrichAsync(await Scan(Role(),Role("0002")),Policy());
        Assert.True(s.Valid);Assert.Equal(2,s.Entries.Count);Assert.Equal(1,clock.Calls);Assert.Equal(Now.AddHours(3),s.ReferenceAt);
    }
    [Fact] public async Task GET_ROLE_MUTATION_DURING_ENRICHMENT_BLOCKED()
    {
        var root=JsonSerializer.SerializeToElement(new{BusinessPartner="0001",BusinessPartnerName="Name",BusinessPartnerFullName="Full",
            to_BusinessPartnerRole=new{results=new[]{new{BusinessPartnerRole="CRM010",ValidFrom=Date(Now.AddDays(-20)),ValidTo=Date(Now.AddDays(30))}}}});
        var s=await Snapshot(root);Assert.False(s.Complete);Assert.Equal("BP_ROLE_CHANGED_DURING_ENRICHMENT",s.Reason);
    }
    [Fact] public async Task APPROVED_SCOPE_DIFFERENCE_BLOCKS_COMPARISON()
    {
        var other=new SapBpComparisonPolicyR20B(Policy().Fields,true,true,"OFFLINE_DIFFERENT_SCOPE",temporalContract:SapRoleTemporalContractR20B.EdmDateTimeOffset);
        var c=new SapBpEnrichedSnapshotComparerR20B().Compare(await Snapshot(Raw()),await Snapshot(Raw(),policy:other));
        Assert.False(c.CompleteAndValidPair);Assert.Contains(SapBpChangeKindR20B.INDETERMINATE,Single(c).Changes);
    }

}
