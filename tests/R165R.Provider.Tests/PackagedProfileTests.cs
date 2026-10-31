using FugaTES.HostIntegracao;
using FugaTES.Persistencia.MotorIntegracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

public sealed class PackagedProfileTests
{
    private static string ReleaseRoot => Environment.GetEnvironmentVariable("R165R_PACKAGE_ROOT")
        ?? throw new InvalidOperationException("Set R165R_PACKAGE_ROOT to the isolated, disabled release candidate.");

    [Fact]
    public void Actual_host_and_callback_profiles_are_identical_and_closed_by_default()
    {
        var hostPath = Path.Combine(ReleaseRoot, "runtime", "host", "appsettings.json");
        var callbackPath = Path.Combine(ReleaseRoot, "runtime", "callback", "appsettings.json");
        Assert.Equal(File.ReadAllBytes(hostPath), File.ReadAllBytes(callbackPath));
        var config = new ConfigurationBuilder().AddJsonFile(hostPath, optional: false).Build();
        Assert.False(config.GetValue<bool>("FUGATES_INTEGRATION_WORKER_ENABLED"));
        Assert.False(config.GetValue<bool>("Sap:Enabled"));
        Assert.False(config.GetValue<bool>("SapContractInbound:ProductiveEnabled"));
        Assert.False(config.GetValue<bool>("Callback:Enabled"));
        Assert.Equal("R165R_CREDENTIAL_SECURITY_APPROVAL_REQUIRED", AcmProviderReadOnlyEntrypoint.ConfigurationBlock(config));
    }

    [Fact]
    public void Packaged_profile_supplies_real_composition_timings_without_host_start_or_connection()
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddJsonFile(Path.Combine(ReleaseRoot, "runtime", "host", "appsettings.json"), optional: false);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HostIntegracao:PersistenceMode"] = "PostgreSql", ["HostIntegracao:ExternalMode"] = "Fake",
            ["AcmR57:Enabled"] = "true", ["AcmR104:ExecutionAuthorized"] = "false",
            ["AcmR165R:CredentialSecurityCleared"] = "true", ["AcmR165R:ApprovalReference"] = "SYNTHETIC-TEST-NOT-AN-AUTHORIZATION",
            ["ConnectionStrings:FugaTES"] = "Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_acm_hml_app;Search Path=fugates;Enlist=false;Options=-c default_transaction_read_only=on;Passfile=/synthetic-test-not-accessed/pgpass"
        });
        Assert.Null(AcmProviderReadOnlyEntrypoint.ConfigurationBlock(builder.Configuration));
        AcmProviderReadOnlyEntrypoint.Configure(builder);
        Assert.DoesNotContain(builder.Services, x => x.ServiceType == typeof(IHostedService));
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        Assert.IsType<RepositorioMotorIntegracaoPostgreSql>(scope.ServiceProvider.GetRequiredService<RepositorioMotorIntegracaoPostgreSql>());
        Assert.IsAssignableFrom<NpgsqlDataSource>(scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>());
        // No OpenConnection, repository method, gateway or Host.Start/Run is invoked.
    }
}
