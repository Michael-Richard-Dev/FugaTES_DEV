using FugaTES.HostIntegracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

public sealed class InertStartupConfigurationTests
{
    private static Dictionary<string, string?> Configuration(string enabled) => new()
    {
        ["ConnectionStrings:FugaTES"] = "Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_acm_hml_app;Pooling=false",
        ["HostIntegracao:PersistenceMode"] = "Fake",
        ["HostIntegracao:ExternalMode"] = "Fake",
        ["HostIntegracao:PollInterval"] = "00:00:05",
        ["HostIntegracao:ShutdownGracePeriod"] = "00:00:10",
        ["MotorIntegracao:LeaseDuration"] = "00:01:00",
        ["MotorIntegracao:LeaseRenewInterval"] = "00:00:20",
        ["MotorIntegracao:RetryBackoff"] = "00:01:00",
        ["TimeoutProcessamentoIntegracao:Timeout"] = "00:00:10",
        ["FUGATES_INTEGRATION_WORKER_ENABLED"] = "false",
        ["AcmR57:Enabled"] = enabled,
        ["AcmR57:Normal:Enabled"] = "false",
        ["AcmR57:Normal:ExecutionAuthorized"] = "false",
        ["AcmR104:ExecutionAuthorized"] = "false",
        ["AcmR165R:CredentialSecurityCleared"] = "false",
        ["Sap:Enabled"] = "false"
    };

    [Fact]
    public void Original_unit_configuration_reproduces_the_observed_role_guard()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Configuration("false")).Build();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            HostIntegracaoComposition.ConfigurarServicos(new ServiceCollection(), configuration));
        Assert.Equal("R165R_ACM_ROLE_REQUIRES_R57_ENABLED", exception.Message);
    }

    [Fact]
    public async Task R57_registration_without_execution_allows_inert_host_start_and_stop()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            EnvironmentName = Environments.Production
        });
        builder.Configuration.AddInMemoryCollection(Configuration("true"));
        HostIntegracaoComposition.Configurar(builder);
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        using var host = builder.Build();
        Assert.Empty(host.Services.GetServices<IHostedService>());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StartAsync(cancellation.Token);
        await host.StopAsync(cancellation.Token);
    }
}
