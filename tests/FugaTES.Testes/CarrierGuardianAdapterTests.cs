using System.Net;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Integracao.Guardian;
namespace FugaTES.Testes;
public sealed class CarrierGuardianAdapterTests
{
    [Fact] public async Task Unproven_HML_profile_blocks_lookup_and_write()
    {
        var a=new GuardianCarrierSyncAdapter();var lookup=await a.ConsultarAsync("000001",default);
        Assert.Equal(CarrierLookupState.Failed,lookup.State);Assert.Equal("GUARDIAN_CARRIER_HML_CONTRACT_NOT_PROVEN",lookup.ErrorCode);
        var w=await a.GravarAsync(Attempt(),default);Assert.False(w.Accepted);Assert.False(w.Indeterminate);
    }
    [Fact] public async Task Explicit_absence_allows_absent_result(){var h=new Handler(HttpStatusCode.NotFound);Assert.Equal(CarrierLookupState.Absent,(await Adapter(h).ConsultarAsync("000001",default)).State);}
    [Theory][InlineData(401)][InlineData(403)][InlineData(500)]public async Task Failed_GET_never_means_absent(int status){Assert.Equal(CarrierLookupState.Failed,(await Adapter(new Handler((HttpStatusCode)status)).ConsultarAsync("000001",default)).State);}
    [Fact]public async Task Undocumented_404_is_failure(){Assert.Equal(CarrierLookupState.Failed,(await Adapter(new Handler(HttpStatusCode.NotFound),Contract() with{AbsenceHttpStatus=null}).ConsultarAsync("000001",default)).State);}
    [Fact]public async Task Identity_mismatch_is_rejected(){Assert.Equal(CarrierLookupState.Failed,(await Adapter(new Handler(HttpStatusCode.OK,"{\"bp\":\"other\"}")).ConsultarAsync("000001",default)).State);}
    [Fact]public async Task External_relative_route_cannot_leak_token(){var h=new Handler(HttpStatusCode.OK);var r=await Adapter(h,Contract() with{LookupRoute="//evil.invalid/{bp}"}).ConsultarAsync("000001",default);Assert.Equal(CarrierLookupState.Failed,r.State);Assert.Equal(0,h.TotalCalls);}
    [Fact]public async Task Unauthorized_write_is_not_sent(){var h=new Handler(HttpStatusCode.OK);var r=await Adapter(h,allowed:false).GravarAsync(Attempt(),default);Assert.False(r.Accepted);Assert.Equal(0,h.TotalCalls);}
    [Theory][InlineData("CREATE","POST")][InlineData("UPDATE","PUT")]public async Task Write_uses_exact_method_and_existing_Bearer_auth(string action,string method)
    {
        var h=new Handler(HttpStatusCode.OK,"{\"code\":\"G1\"}");var w=await Adapter(h).GravarAsync(Attempt() with{Action=action,GuardianCode=action=="UPDATE"?"G1":null},default);
        Assert.True(w.Accepted);Assert.False(w.Indeterminate);Assert.Equal(method,h.CarrierMethod);Assert.Equal("Bearer",h.AuthScheme);Assert.Equal(1,h.CarrierCalls);
    }
    [Fact]public async Task Timeout_write_is_indeterminate_and_not_retried(){var h=new Handler(HttpStatusCode.OK){TimeoutWrite=true};var w=await Adapter(h).GravarAsync(Attempt(),default);Assert.True(w.Indeterminate);Assert.False(w.Accepted);Assert.Equal(1,h.CarrierCalls);}
    [Fact]public async Task Invalid_payload_is_not_truncated_or_sent(){var h=new Handler(HttpStatusCode.OK);var w=await Adapter(h).GravarAsync(Attempt() with{Payload=new("000001",new string('x',51),"Carrier")},default);Assert.False(w.Accepted);Assert.Equal(0,h.TotalCalls);}
    [Fact]public async Task Readback_requires_matching_Guardian_code(){var r=await Adapter(new Handler(HttpStatusCode.OK,"{\"bp\":\"000001\",\"code\":\"G2\"}")).LerConfirmacaoAsync("G1",default);Assert.Equal(CarrierLookupState.Failed,r.State);}
    [Fact] public async Task Authorization_is_revalidated_immediately_before_dispatch()
    {
        var h=new Handler(HttpStatusCode.OK,"{\"code\":\"G1\"}");var http=new HttpClient(h);var o=new GuardianHttpOptions{BaseUrl=new("https://guardian.invalid/"),Timeout=TimeSpan.FromSeconds(1)};int checks=0;
        var a=new GuardianCarrierSyncAdapter(http,o,new GuardianTokenClient(http,o,new Credentials()),Contract(),_=>++checks==1);
        var r=await a.GravarAsync(Attempt(),default);Assert.False(r.Accepted);Assert.False(r.Indeterminate);Assert.Equal(0,h.CarrierCalls);
    }
    [Fact] public async Task Caller_cancellation_after_send_is_indeterminate_without_retry()
    {
        using var cancel=new CancellationTokenSource();var h=new Handler(HttpStatusCode.OK){TimeoutWrite=true,OnWrite=()=>cancel.Cancel()};
        var r=await Adapter(h).GravarAsync(Attempt(),cancel.Token);Assert.True(r.Indeterminate);Assert.Equal(1,h.CarrierCalls);
    }
    [Fact] public async Task Server_write_error_is_indeterminate_without_retry()
    {
        var h=new Handler(HttpStatusCode.InternalServerError);var r=await Adapter(h).GravarAsync(Attempt(),default);Assert.True(r.Indeterminate);Assert.Equal(1,h.CarrierCalls);
    }
    private static CarrierWriteAttempt Attempt()=>new(Guid.NewGuid(),"000001",null,"CREATE",new("000001","Carrier","Carrier Ltd"));
    private static GuardianCarrierHmlContract Contract()=>new(true,true,"fixture-HML-proof","/lookup/{bp}","/create","/update/{code}","/readback/{code}",404,j=>new(CarrierLookupState.Unique,j.TryGetProperty("code",out var c)?c.GetString():"G1",new(j.GetProperty("bp").GetString()!,"Carrier","Carrier Ltd")),a=>new{codigo=a.Payload.BusinessPartner,descricao=a.Payload.Name,razaoSocial=a.Payload.LegalName},j=>j.GetProperty("code").GetString());
    private static GuardianCarrierSyncAdapter Adapter(Handler h,GuardianCarrierHmlContract? c=null,bool allowed=true)
    {
        var http=new HttpClient(h);var o=new GuardianHttpOptions{BaseUrl=new("https://guardian.invalid/"),Timeout=TimeSpan.FromSeconds(1)};
        return new(http,o,new GuardianTokenClient(http,o,new Credentials()),c??Contract(),_=>allowed);
    }
    private sealed class Credentials:ICredenciaisGuardianSet26Provider {public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct)=>Task.FromResult(new CredenciaisGuardianSet26("fixture","fixture","fixture"));}
    private sealed class Handler(HttpStatusCode status,string json="{\"bp\":\"000001\",\"code\":\"G1\"}"):HttpMessageHandler
    {
        public int TotalCalls,CarrierCalls;public bool TimeoutWrite;public Action? OnWrite;public string? CarrierMethod,AuthScheme;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            TotalCalls++;if(r.RequestUri!.AbsolutePath=="/api/v2/CreateToken")return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"token\":\"fixture-token\"}")});
            CarrierCalls++;CarrierMethod=r.Method.Method;AuthScheme=r.Headers.Authorization?.Scheme;
            if(TimeoutWrite && r.Method!=HttpMethod.Get){OnWrite?.Invoke();throw new TaskCanceledException();}
            return Task.FromResult(new HttpResponseMessage(status){Content=new StringContent(json)});
        }
    }
}


