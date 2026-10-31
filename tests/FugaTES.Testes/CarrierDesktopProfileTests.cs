using Microsoft.Extensions.Configuration;
using FugaTES.WinForms.Transportadoras;
using FugaTES.WinForms.Runtime;
namespace FugaTES.Testes;
public class CarrierDesktopProfileTests
{
    internal static IConfiguration Valid()=>Config(new());
    static IConfiguration Config(Dictionary<string,string?> changes)
    {
        var d=new Dictionary<string,string?>{{"Carrier:Environment","HML"},{"Carrier:DesktopProfileApproved","true"},{"Carrier:ApprovalReference","SYNTHETIC_HERA_APPROVAL"},{"Sap:BaseUrl","https://sap.invalid/"},{"Guardian:BaseUrl","https://guardian.invalid/"}};
        d["Carrier:ManualReadOnly:Identities:0000000042:Proven"]="true";
        d["Carrier:ManualReadOnly:Identities:0000000042:Code"]="G42";
        d["Carrier:ManualReadOnly:Identities:0000000042:ApprovalReference"]="SYNTHETIC_EXPLICIT_BINDING";
        foreach(var key in new[]{"SapUser","SapCredentialTarget","GuardianUser","GuardianCredentialTarget","GuardianSignatureTarget","GuardianSignatureUser"})d["Carrier:Secrets:"+key]="SYNTHETIC_AUTHORIZED_REFERENCE";
        var sap=new Dictionary<string,string>{{"Origin","https://sap.invalid/"},{"Proven","true"},{"IncludesBpChanges","true"},{"IncludesNewCrm010Assignments","true"},{"ApprovalReference","SYNTHETIC"},{"DeltaTemplate","/delta?lo={watermark}&hi={upperBound}"},{"InitialTemplate","/initial?hi={upperBound}"},{"RowsPath","value"},{"BusinessPartnerField","BusinessPartner"},{"NextLinkPath","next"},{"PageLimit","50"}};
        var guardian=new Dictionary<string,string>{{"Environment","HML"},{"ProfileVerified","true"},{"SignatureVerified","true"},{"ApprovalReference","SYNTHETIC"},{"LookupRoute","/lookup/{bp}"},{"CreateRoute","/create"},{"UpdateRoute","/update/{code}"},{"ReadbackRoute","/readback/{code}"},{"RowsPath","data"},{"BusinessPartnerField","BP"},{"CodeField","CODE"},{"NameField","NAME"},{"LegalNameField","LEGAL"},{"WriteCodeField","carrierCode"},{"WriteNameField","name"},{"WriteLegalNameField","legal"},{"ResponseCodePath","code"}};
        foreach(var (key,value) in sap)d["Carrier:Protocols:Sap:"+key]=value;foreach(var(key,value)in guardian)d["Carrier:Protocols:Guardian:"+key]=value;
        foreach(var(key,value)in changes)d[key]=value;return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }
    [Fact] public void Explicitly_approved_HML_profile_has_no_assumed_TEST_environment()=>CarrierDesktopProfile.Validate(Valid());
    [Theory][InlineData("Carrier:Environment","PRD")][InlineData("Carrier:Environment","TESTE")][InlineData("Carrier:DesktopProfileApproved","false")][InlineData("Carrier:Protocols:Guardian:ProfileVerified","false")][InlineData("Carrier:Protocols:Guardian:SignatureVerified","false")][InlineData("Carrier:Protocols:Guardian:Environment","PRD")][InlineData("Carrier:Secrets:GuardianCredentialTarget","")][InlineData("INC005_WORKER_ENABLED","true")][InlineData("Sap:BaseUrl","https://other.invalid/")]
    public void Unproved_or_wrong_profiles_block_before_credential_access(string key,string value)=>Assert.Throws<DesktopStartupException>(()=>CarrierDesktopProfile.Validate(Config(new(){{key,value}})));
    [Fact] public void Plaintext_password_is_rejected_and_never_reaches_diagnostics()
    {var path=Path.GetTempFileName();try{File.WriteAllText(path,"{\"Password\":\"SECRET_PAYLOAD\"}");var ex=Assert.Throws<DesktopStartupException>(()=>CarrierDesktopProfile.Load(path));Assert.Equal("CARRIER_DESKTOP_PLAINTEXT_SECRET_FORBIDDEN",ex.Code);Assert.DoesNotContain("SECRET_PAYLOAD",ex.ToString());}finally{File.Delete(path);}}
    [Fact] public void Missing_profile_does_not_fallback_to_existing_Windows_credentials()=>Assert.Equal("CARRIER_DESKTOP_HML_PROFILE_MISSING",Assert.Throws<DesktopStartupException>(()=>CarrierDesktopProfile.Load(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json"))).Code);
}
