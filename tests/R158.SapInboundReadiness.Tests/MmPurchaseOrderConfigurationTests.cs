using FugaTES.WinForms.Mm;
using Microsoft.Extensions.Configuration;
namespace FugaTES.Testes;
public sealed class MmPurchaseOrderConfigurationTests
{
    [Theory]
    [InlineData("Sap:BaseUrl","http://vhfufds4ci.sap.fugacouros.com.br:44300")]
    [InlineData("Sap:BaseUrl","https://attacker.invalid:44300")]
    [InlineData("Sap:BaseUrl","https://user@vhfufds4ci.sap.fugacouros.com.br:44300")]
    [InlineData("Sap:SapClient","100")]
    [InlineData("Carrier:Secrets:SapUser","")]
    [InlineData("Carrier:Secrets:SapCredentialTarget","")]
    public void Incompatible_profile_never_reaches_credential_resolution(string key,string value)
    {var config=Config();config[key]=value;Assert.ThrowsAny<Exception>(()=>MmPurchaseOrderComposition.Validate(config));}
    [Fact] public void Approved_existing_profile_references_are_accepted_without_resolving_a_password()=>MmPurchaseOrderComposition.Validate(Config());
    [Fact] public void Unexpected_headless_arguments_do_not_start_the_collector_or_read_a_credential()=>Assert.Equal(2,MmPurchaseOrderComposition.RunCollector(["--mm-unknown"]));
    [Fact] public void Old_snapshot_cannot_claim_connected_status()=>Assert.False(new MmPurchaseOrderSnapshot{State="MM_SAP_COMPLETE",LastSuccessUtc=DateTimeOffset.UtcNow.AddMinutes(-3)}.IsFresh(DateTimeOffset.UtcNow));
    [Fact] public void HTTP_failure_never_appears_connected_despite_a_recent_cached_success()=>Assert.False(new MmPurchaseOrderSnapshot{State="MM_SAP_AUTHENTICATION_REQUIRED",LastSuccessUtc=DateTimeOffset.UtcNow}.IsFresh(DateTimeOffset.UtcNow));
    private static IConfigurationRoot Config()=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Sap:BaseUrl"]="https://vhfufds4ci.sap.fugacouros.com.br:44300",["Sap:SapClient"]="110",["Carrier:Secrets:SapUser"]="TEST_ONLY",["Carrier:Secrets:SapCredentialTarget"]="TEST_ONLY_NO_SECRET"}).Build();
}
