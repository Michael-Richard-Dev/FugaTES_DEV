using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.HostIntegracao;
using FugaTES.Persistencia.MotorIntegracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace R165R.Provider.Tests;

public class ProviderConfigurationTests
{
    // Deliberately nonexistent, synthetic fixture. Never an operational credential path.
    public const string Connection = "Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_acm_hml_app;Search Path=fugates;Enlist=false;Options=-c default_transaction_read_only=on;Passfile=/SYNTHETIC-NOT-ACCESSED/passfile";

    public static Dictionary<string, string?> Profile() => new()
    {
        ["HostIntegracao:PersistenceMode"] = "PostgreSql",
        ["HostIntegracao:ExternalMode"] = "Fake",
        ["HostIntegracao:PollInterval"] = "00:00:30",
        ["HostIntegracao:ShutdownGracePeriod"] = "00:00:10",
        ["MotorIntegracao:LeaseDuration"] = "00:01:00",
        ["MotorIntegracao:LeaseRenewInterval"] = "00:00:20",
        ["MotorIntegracao:RetryBackoff"] = "00:01:00",
        ["TimeoutProcessamentoIntegracao:Timeout"] = "00:00:10",
        ["AcmR57:Enabled"] = "true",
        ["AcmR165R:CredentialSecurityCleared"] = "true",
        ["AcmR165R:ApprovalReference"] = "SYNTHETIC-OFFLINE-APPROVAL",
        ["FUGATES_INTEGRATION_WORKER_ENABLED"] = "false",
        ["INC005_WORKER_ENABLED"] = "false",
        ["FUGATES_CARRIER_SYNC_ENABLED"] = "false",
        ["CARRIER_WORKER_ENABLED"] = "false",
        ["Sap:Enabled"] = "false",
        ["AcmR104:ExecutionAuthorized"] = "false",
        ["ConnectionStrings:FugaTES"] = Connection
    };

    public static IConfiguration Config(Dictionary<string, string?> profile) =>
        new ConfigurationBuilder().AddInMemoryCollection(profile).Build();

    [Fact]
    public void Explicit_inert_profile_is_accepted_without_any_io() =>
        Assert.Null(AcmProviderReadOnlyEntrypoint.ConfigurationBlock(Config(Profile())));

    [Theory]
    [InlineData("AcmR165R:CredentialSecurityCleared", null)]
    [InlineData("AcmR165R:CredentialSecurityCleared", "false")]
    [InlineData("AcmR165R:CredentialSecurityCleared", "invalid")]
    [InlineData("AcmR165R:ApprovalReference", null)]
    [InlineData("AcmR165R:ApprovalReference", " ")]
    [InlineData("AcmR165R:ApprovalReference", "unsafe\nreference")]
    public void Credential_approval_is_blocked_by_default(string key, string? value)
    {
        var p = Profile(); p[key] = value;
        // Keep the old role here so RED detects the missing approval gate itself.
        p["ConnectionStrings:FugaTES"] = Connection.Replace("fugates_acm_hml_app", "fugates_dev_app");
        Assert.Equal("R165R_CREDENTIAL_SECURITY_APPROVAL_REQUIRED", AcmProviderReadOnlyEntrypoint.ConfigurationBlock(Config(p)));
    }

    public static IEnumerable<object?[]> ForbiddenFlags =>
        new[] { "FUGATES_INTEGRATION_WORKER_ENABLED", "INC005_WORKER_ENABLED", "FUGATES_CARRIER_SYNC_ENABLED", "CARRIER_WORKER_ENABLED", "Sap:Enabled", "AcmR104:ExecutionAuthorized" }
            .SelectMany(key => new string?[] { null, "true", "invalid" }.Select(value => new object?[] { key, value }));

    [Theory, MemberData(nameof(ForbiddenFlags))]
    public void Execution_flags_must_be_explicitly_false(string key, string? value)
    {
        var p = Profile(); p[key] = value;
        Assert.NotNull(AcmProviderReadOnlyEntrypoint.ConfigurationBlock(Config(p)));
    }

    [Theory]
    [InlineData("HostIntegracao:Mode", "CarrierSync")]
    [InlineData("HostIntegracao:PersistenceMode", "Fake")]
    [InlineData("HostIntegracao:ExternalMode", "Guardian")]
    [InlineData("AcmR57:Enabled", "false")]
    [InlineData("AcmR57:Enabled", null)]
    public void Provider_test_cannot_become_a_business_profile(string key, string? value)
    {
        var p = Profile(); p[key] = value;
        Assert.NotNull(AcmProviderReadOnlyEntrypoint.ConfigurationBlock(Config(p)));
    }

    public static IEnumerable<object?[]> InvalidConnections => new object?[][]
    {
        new object?[] { "Username", "fugates_dev_app" },
        new object?[] { "Username", "postgres" },
        new object?[] { "Port", 5432 }, new object?[] { "Port", 5434 },
        new object?[] { "Host", "remote.invalid" },
        new object?[] { "Database", "other" }, new object?[] { "Search Path", "public" },
        new object?[] { "Passfile", null }, new object?[] { "Passfile", " " },
        new object?[] { "Passfile", "/home/michael/.fugates-r116-dev/pgpass" },
        new object?[] { "Password", "SYNTHETIC-REJECTED-NOT-A-CREDENTIAL" },
        new object?[] { "Password", " " }, new object?[] { "Enlist", true },
        new object?[] { "Options", "-c default_transaction_read_only=off" }
    };

    [Theory, MemberData(nameof(InvalidConnections))]
    public void Unsafe_connection_is_rejected_before_io(string key, object? value)
    {
        var p = Profile(); var c = new NpgsqlConnectionStringBuilder(Connection); c[key] = value;
        p["ConnectionStrings:FugaTES"] = c.ConnectionString;
        Assert.NotNull(AcmProviderReadOnlyEntrypoint.ConfigurationBlock(Config(p)));
    }

    [Fact]
    public void Legacy_pgpassfile_cannot_supply_an_implicit_fallback()
    {
        var p = Profile(); var c = new NpgsqlConnectionStringBuilder(Connection); c.Remove("Passfile");
        p["ConnectionStrings:FugaTES"] = c.ConnectionString;
        p["PGPASSFILE"] = "/home/michael/.fugates-r116-dev/pgpass";
        Assert.NotNull(AcmProviderReadOnlyEntrypoint.ConfigurationBlock(Config(p)));
    }

    [Theory]
    [InlineData("PGPASSWORD", "SYNTHETIC-REJECTED-NOT-A-CREDENTIAL")]
    [InlineData("PGPASSFILE", "/home/michael/.fugates-r116-dev/pgpass")]
    public void Ambient_credential_configuration_cannot_override_the_approved_profile(string key, string value)
    {
        var p = Profile(); p[key] = value;
        Assert.NotNull(AcmProviderReadOnlyEntrypoint.ConfigurationBlock(Config(p)));
    }

    [Fact]
    public void Configure_resolves_real_provider_and_repository_but_no_hosted_services()
    {
        var b = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        b.Configuration.AddInMemoryCollection(Profile());
        AcmProviderReadOnlyEntrypoint.Configure(b);
        Assert.DoesNotContain(b.Services, x => x.ServiceType == typeof(IHostedService));
        using var host = b.Build(); using var scope = host.Services.CreateScope();
        Assert.IsAssignableFrom<NpgsqlDataSource>(scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>());
        FactoryTests.AssertPhysicalInitializer(scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>());
        Assert.IsType<RepositorioMotorIntegracaoPostgreSql>(scope.ServiceProvider.GetRequiredService<IMotorIntegracaoRepository>());
        Assert.False(scope.ServiceProvider.GetRequiredService<AcmR104OperationalProfile>().ExecutionAuthorized);
        Assert.IsType<FugaTES.Infraestrutura.MotorIntegracao.ProcessadorExternoIntegracaoFake>(
            scope.ServiceProvider.GetRequiredService<FugaTES.Aplicacao.Contratos.MotorIntegracao.IProcessadorExternoIntegracao>());
    }

    [Theory]
    [InlineData(null)] [InlineData("false")]
    public void Host_rejects_new_role_without_explicit_R57(string? enabled)
    {
        var p = Profile(); p["AcmR57:Enabled"] = enabled;
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => HostIntegracaoComposition.ConfigurarServicos(services, Config(p)));
    }

    [Fact]
    public void Host_ACM_flag_cannot_resolve_legacy_role()
    {
        var p = Profile(); p["ConnectionStrings:FugaTES"] = Connection.Replace("fugates_acm_hml_app", "fugates_dev_app");
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
        {
            HostIntegracaoComposition.ConfigurarServicos(services, Config(p));
            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<NpgsqlDataSource>();
        });
    }

    [Theory]
    [InlineData("Fake", null, false)] [InlineData("Fake", "false", false)]
    [InlineData("Fake", "true", true)] [InlineData("Guardian", "false", true)]
    [InlineData("Guardian", null, true)]
    public void R161L_identity_binding_is_preserved(string mode, string? enabled, bool expected)
    {
        var p = Profile(); p["HostIntegracao:ExternalMode"] = mode; p["AcmR57:Enabled"] = enabled;
        Assert.Equal(expected, HostIntegracaoComposition.RequiresAcmPhysicalIdentity(Config(p)));
    }

    [Fact]
    public async Task Readonly_entrypoint_default_configuration_is_blocked_before_host_build()
    {
        var b = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        Assert.Equal(78, await AcmProviderReadOnlyEntrypoint.RunAsync(new[] { AcmProviderReadOnlyEntrypoint.Argument }, b));
        Assert.DoesNotContain(b.Services, x => x.ServiceType == typeof(NpgsqlDataSource));
    }

    [Theory]
    [InlineData("--acm-provider-readonly=R165R")]
    [InlineData("--acm-provider-readonly")]
    public async Task Readonly_argument_scope_is_exact_and_never_falls_through_to_business(string argument)
    {
        var b = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        Assert.Equal(64, await AcmProviderReadOnlyEntrypoint.RunAsync(new[] { argument }, b));
    }
}
