using FugaTES.HostIntegracao;
using FugaTES.WinForms.Transportadoras;
using FugaTES.WinForms.Runtime;
using Microsoft.Extensions.Configuration;
namespace FugaTES.Testes;
public class CarrierManualReadOnlyProfileR15Tests
{
    static Dictionary<string,string?> Valid(bool includeIdentity=true)
    {
        var d=new Dictionary<string,string?>{
            ["Carrier:Environment"]="HML",["Carrier:DesktopProfileApproved"]="true",["Carrier:ApprovalReference"]="OFFLINE_PROFILE",
            ["Carrier:ManualReadOnly:Approved"]="true",["Carrier:ManualReadOnly:ApprovalReference"]="OFFLINE_READ_AUTH",["Carrier:ManualReadOnly:TimeoutSeconds"]="30",
            ["Sap:BaseUrl"]="https://vhfufds4ci.sap.fugacouros.com.br:44300/sap/opu/odata/sap/API_BUSINESS_PARTNER/",["Sap:SapClient"]="110",
            ["Guardian:BaseUrl"]="https://guardian.cloudprix.com.br/",["Carrier:ManualReadOnly:Sap:Proven"]="true",
            ["Carrier:ManualReadOnly:Sap:Origin"]="https://vhfufds4ci.sap.fugacouros.com.br:44300",["Carrier:ManualReadOnly:Sap:ApprovalReference"]="OFFLINE_INDIVIDUAL_ONLY",
            ["Carrier:Authorization:UntilUtc"]="2060-01-01T00:00:00Z",["Carrier:Authorization:AllowedBps:0"]="0000000042",["Carrier:Authorization:AllowedSids:0"]="TEST_SID"
        };
        if(includeIdentity)
        {
            var identity="Carrier:ManualReadOnly:Identities:0000000042:";
            d[identity+"Proven"]="true";d[identity+"Code"]="G42";
            d[identity+"GetSignatureId"]="SIG_GET42";d[identity+"ApprovalReference"]="OFFLINE_EXPLICIT_BINDING";
        }
        foreach(var key in new[]{"FUGATES_INTEGRATION_WORKER_ENABLED","INC005_WORKER_ENABLED","Carrier:IntegrationWorkerEnabled","Carrier:DesktopGuardianWritesEnabled","Carrier:Authorization:IncrementalReleased"})d[key]="false";
        foreach(var key in new[]{"SapUser","SapCredentialTarget","GuardianUser","GuardianCredentialTarget","GuardianSignatureTarget","GuardianSignatureUser"})d["Carrier:Secrets:"+key]="OFFLINE_REFERENCE";
        var p="Carrier:Protocols:Guardian:";
        foreach(var pair in new Dictionary<string,string?>{
            ["Environment"]="HML",["ProfileVerified"]="true",["SignatureVerified"]="true",["ApprovalReference"]="OFFLINE_GET_CONTRACT",
            ["LookupRoute"]="/api/v0/Guardian/Transportadora/{bp}?idAssinatura=SIG_GET42",["CreateRoute"]="/api/v0/Guardian/Transportadora",["UpdateRoute"]="/api/v0/Guardian/Transportadora",
            ["ReadbackRoute"]="/api/v0/Guardian/Transportadora/{code}",["RowsPath"]="$",["CodeField"]="codigo",["NameField"]="descricao",["LegalNameField"]="razaoSocial",
            ["WriteCodeField"]="codigo",["WriteNameField"]="descricao",["WriteLegalNameField"]="razaoSocial",["ResponseCodePath"]="codigo",["AbsenceHttpStatus"]="404"})d[p+pair.Key]=pair.Value;
        return d;
    }
    static IConfiguration Config(Dictionary<string,string?> d)=>new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    [Fact] public void Individual_profile_is_delta_independent_without_promoting_any_delta_flags()
    {
        var c=Config(Valid());Assert.Null(CarrierProtocolBinding.BuildSapContract(c));
        CarrierManualReadOnlyProfile.Validate(c,"0000000042","TEST_SID",DateTimeOffset.Parse("2026-10-11T12:00:00Z"));
        Assert.Null(CarrierProtocolBinding.BuildSapContract(c));
    }
    [Theory][InlineData("Carrier:Protocols:Guardian:ProfileVerified","false")][InlineData("Carrier:Protocols:Guardian:SignatureVerified","false")]
    [InlineData("Carrier:DesktopProfileApproved","false")][InlineData("Carrier:ManualReadOnly:Approved","false")][InlineData("Carrier:Authorization:UntilUtc","2020-01-01T00:00:00Z")]
    [InlineData("Carrier:DesktopGuardianWritesEnabled","true")][InlineData("Carrier:IntegrationWorkerEnabled","true")][InlineData("FUGATES_INTEGRATION_WORKER_ENABLED","true")][InlineData("INC005_WORKER_ENABLED","true")][InlineData("Carrier:Authorization:IncrementalReleased","true")]
    [InlineData("Carrier:Environment","PRD")][InlineData("Sap:SapClient","100")][InlineData("Sap:BaseUrl","http://vhfufds4ci.sap.fugacouros.com.br:44300/sap/opu/odata/sap/API_BUSINESS_PARTNER/")]
    [InlineData("Carrier:Secrets:GuardianSignatureTarget","")][InlineData("Carrier:ManualReadOnly:Sap:ApprovalReference","")]
    public void WRITE_GUARD_profiles_missing_approval_or_enabling_write_are_blocked(string key,string value)
    {
        var d=Valid();d[key]=value;Assert.Throws<DesktopStartupException>(()=>CarrierManualReadOnlyProfile.Validate(Config(d),"0000000042","TEST_SID",DateTimeOffset.Parse("2026-10-11T12:00:00Z")));
    }
    [Fact] public void Identity_resolver_does_not_assume_BP_equals_Guardian_code(){var c=Config(Valid(includeIdentity:false));Assert.Null(CarrierManualReadOnlyProfile.Identity(c,"0000000042"));}
}
