using System.Globalization;
using System.Security.Principal;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.Guardian;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using FugaTES.WinForms.Runtime;
using Microsoft.Extensions.Configuration;
namespace FugaTES.WinForms.Transportadoras;

public static class CarrierManualReadOnlyProfile
{
    public static void Validate(IConfiguration c,string bp,string sid,DateTimeOffset now)
    {
        CarrierPersistencePolicy.ValidateBp(bp);
        if(c["Carrier:Environment"]!="HML"||!CarrierDesktopProfile.True(c,"Carrier:DesktopProfileApproved")||string.IsNullOrWhiteSpace(c["Carrier:ApprovalReference"])||
            !CarrierDesktopProfile.True(c,"Carrier:ManualReadOnly:Approved")||string.IsNullOrWhiteSpace(c["Carrier:ManualReadOnly:ApprovalReference"]))
            throw new DesktopStartupException("MANUAL_HML_PROFILE_NOT_APPROVED");
        foreach(var key in new[]{"FUGATES_INTEGRATION_WORKER_ENABLED","INC005_WORKER_ENABLED","Carrier:IntegrationWorkerEnabled","Carrier:DesktopGuardianWritesEnabled","Carrier:Authorization:IncrementalReleased"})
            if(!bool.TryParse(c[key],out var enabled)||enabled||bool.TryParse(Environment.GetEnvironmentVariable(key),out var env)&&env)
                throw new DesktopStartupException("MANUAL_READONLY_FLAGS_NOT_PROVEN_FALSE");
        var sap=SafeUri(c["Sap:BaseUrl"]);var guardian=SafeUri(c["Guardian:BaseUrl"]);
        if(sap.GetLeftPart(UriPartial.Authority)!="https://vhfufds4ci.sap.fugacouros.com.br:44300"||sap.AbsolutePath.TrimEnd('/')!="/sap/opu/odata/sap/API_BUSINESS_PARTNER"||c["Sap:SapClient"]!="110"||
            guardian.GetLeftPart(UriPartial.Authority)!="https://guardian.cloudprix.com.br")
            throw new DesktopStartupException("MANUAL_APPROVED_ORIGIN_MISMATCH");
        if(!CarrierDesktopProfile.True(c,"Carrier:ManualReadOnly:Sap:Proven")||string.IsNullOrWhiteSpace(c["Carrier:ManualReadOnly:Sap:ApprovalReference"])||
            SafeUri(c["Carrier:ManualReadOnly:Sap:Origin"]).GetLeftPart(UriPartial.Authority)!=sap.GetLeftPart(UriPartial.Authority))
            throw new DesktopStartupException("MANUAL_SAP_INDIVIDUAL_CONTRACT_NOT_PROVEN");
        if(CarrierProtocolBinding.BuildGuardianContract(c) is null)throw new DesktopStartupException("MANUAL_GUARDIAN_CONTRACT_NOT_PROVEN");
        foreach(var key in new[]{"SapUser","SapCredentialTarget","GuardianUser","GuardianCredentialTarget","GuardianSignatureTarget","GuardianSignatureUser"})
            if(string.IsNullOrWhiteSpace(c["Carrier:Secrets:"+key]))throw new DesktopStartupException("MANUAL_CREDENTIAL_REFERENCE_MISSING");
        if(!DateTimeOffset.TryParse(c["Carrier:Authorization:UntilUtc"],CultureInfo.InvariantCulture,DateTimeStyles.None,out var until)||now>=until||
            !Values(c,"Carrier:Authorization:AllowedBps").Contains(bp,StringComparer.Ordinal)||string.IsNullOrWhiteSpace(sid)||!Values(c,"Carrier:Authorization:AllowedSids").Contains(sid,StringComparer.Ordinal))
            throw new DesktopStartupException("MANUAL_BP_OR_SID_NOT_AUTHORIZED");
        if(!int.TryParse(c["Carrier:ManualReadOnly:TimeoutSeconds"],out var timeout)||timeout is <1 or >120)
            throw new DesktopStartupException("MANUAL_TIMEOUT_NOT_CONFIGURED");
        // No BuildSapContract call. Discovery/delta capabilities are not individual-read capabilities.
    }
    public static CarrierManualIdentity? Identity(IConfiguration c,string bp)
    {
        var p="Carrier:ManualReadOnly:Identities:"+bp+":";
        if(!CarrierDesktopProfile.True(c,p+"Proven")||string.IsNullOrWhiteSpace(c[p+"Code"])||string.IsNullOrWhiteSpace(c[p+"GetSignatureId"])||string.IsNullOrWhiteSpace(c[p+"ApprovalReference"]))return null;
        return new(c[p+"Code"]!,c[p+"GetSignatureId"]!,true,c[p+"ApprovalReference"]!);
    }
    static string[] Values(IConfiguration c,string key)=>c.GetSection(key).GetChildren().Select(n=>n.Value).Where(v=>v is not null).Cast<string>().ToArray();
    static Uri SafeUri(string? text)
    {
        if(!Uri.TryCreate(text,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0)throw new DesktopStartupException("MANUAL_URI_INVALID");
        return uri;
    }
}
public static class CarrierManualReadOnlyComposition
{
    static IConfiguration Load()=>CarrierDesktopProfile.Load(Path.Combine(AppContext.BaseDirectory,"FugaTES.carrier.json"));
    public static bool Available(string bp)
    {
        try{if(!DesktopRuntime.Connected)return false;var c=Load();CarrierManualReadOnlyProfile.Validate(c,bp,WindowsIdentity.GetCurrent().User?.Value??"",DateTimeOffset.UtcNow);return true;}
        catch{return false;}
    }
    public static async Task<ResultadoCarrierManual> ConsultarAsync(string bp,CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if(!DesktopRuntime.Connected)return ResultadoCarrierManual.Bloqueado(bp,"MANUAL_POSTGRESQL_RUNTIME_NOT_VALIDATED");
            var config=Load();var sid=WindowsIdentity.GetCurrent().User?.Value??"";
            CarrierManualReadOnlyProfile.Validate(config,bp,sid,DateTimeOffset.UtcNow);
            var sapUri=new Uri(config["Sap:BaseUrl"]!);var guardianUri=new Uri(config["Guardian:BaseUrl"]!);
            using var http=new HttpClient(new CarrierManualReadOnlyHttpGuard(sapUri,guardianUri,new HttpClientHandler{AllowAutoRedirect=false}));
            var timeout=TimeSpan.FromSeconds(int.Parse(config["Carrier:ManualReadOnly:TimeoutSeconds"]!));
            var sap=new CarrierManualSapReadOnlyAdapter(new ClienteHttpParceiroNegocioSap(http,new OpcoesSap{BaseUrl=sapUri,SapClient="110",Timeout=timeout},new CarrierServiceSapAuthentication(config)));
            var credentials=new CarrierServiceGuardianCredentials(config);
            var options=new GuardianHttpOptions{BaseUrl=guardianUri,Timeout=timeout};
            var tokens=new GuardianTokenClient(http,options,credentials);
            var protocol=config.GetSection("Carrier:Protocols:Guardian");
            var contract=new GuardianCarrierManualContract(CarrierDesktopProfile.True(protocol,"ProfileVerified"),CarrierDesktopProfile.True(protocol,"SignatureVerified"),
                protocol["ApprovalReference"]!,protocol["LookupRoute"]!,protocol["CodeField"]!,protocol["LegalNameField"]!,protocol["AbsenceHttpStatus"]=="404");
            var guardian=new GuardianCarrierManualReadOnlyAdapter(http,options,tokens,contract);
            var comparison=new CarrierManualComparison([new("BusinessPartnerFullName","razaoSocial",true)],false,"ARES_R15_DIRECT_LEGAL_NAME_ONLY");
            bool Authorized(string candidate){try{CarrierManualReadOnlyProfile.Validate(config,candidate,sid,DateTimeOffset.UtcNow);return true;}catch{return false;}}
            return await new ConsultarTransportadoraManualReadOnlyCasoDeUso(sap,guardian,Authorized,b=>CarrierManualReadOnlyProfile.Identity(config,b),comparison,TimeProvider.System).ExecutarAsync(bp,ct).ConfigureAwait(false);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(DesktopStartupException ex){return ResultadoCarrierManual.Bloqueado(bp,ex.Code);}
        catch{return ResultadoCarrierManual.Bloqueado(bp,"MANUAL_READONLY_STARTUP_FAILED");}
    }
}
// Defense in depth. Authentication CreateToken may POST, never a Guardian business resource.
public sealed class CarrierManualReadOnlyHttpGuard(Uri sapOrigin,Uri guardianOrigin,HttpMessageHandler inner):DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var uri=request.RequestUri??throw new CarrierGatewayException("MANUAL_HTTP_URI_INVALID");
        bool safe=uri.Scheme=="https"&&uri.UserInfo.Length==0&&uri.Fragment.Length==0;
        bool sap=uri.GetLeftPart(UriPartial.Authority)==sapOrigin.GetLeftPart(UriPartial.Authority)&&request.Method==HttpMethod.Get&&
            uri.AbsolutePath.StartsWith("/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner(",StringComparison.Ordinal);
        bool guardian=uri.GetLeftPart(UriPartial.Authority)==guardianOrigin.GetLeftPart(UriPartial.Authority)&&
            (request.Method==HttpMethod.Get&&uri.AbsolutePath.StartsWith("/api/v0/Guardian/Transportadora/",StringComparison.Ordinal)||
             request.Method==HttpMethod.Post&&uri.AbsolutePath=="/api/v2/CreateToken");
        if(!safe||!sap&&!guardian)throw new CarrierGatewayException("MANUAL_HTTP_WRITE_OR_ROUTE_FORBIDDEN");
        return base.SendAsync(request,ct);
    }
}
