using System.Net;
using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Integracao.Guardian;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using FugaTES.WinForms.Transportadoras;
namespace FugaTES.Testes;
public class CarrierManualReadOnlyHttpR15Tests
{
    static readonly Uri SapOrigin=new("https://vhfufds4ci.sap.fugacouros.com.br:44300/sap/opu/odata/sap/API_BUSINESS_PARTNER/");
    static readonly Uri GuardianOrigin=new("https://guardian.cloudprix.com.br/");
    static readonly DateTimeOffset Now=new(2026,10,11,12,0,0,TimeSpan.Zero);
    sealed class Clock:TimeProvider {public override DateTimeOffset GetUtcNow()=>Now;}
    sealed class Auth:IAutenticacaoSap {public int Calls;public Task AplicarAsync(HttpRequestMessage r,CancellationToken ct){ct.ThrowIfCancellationRequested();Calls++;return Task.CompletedTask;}}
    sealed class Credentials:ICredenciaisGuardianSet26Provider {public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct)=>Task.FromResult(new CredenciaisGuardianSet26("OFFLINE_FIXTURE","OFFLINE_FIXTURE","TOKEN_SIGNATURE_DISTINCT_FROM_GET_ID"));}
    sealed class Handler:HttpMessageHandler {
        public List<(string Method,Uri Uri)> Calls=[];public HttpStatusCode GuardianStatus=HttpStatusCode.OK;public string GuardianBody="{\"codigo\":\"GUARDIAN42\",\"razaoSocial\":\"Outra razao\",\"sequencial\":\"0000000042\"}";
        public string SapBody=JsonSerializer.Serialize(new{d=new{BusinessPartner="0000000042",BusinessPartnerFullName="Razao SAP",BusinessPartnerName="Nome",BusinessPartnerIsBlocked=false,to_BusinessPartnerRole=new{results=new[]{new{BusinessPartner="0000000042",BusinessPartnerRole="CRM010",ValidFrom="/Date("+Now.AddDays(-30).ToUnixTimeMilliseconds()+")/",ValidTo="/Date("+Now.AddDays(30).ToUnixTimeMilliseconds()+")/"}}}}});
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){
            ct.ThrowIfCancellationRequested();Calls.Add((r.Method.Method,r.RequestUri!));
            if(r.RequestUri!.AbsolutePath=="/api/v2/CreateToken")return Task.FromResult(Response(HttpStatusCode.OK,"{\"token\":\"OFFLINE_FIXTURE_TOKEN\"}"));
            return Task.FromResult(r.RequestUri.Host==SapOrigin.Host?Response(HttpStatusCode.OK,SapBody):Response(GuardianStatus,GuardianBody));
        }
        static HttpResponseMessage Response(HttpStatusCode status,string body)=>new(status){Content=new StringContent(body)};
    }
    static GuardianCarrierManualContract Contract()=>new(true,true,"OFFLINE_VERIFIED_SCOPE","/api/v0/Guardian/Transportadora/{bp}?idAssinatura=SIG_GET42","codigo","razaoSocial",true);
    static CarrierManualIdentity Identity()=>new("GUARDIAN42","SIG_GET42",true,"OFFLINE_CODE_BINDING");
    static ConsultarTransportadoraManualReadOnlyCasoDeUso Case(Handler h,bool complete=false)
    {
        var http=new HttpClient(new CarrierManualReadOnlyHttpGuard(SapOrigin,GuardianOrigin,h));
        var options=new GuardianHttpOptions{BaseUrl=GuardianOrigin,Timeout=TimeSpan.FromSeconds(2)};
        var sap=new CarrierManualSapReadOnlyAdapter(new ClienteHttpParceiroNegocioSap(http,new OpcoesSap{BaseUrl=SapOrigin,SapClient="110",Timeout=TimeSpan.FromSeconds(2)},new Auth()));
        var guardian=new GuardianCarrierManualReadOnlyAdapter(http,options,new GuardianTokenClient(http,options,new Credentials()),Contract());
        return new(sap,guardian,_=>true,_=>Identity(),new([new("BusinessPartnerFullName","razaoSocial",true)],complete,"OFFLINE_COMPLETE_CONTRACT"),new Clock());
    }
    [Theory][InlineData(200,DecisaoCarrierManual.UPDATE_CANDIDATE)][InlineData(404,DecisaoCarrierManual.CREATE_CANDIDATE)][InlineData(500,DecisaoCarrierManual.INDETERMINATE)][InlineData(403,DecisaoCarrierManual.INDETERMINATE)]
    public async Task GUARDIAN_GET_200_404_executed_through_real_readonly_adapters(int status,DecisaoCarrierManual expected)
    {
        var h=new Handler{GuardianStatus=(HttpStatusCode)status};var result=await Case(h).ExecutarAsync("0000000042",default);Assert.Equal(expected,result.Decisao);
        var sap=Assert.Single(h.Calls,c=>c.Uri.Host==SapOrigin.Host);Assert.Equal("GET",sap.Method);Assert.Contains("A_BusinessPartner('0000000042')",Uri.UnescapeDataString(sap.Uri.AbsolutePath));Assert.Contains("to_BusinessPartnerRole",Uri.UnescapeDataString(sap.Uri.Query));Assert.DoesNotContain("5001",sap.Uri.ToString());Assert.Contains("sap-client=110",sap.Uri.Query);
        var get=Assert.Single(h.Calls,c=>c.Uri.Host==GuardianOrigin.Host&&c.Method=="GET");Assert.EndsWith("/GUARDIAN42",get.Uri.AbsolutePath);Assert.Contains("idAssinatura=SIG_GET42",get.Uri.Query);
        Assert.All(h.Calls.Where(c=>c.Uri.AbsolutePath!="/api/v2/CreateToken"),c=>Assert.Equal("GET",c.Method));
    }
    [Fact] public async Task NO_ACTION_real_adapters_require_complete_comparison_proof()
    {
        var h=new Handler{GuardianBody="{\"codigo\":\"GUARDIAN42\",\"razaoSocial\":\"Razao SAP\"}"};Assert.Equal(DecisaoCarrierManual.INDETERMINATE,(await Case(h).ExecutarAsync("0000000042",default)).Decisao);
        Assert.Equal(DecisaoCarrierManual.NO_ACTION,(await Case(new Handler{GuardianBody=h.GuardianBody},complete:true).ExecutarAsync("0000000042",default)).Decisao);
    }
    [Theory][InlineData("/Date(0)/",true)][InlineData("/Date(0+0300)/",true)][InlineData("/Date(0+0199)/",false)][InlineData("/Date(0+1500)/",false)][InlineData("not-date",false)][InlineData("/Date(999999999999999999)/",false)]
    public void CRM010_OData_V2_parser_does_not_accept_invalid_dates_or_offsets(string input,bool valid)=>Assert.Equal(valid,CarrierManualODataDateParser.Parse(input).HasValue);
    [Fact] public async Task CRM010_DATE_INDETERMINATE_from_executed_SAP_JSON_parser()
    {
        var h=new Handler();h.SapBody=h.SapBody.Replace("/Date("+Now.AddDays(30).ToUnixTimeMilliseconds()+")/","/Date("+Now.ToUnixTimeMilliseconds()+")/");
        Assert.Equal(DecisaoCarrierManual.INDETERMINATE,(await Case(h).ExecutarAsync("0000000042",default)).Decisao);Assert.Single(h.Calls);Assert.Equal(SapOrigin.Host,h.Calls[0].Uri.Host);
    }
    [Theory][InlineData("PUT","https://guardian.cloudprix.com.br/api/v0/Guardian/Transportadora")][InlineData("POST","https://guardian.cloudprix.com.br/api/v0/Guardian/Transportadora")][InlineData("POST","https://vhfufds4ci.sap.fugacouros.com.br:44300/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner('42')")][InlineData("GET","https://guardian.cloudprix.com.br/api/v0/Guardian/Transportadora")][InlineData("POST","https://evil.invalid/api/v2/CreateToken")]
    public async Task WRITE_GUARD_blocks_business_methods_before_any_dispatch(string method,string url)
    {
        var h=new Handler();using var http=new HttpClient(new CarrierManualReadOnlyHttpGuard(SapOrigin,GuardianOrigin,h));using var request=new HttpRequestMessage(new HttpMethod(method),url);
        await Assert.ThrowsAsync<CarrierGatewayException>(()=>http.SendAsync(request));Assert.Empty(h.Calls);
    }
    [Fact] public async Task GUARDIAN_signature_scope_mismatch_does_not_dispatch_or_treat_404_globally()
    {
        var h=new Handler{GuardianStatus=HttpStatusCode.NotFound};using var http=new HttpClient(h);var o=new GuardianHttpOptions{BaseUrl=GuardianOrigin,Timeout=TimeSpan.FromSeconds(2)};
        var adapter=new GuardianCarrierManualReadOnlyAdapter(http,o,new GuardianTokenClient(http,o,new Credentials()),Contract());
        var result=await adapter.ConsultarCodigoAsync(Identity() with{IdAssinatura="OTHER_SIGNATURE"},default);Assert.Equal(CarrierLookupState.Failed,result.State);Assert.Empty(h.Calls);
    }
    [Fact] public async Task PUT_COLLECTION_RESOLUTION_preserves_R13_patch_executed_with_fake_HTTP_only()
    {
        var h=new Handler{GuardianBody="{}"};using var http=new HttpClient(h);var o=new GuardianHttpOptions{BaseUrl=GuardianOrigin,Timeout=TimeSpan.FromSeconds(2)};
        var contract=new GuardianCarrierHmlContract(true,true,"OFFLINE_R13_APPROVAL","/lookup/{bp}","/create","/api/v0/Guardian/Transportadora","/readback/{code}",404,
            _=>new(CarrierLookupState.Failed),a=>new{codigo=a.GuardianCode,descricao=a.Payload.Name,razaoSocial=a.Payload.LegalName},_=>null);
        var adapter=new GuardianCarrierSyncAdapter(http,o,new GuardianTokenClient(http,o,new Credentials()),contract,_=>true);
        var result=await adapter.GravarAsync(new(Guid.NewGuid(),"0000000042","GUARDIAN42","UPDATE",new("0000000042","Nome","Razao")),default);
        Assert.True(result.Accepted);var put=Assert.Single(h.Calls,c=>c.Method=="PUT");Assert.Equal("/api/v0/Guardian/Transportadora",put.Uri.AbsolutePath);Assert.DoesNotContain("GUARDIAN42",put.Uri.AbsolutePath);
    }
}
