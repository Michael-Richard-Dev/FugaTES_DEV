using FugaTES.HostIntegracao;
using Npgsql;
using System.Text.Json;

namespace FugaTES.Testes;
public sealed class CarrierServiceBootstrapTests
{
    private static CarrierServiceProfile Valid() => new("HML", false, 10, "S-1-5-21-test", new("127.0.0.1",15433,"synthetic_hml","synthetic_writer","fugates","FugaTES/Carrier/HML/PostgreSQL"),new(true,"192.168.17.40",22,"synthetic_service",@"C:\ExternalSecrets\service_key",@"C:\ExternalSecrets\known_hosts","127.0.0.1",5432));
    [Fact] public void Dedicated_hml_profile_is_valid() => Valid().Validar("S-1-5-21-test");
    [Theory]
    [InlineData("HML",true,15433,"CARRIER_INC005_FORBIDDEN")]
    [InlineData("PRD",false,15433,"CARRIER_SERVICE_ENVIRONMENT_INVALID")]
    [InlineData("HML",false,15432,"CARRIER_SERVICE_PORT_NOT_DEDICATED")]
    public void Unsafe_service_configuration_is_rejected(string environment,bool inc005,int port,string reason)
    {
        var profile=Valid() with { Environment=environment,IntegrationWorkerEnabled=inc005,Database=Valid().Database with {Port=port}};
        Assert.Equal(reason,Assert.Throws<CarrierServiceStartupException>(()=>profile.Validar("S-1-5-21-test")).Code);
    }
    [Fact] public async Task Bootstrap_without_profile_returns_typed_failure_before_connecting()
    {
        var error=await Assert.ThrowsAsync<CarrierServiceStartupException>(()=>CarrierServiceRuntimeBootstrap.StartAsync(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json"),CancellationToken.None));
        Assert.Equal("CARRIER_SERVICE_PROFILE_MISSING",error.Code);
    }
    [Fact] public void Disabled_ssh_cannot_depend_on_desktop_tunnel()
    {
        var profile=Valid() with {Ssh=Valid().Ssh with {Enabled=false}};
        Assert.Equal("CARRIER_SERVICE_SSH_REQUIRED",Assert.Throws<CarrierServiceStartupException>(()=>profile.Validar("S-1-5-21-test")).Code);
    }
    [Fact] public void Different_service_account_is_blocked() => Assert.Equal("CARRIER_SERVICE_ACCOUNT_MISMATCH",Assert.Throws<CarrierServiceStartupException>(()=>Valid().Validar("S-1-5-21-other")).Code);
    [Fact] public void Service_connection_is_write_capable_and_secrets_are_memory_only()
    {
        var profile=Valid(); var value=new NpgsqlConnectionStringBuilder(profile.ConnectionString("SYNTHETIC_SECRET"));
        Assert.Equal("-c default_transaction_read_only=off",value.Options);
        Assert.False(value.IncludeErrorDetail);
        Assert.DoesNotContain("SYNTHETIC_SECRET",JsonSerializer.Serialize(profile));
    }
    [Fact] public void Ssh_is_strict_hidden_noninteractive_and_dedicated()
    {
        var info=CarrierServiceRuntimeBootstrap.StartInfo(Valid(),"ssh.exe");
        Assert.False(info.UseShellExecute); Assert.True(info.CreateNoWindow);
        Assert.Contains("StrictHostKeyChecking=yes",info.ArgumentList); Assert.Contains("BatchMode=yes",info.ArgumentList);
        Assert.Contains("127.0.0.1:15433:127.0.0.1:5432",info.ArgumentList);
        Assert.DoesNotContain("15432",string.Join(" ",info.ArgumentList));
    }
    [Fact] public void Secret_field_in_profile_is_rejected()
    {
        var path=Path.GetTempFileName();
        try { File.WriteAllText(path,JsonSerializer.Serialize(Valid()).Replace("\"Environment\"","\"Password\":\"FORBIDDEN\",\"Environment\"")); Assert.Equal("CARRIER_SERVICE_PROFILE_INVALID",Assert.Throws<CarrierServiceStartupException>(()=>CarrierServiceProfile.Load(path)).Code); }
        finally {File.Delete(path);}
    }
    [Fact] public void Missing_external_profile_is_explicitly_blocked() => Assert.Equal("CARRIER_SERVICE_PROFILE_MISSING",Assert.Throws<CarrierServiceStartupException>(()=>CarrierServiceProfile.Load(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json"))).Code);
}
