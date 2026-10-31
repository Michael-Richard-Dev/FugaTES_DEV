using FugaTES.Aplicacao.Transportadoras;
namespace FugaTES.Testes;
public class CarrierSyncPolicyTests
{
    private static CarrierSyncAuthorization Allowed => new("HML",true,true,DateTimeOffset.UtcNow.AddMinutes(5),["000001"],["S-1-5-21-1"]);
    [Fact] public void Controlled_hml_mass_authorized() => Assert.True(new CarrierSyncPolicy(Allowed,TimeProvider.System).PodeEscrever("000001"));
    [Fact] public void Authenticated_allowed_sid_can_request() => Assert.True(new CarrierSyncPolicy(Allowed,TimeProvider.System).PodeSolicitar("000001","S-1-5-21-1"));
    [Theory] [InlineData("PRD",true,true)] [InlineData("HML",false,true)] [InlineData("HML",true,false)]
    public void Profile_failclosed(string environment,bool profile,bool signature) => Assert.False(new CarrierSyncPolicy(Allowed with {Environment=environment,ProfileProven=profile,SignatureProven=signature},TimeProvider.System).PodeEscrever("000001"));
    [Fact] public void Expired_auth_blocks() => Assert.False(new CarrierSyncPolicy(Allowed with {UntilUtc=DateTimeOffset.UtcNow.AddSeconds(-1)},TimeProvider.System).PodeEscrever("000001"));
    [Fact] public void Other_bp_blocks() => Assert.False(new CarrierSyncPolicy(Allowed,TimeProvider.System).PodeEscrever("000002"));
    [Fact] public void Forged_sid_blocks() => Assert.False(new CarrierSyncPolicy(Allowed,TimeProvider.System).PodeSolicitar("000001","S-1-5-21-2"));
}