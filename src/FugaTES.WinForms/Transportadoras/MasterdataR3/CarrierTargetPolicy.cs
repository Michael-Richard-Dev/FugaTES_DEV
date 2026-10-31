using System.Globalization;
using FugaTES.Aplicacao.Transportadoras;
using Microsoft.Extensions.Configuration;

namespace FugaTES.WinForms.Transportadoras.MasterdataR3;

// Read authorization and CREATE authorization have separate lifetimes. No compiled target BP.
public static class CarrierTargetPolicy
{
    public static void ValidateRead(IConfiguration c,string bp,string sid,DateTimeOffset now)
    {
        if(!CarrierDesktopProfile.True(c,"Masterdata:Read:DynamicSapBpSelectionApproved")){CarrierManualReadOnlyProfile.Validate(c,bp,sid,now);return;}
        CarrierPersistencePolicy.ValidateBp(bp);
        bool Has(string key)=>!string.IsNullOrWhiteSpace(c[key])&&!c[key]!.Contains('<');
        if(c["Carrier:Environment"]!="HML"||!CarrierDesktopProfile.True(c,"Carrier:DesktopProfileApproved")||!Has("Carrier:ApprovalReference")||
            !Has("Masterdata:Read:ApprovalReference")||!CarrierDesktopProfile.True(c,"Masterdata:Read:GetScopeApproved")||!Has("Masterdata:Read:GetSignatureId")||
            string.IsNullOrWhiteSpace(sid)||!c.GetSection("Masterdata:Read:AllowedSids").GetChildren().Any(x=>x.Value==sid))
            throw new FugaTES.WinForms.Runtime.DesktopStartupException("MASTERDATA_READ_SCOPE_OR_SID_NOT_APPROVED");
        foreach(var key in new[]{"FUGATES_INTEGRATION_WORKER_ENABLED","INC005_WORKER_ENABLED","FUGATES_CARRIER_SYNC_ENABLED","FUGATES_CARRIER_WORKER_ENABLED","Carrier:IntegrationWorkerEnabled","Carrier:DesktopGuardianWritesEnabled","Carrier:Authorization:IncrementalReleased"})
            if(!bool.TryParse(c[key],out var enabled)||enabled||bool.TryParse(Environment.GetEnvironmentVariable(key),out var global)&&global)
                throw new FugaTES.WinForms.Runtime.DesktopStartupException("MASTERDATA_AUTOMATIC_ROUTES_MUST_BE_DISABLED");
        if(c["Sap:BaseUrl"]?.TrimEnd('/')!="https://vhfufds4ci.sap.fugacouros.com.br:44300/sap/opu/odata/sap/API_BUSINESS_PARTNER"||c["Sap:SapClient"]!="110"||c["Guardian:BaseUrl"]?.TrimEnd('/')!="https://guardian.cloudprix.com.br"||FugaTES.HostIntegracao.CarrierProtocolBinding.BuildGuardianManualContract(c) is null)
            throw new FugaTES.WinForms.Runtime.DesktopStartupException("MASTERDATA_READ_CONTRACT_NOT_PROVEN");
        foreach(var field in new[]{"SapUser","SapCredentialTarget","GuardianUser","GuardianCredentialTarget","GuardianSignatureTarget","GuardianSignatureUser"})
            if(!Has("Carrier:Secrets:"+field))throw new FugaTES.WinForms.Runtime.DesktopStartupException("MASTERDATA_CREDENTIAL_REFERENCE_MISSING");
    }
    public static CarrierManualIdentity ReadIdentity(IConfiguration c,string bp)
    {
        var known=CarrierManualReadOnlyProfile.Identity(c,bp);if(known is not null)return known;
        if(!CarrierDesktopProfile.True(c,"Masterdata:Read:DynamicSapBpSelectionApproved")||!CarrierDesktopProfile.True(c,"Masterdata:Read:GetScopeApproved")||string.IsNullOrWhiteSpace(c["Masterdata:Read:GetSignatureId"])||string.IsNullOrWhiteSpace(c["Masterdata:Read:ApprovalReference"]))throw new InvalidOperationException("MASTERDATA_IDENTITY_NOT_APPROVED");
        CarrierPersistencePolicy.ValidateBp(bp);
        // Generic BP-as-code is a lookup only. CanCreate still requires an explicit per-BP identity.
        return new(bp,c["Masterdata:Read:GetSignatureId"]!,true,c["Masterdata:Read:ApprovalReference"]!);
    }
    public static bool HistoryAllowsCreate(IReadOnlyList<FugaTES.Persistencia.Transportadoras.CarrierCreateHistoryEntry> rows)=>rows.Count==0;
    public static bool CanCreate(IConfiguration c,string bp,string code,string sid,DateTimeOffset now)
    {
        try
        {
            CarrierManualReadOnlyProfile.Validate(c,bp,sid,now);
            var p="Masterdata:Create:";var t=p+"Targets:"+bp+":";
            bool Yes(string key)=>CarrierDesktopProfile.True(c,key);
            string[] List(string key)=>c.GetSection(key).GetChildren().Select(x=>x.Value??"").ToArray();
            var identity=CarrierManualReadOnlyProfile.Identity(c,bp);
            if(!Yes(p+"Enabled")||!Yes(p+"SpecificCreateAuthorized")||!List(p+"AllowedBps").Contains(bp,StringComparer.Ordinal)||
                !List(p+"AllowedSids").Contains(sid,StringComparer.Ordinal)||identity?.Codigo!=code||
                !Yes(t+"HistoricalPostReconciled")||string.IsNullOrWhiteSpace(c[t+"HistoricalEvidenceReference"])||
                string.IsNullOrWhiteSpace(c[t+"ExecutionApprovalReference"])||c[t+"GuardianCode"]!=code||c[t+"Estado"]!="1"||
                !Yes(t+"CodeApproved")||!Yes(t+"EstadoApproved")||!Yes(t+"PayloadContractApproved")||
                !Yes(p+"CallbackApproved")||!Yes(p+"CallbackAuthenticationConfirmed"))return false;
            if(!DateTimeOffset.TryParse(c[p+"FromUtc"],CultureInfo.InvariantCulture,DateTimeStyles.None,out var from)||
                !DateTimeOffset.TryParse(c[p+"UntilUtc"],CultureInfo.InvariantCulture,DateTimeStyles.None,out var until)||
                from.Offset!=TimeSpan.Zero||until.Offset!=TimeSpan.Zero||until<=from||until-from>TimeSpan.FromMinutes(10)||now<from||now>=until)return false;
            if(!FugaTES.Integracao.Guardian.Transportadoras.MasterdataCallbackUrlPolicyR159B.IsAllowed(c[p+"CallbackUrl"],FugaTES.Integracao.Guardian.Transportadoras.MasterdataPrivateHttpBindingR159B.FromConfiguration(c))||
                !Uri.TryCreate(c[p+"CallbackUrl"],UriKind.Absolute,out var callback)||callback.Query.Length!=0)return false;
            return true;
        }
        catch{return false;}
    }
    public static bool SapUnblocked(System.Text.Json.JsonElement raw)=>
        raw.TryGetProperty("BusinessPartnerIsBlocked",out var v)&&v.ValueKind==System.Text.Json.JsonValueKind.False;
}
