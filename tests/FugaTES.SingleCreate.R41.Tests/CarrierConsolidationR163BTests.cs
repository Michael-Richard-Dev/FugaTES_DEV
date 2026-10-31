using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Aplicacao.Transportadoras.SingleCreateR31;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Persistencia.Transportadoras;
using FugaTES.WinForms.Transportadoras.MasterdataR3;
using Microsoft.Extensions.Configuration;
using FugaTES.Integracao.Guardian;
using System.Net;
using FugaTES.Aplicacao.Contratos.Recebimento;

namespace FugaTES.SingleCreate.R41.Tests;

public class CarrierConsolidationR163BTests
{
    const string Bp="14300001";
    sealed class Sap:ISapBpMasterReaderR20B
    {
        public string Name="SYNTHETIC Nome á & \"texto\"",Legal="SYNTHETIC Razão",Tax="12ABC34501DE35";
        public bool Fail;
        public Task<SapBpReadResultR20B> ReadAsync(string bp,CancellationToken ct)
        {
            if(Fail)throw new InvalidOperationException("PRIVATE_ERROR_NOT_FOR_UI");
            var raw=JsonSerializer.SerializeToElement(new{BusinessPartner=bp,BusinessPartnerName=Name,BusinessPartnerFullName=Legal,BusinessPartnerIsBlocked=false,
                to_BusinessPartnerRole=new{results=new[]{new{BusinessPartner=bp,BusinessPartnerRole="CRM010",ValidFrom="/Date(1609459200000)/",ValidTo="/Date(1893456000000)/"}}},
                to_BusinessPartnerTax=new{results=new[]{new{BusinessPartner=bp,BPTaxType="BR1",BPTaxNumber=Tax,BPTaxLongNumber=Tax}}}});
            return Task.FromResult(new SapBpReadResultR20B(bp,raw,true));
        }
    }
    sealed class Reader:IGuardianCarrierManualReadOnly
    {
        public bool Fail,Exists;
        public Task<CarrierManualGuardian> ConsultarCodigoAsync(CarrierManualIdentity identity,CancellationToken ct)
        {
            if(Fail)throw new InvalidOperationException("PRIVATE_ERROR_NOT_FOR_UI");
            return Task.FromResult(new CarrierManualGuardian(Exists?CarrierLookupState.Unique:CarrierLookupState.Absent,Exists?Bp:null,
                new Dictionary<string,string?>{{"sequencial","271"}},!Exists));
        }
    }
    static IConfiguration Config(bool legacyField=true)
    {
        var d=new Dictionary<string,string?>();
        var p="Carrier:Protocols:Guardian:";
        foreach(var pair in new Dictionary<string,string?>{{"Environment","HML"},{"ProfileVerified","true"},{"SignatureVerified","true"},{"ApprovalReference","SYNTHETIC"},
            {"LookupRoute","/api/v0/Guardian/Transportadora/{bp}?idAssinatura=133289"},{"CreateRoute","/api/v0/Guardian/Transportadora"},{"UpdateRoute","/api/v0/Guardian/Transportadora"},
            {"ReadbackRoute","/api/v0/Guardian/Transportadora/{code}"},{"RowsPath","$"},{"CodeField","codigo"},{"NameField","descricao"},{"LegalNameField","razaoSocial"},
            {"WriteCodeField","codigo"},{"WriteNameField","descricao"},{"WriteLegalNameField","razaoSocial"},{"ResponseCodePath","codigo"},{"AbsenceHttpStatus","404"}})d[p+pair.Key]=pair.Value;
        if(legacyField)d[p+"BusinessPartnerField"]="BusinessPartner";
        var i=$"Carrier:ManualReadOnly:Identities:{Bp}:";
        d[i+"Proven"]="true";d[i+"Code"]=Bp;d[i+"GetSignatureId"]="133289";d[i+"ApprovalReference"]="SYNTHETIC";
        var t=$"Masterdata:Create:Targets:{Bp}:";
        foreach(var flag in new[]{"CodeApproved","EstadoApproved","PayloadContractApproved"})d[t+flag]="true";
        d[t+"Estado"]="1";d[t+"ExecutionApprovalReference"]="SYNTHETIC";
        d["Masterdata:Create:CallbackApproved"]="true";d["Masterdata:Create:CallbackUrl"]="https://callback.example.invalid/fixed";
        d["Masterdata:Create:UntilUtc"]=DateTimeOffset.UtcNow.AddMinutes(5).ToString("O");
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }
    static CarrierCreateHistoryEntry Confirmed()=>new(Guid.NewGuid(),Bp,Bp,"CONFIRMED",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);
    [Fact] public async Task Unchanged_preview_is_exact_dispatched_body()
    {
        var sap=new Sap();byte[]? sent=null;
        await using var s=new CarrierMasterdataSession(Config(),Bp,sap,new Reader(),_=>Task.FromResult<IReadOnlyList<CarrierCreateHistoryEntry>>([]),
            (p,r,c,ct)=>{sent=p.CopyBody();return Task.FromResult(new SingleCreateResultR31(r.AttemptId,SingleCreateStateR31.CONFIRMED,"CONFIRMED"));},()=>true);
        await s.ConsultAsync(default);var shown=s.JsonPreview;await s.CreateAsync(default);
        Assert.Equal(shown,Encoding.UTF8.GetString(sent!));
    }
    [Theory][InlineData("name")][InlineData("legal")][InlineData("cnpj")]
    public async Task Changed_data_never_replaces_approved_preview_or_dispatches(string field)
    {
        var sap=new Sap();var sends=0;
        await using var s=new CarrierMasterdataSession(Config(),Bp,sap,new Reader(),_=>Task.FromResult<IReadOnlyList<CarrierCreateHistoryEntry>>([]),
            (p,r,c,ct)=>{sends++;return Task.FromResult(new SingleCreateResultR31(r.AttemptId,SingleCreateStateR31.CONFIRMED,"CONFIRMED"));},()=>true);
        await s.ConsultAsync(default);var shown=s.JsonPreview;
        if(field=="name")sap.Name+=" CHANGED";if(field=="legal")sap.Legal+=" CHANGED";if(field=="cnpj")sap.Tax="04.252.011/0001-10";
        var result=await s.CreateAsync(default);
        Assert.Equal(SingleCreateStateR31.BLOCKED,result.State);Assert.Equal(0,sends);Assert.Equal(shown,s.JsonPreview);
    }
    [Fact] public async Task Persisted_confirmation_survives_sap_and_guardian_failure()
    {
        var row=Confirmed();var sap=new Sap{Fail=true};var reader=new Reader{Fail=true};var sends=0;
        await using var s=new CarrierMasterdataSession(Config(),Bp,sap,reader,_=>Task.FromResult<IReadOnlyList<CarrierCreateHistoryEntry>>([row]),
            (p,r,c,ct)=>{sends++;throw new Exception();},()=>true);
        await s.ConsultAsync(default);
        Assert.Equal(row,Assert.Single(s.History));Assert.Contains("EXISTING_CONFIRMED",s.CreateReadiness);Assert.False(s.CanCreate);
        Assert.Equal(0,sends);
    }
    [Theory][InlineData(SingleCreateStateR31.CONFIRMED)][InlineData(SingleCreateStateR31.INDETERMINATE)]
    public async Task Post_result_is_not_lost_when_history_refresh_fails(SingleCreateStateR31 outcome)
    {
        var reads=0;var sends=0;
        await using var s=new CarrierMasterdataSession(Config(),Bp,new Sap(),new Reader(),_=>++reads>=3?throw new InvalidOperationException("PRIVATE_ERROR_NOT_FOR_UI"):Task.FromResult<IReadOnlyList<CarrierCreateHistoryEntry>>([]),
            (p,r,c,ct)=>{sends++;return Task.FromResult(new SingleCreateResultR31(r.AttemptId,outcome,"SYNTHETIC_RESULT"));},()=>true);
        await s.ConsultAsync(default);var result=await s.CreateAsync(default);
        Assert.Equal(outcome,result.State);Assert.False(s.CanCreate);Assert.Equal(1,sends);
        await s.CreateAsync(default);Assert.Equal(1,sends);
    }
    [Fact] public async Task Confirmed_get_sequence_survives_later_read_failure()
    {
        var row=Confirmed();var reader=new Reader{Exists=true};var sap=new Sap();
        await using var s=new CarrierMasterdataSession(Config(),Bp,sap,reader,_=>Task.FromResult<IReadOnlyList<CarrierCreateHistoryEntry>>([row]),(p,r,c,ct)=>throw new Exception(),()=>false);
        await s.ConsultAsync(default);Assert.Equal(271,s.ReturnedSequence);
        reader.Fail=true;sap.Fail=true;await s.ConsultAsync(default);
        Assert.Equal(271,s.ReturnedSequence);Assert.Equal(row,Assert.Single(s.History));Assert.False(s.CanCreate);
    }
    [Fact] public void Approved_guardian_contract_does_not_require_fabricated_bp_field()
        =>Assert.NotNull(CarrierProtocolBinding.BuildGuardianContract(Config(false)));

    [Fact] public async Task Confirmed_history_is_monotonic_when_later_history_is_empty()
    {
        var row=Confirmed();var reads=0;
        await using var s=new CarrierMasterdataSession(Config(),Bp,new Sap(),new Reader{Fail=true},
            _=>Task.FromResult<IReadOnlyList<CarrierCreateHistoryEntry>>(++reads==1?[row]:[]),(p,r,c,ct)=>throw new Exception(),()=>true);
        await s.ConsultAsync(default);await s.ConsultAsync(default);
        Assert.Equal(row,Assert.Single(s.History));Assert.False(s.CanCreate);Assert.Contains("EXISTING_CONFIRMED",s.CreateReadiness);
    }
    [Fact] public async Task Initial_history_failure_never_authorizes_create()
    {
        await using var s=new CarrierMasterdataSession(Config(),Bp,new Sap(),new Reader(),_=>throw new InvalidOperationException("PRIVATE_ERROR_NOT_FOR_UI"),
            (p,r,c,ct)=>throw new Exception(),()=>true);
        await s.ConsultAsync(default);Assert.False(s.CanCreate);Assert.Contains("MASTERDATA_HISTORY_READ_FAILED",s.HistoryReadStatus);
        Assert.DoesNotContain("PRIVATE_ERROR",s.HistoryReadStatus);
    }
    [Fact] public async Task Observed_existence_followed_by_404_never_reopens_create()
    {
        var reader=new Reader{Exists=true};
        await using var s=new CarrierMasterdataSession(Config(),Bp,new Sap(),reader,_=>Task.FromResult<IReadOnlyList<CarrierCreateHistoryEntry>>([]),
            (p,r,c,ct)=>throw new Exception(),()=>true);
        await s.ConsultAsync(default);reader.Exists=false;await s.ConsultAsync(default);Assert.False(s.CanCreate);
    }
    [Theory][InlineData("PREPARED")][InlineData("INDETERMINATE")]
    public async Task Pending_attempt_followed_by_empty_refresh_never_reopens_create(string status)
    {
        var row=Confirmed() with{Status=status,ConfirmedUtc=null};var reads=0;
        await using var s=new CarrierMasterdataSession(Config(),Bp,new Sap(),new Reader(),
            _=>Task.FromResult<IReadOnlyList<CarrierCreateHistoryEntry>>(++reads==1?[row]:[]),(p,r,c,ct)=>throw new Exception(),()=>true);
        await s.ConsultAsync(default);await s.ConsultAsync(default);Assert.False(s.CanCreate);Assert.Equal(row,Assert.Single(s.History));
    }
    [Fact] public async Task Cancelled_post_confirmation_refresh_does_not_hide_confirmed_result()
    {
        using var cancel=new CancellationTokenSource();
        await using var s=new CarrierMasterdataSession(Config(),Bp,new Sap(),new Reader(),ct=>{
            ct.ThrowIfCancellationRequested();return Task.FromResult<IReadOnlyList<CarrierCreateHistoryEntry>>([]);},
            (p,r,c,ct)=>{cancel.Cancel();return Task.FromResult(new SingleCreateResultR31(r.AttemptId,SingleCreateStateR31.CONFIRMED,"CONFIRMED"));},()=>true);
        await s.ConsultAsync(default);var result=await s.CreateAsync(cancel.Token);
        Assert.Equal(SingleCreateStateR31.CONFIRMED,result.State);Assert.Equal(result,s.LastResult);Assert.False(s.CanCreate);
    }
    sealed class Credentials:ICredenciaisGuardianSet26Provider
    {public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct)=>Task.FromResult(new CredenciaisGuardianSet26("SYNTHETIC","SYNTHETIC_PASSWORD","SYNTHETIC_SIGNATURE"));}
    sealed class Journal:ISingleCreateJournalR31
    {
        public bool Prepared,Confirmed;public string? Digest;
        public Task<bool> TryPrepareAsync(Guid id,string bp,string code,string hash,CancellationToken ct){Prepared=true;Digest=hash;return Task.FromResult(true);}
        public Task ConfirmAsync(Guid id,CancellationToken ct){Confirmed=true;return Task.CompletedTask;}
    }
    sealed class Wire(Journal journal):HttpMessageHandler
    {
        public byte[]? Sent;public int Creates;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            Assert.Equal("offline.invalid",r.RequestUri!.Host);
            if(r.RequestUri.AbsolutePath=="/api/v2/CreateToken")return new(HttpStatusCode.OK){Content=new StringContent("{\"token\":\"SYNTHETIC_TOKEN\"}")};
            if(r.Method==HttpMethod.Get)return new(HttpStatusCode.OK){Content=new StringContent("{\"codigo\":\"14300001\",\"sequencial\":271,\"razaoSocial\":\"SYNTHETIC\"}")};
            Assert.True(journal.Prepared);Assert.Equal(HttpMethod.Post,r.Method);Creates++;Sent=await r.Content!.ReadAsByteArrayAsync(ct);
            return new(HttpStatusCode.OK){Content=new StringContent("{\"codigo\":\"14300001\",\"sequencial\":271}")};
        }
    }
    [Fact] public async Task Real_adapter_serialization_matches_preview_including_unicode_and_fiscal_data()
    {
        var c=Config(false);var journal=new Journal();var wire=new Wire(journal);using var http=new HttpClient(wire);
        var options=new GuardianHttpOptions{BaseUrl=new("https://offline.invalid"),Timeout=TimeSpan.FromSeconds(10)};
        var tokens=new GuardianTokenClient(http,options,new Credentials());var reader=new Reader();
        await using var s=new CarrierMasterdataSession(c,Bp,new Sap(),reader,_=>Task.FromResult<IReadOnlyList<CarrierCreateHistoryEntry>>([]),
            (p,r,contract,ct)=>p.CreateBoundUseCase(reader,http,options,tokens,contract,journal,()=>true).ExecuteAsync(r,ct),()=>true);
        await s.ConsultAsync(default);var shown=s.JsonPreview;var result=await s.CreateAsync(default);
        Assert.Equal(SingleCreateStateR31.CONFIRMED,result.State);Assert.True(journal.Confirmed);Assert.Equal(1,wire.Creates);
        Assert.Equal(shown,Encoding.UTF8.GetString(wire.Sent!));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wire.Sent!)).ToLowerInvariant(),journal.Digest);
        Assert.DoesNotContain("SYNTHETIC_PASSWORD",shown);Assert.DoesNotContain("SYNTHETIC_SIGNATURE",shown);Assert.DoesNotContain("SYNTHETIC_TOKEN",shown);
    }
    [Fact] public async Task Production_manual_adapter_preserves_numeric_sequence_separately_from_identity()
    {
        using var http=new HttpClient(new Wire(new Journal()));var options=new GuardianHttpOptions{BaseUrl=new("https://offline.invalid"),Timeout=TimeSpan.FromSeconds(10)};
        var adapter=new GuardianCarrierManualReadOnlyAdapter(http,options,new GuardianTokenClient(http,options,new Credentials()),
            new(true,true,"SYNTHETIC","/api/v0/Guardian/Transportadora/{bp}?idAssinatura=133289","codigo","razaoSocial",true));
        var result=await adapter.ConsultarCodigoAsync(new(Bp,"133289",true,"SYNTHETIC"),default);
        Assert.Equal(Bp,result.Codigo);Assert.Equal("271",result.Campos["sequencial"]);Assert.Equal(CarrierLookupState.Unique,result.State);
    }
}
