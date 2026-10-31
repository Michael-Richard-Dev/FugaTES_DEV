using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Callback.Http;
using FugaTES.HostIntegracao;
using FugaTES.Persistencia.MotorIntegracao;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

public sealed class R165AGCompositionTests
{
    [Fact]
    public void Packaged_inert_R2_profile_selects_its_declared_ACM_reference_in_DI_without_opening_DB()
    {
        var profile = Path.Combine(AppContext.BaseDirectory, "configuration", "appsettings.inert.json");
        Assert.True(File.Exists(profile));
        var inert = new ConfigurationBuilder().AddJsonFile(profile).Build();
        Assert.False(inert.GetValue<bool>("SapContractInbound:ProductiveEnabled"));
        Assert.False(inert.GetValue<bool>("FUGATES_INTEGRATION_WORKER_ENABLED"));
        Assert.False(inert.GetValue<bool>("MotorIntegracao:AcmInboundWindow:Enabled"));
        var window = R165AGWindowSelectionTests.Window();
        var config = new ConfigurationBuilder().AddConfiguration(inert)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SapContractInbound:ProductiveEnabled"] = "true",
                ["SapInbound:BasicAuth:Username"] = R165AGInboundAuthScopeTests.User,
                ["SapInbound:BasicAuth:Password"] = R165AGInboundAuthScopeTests.Password,
                ["FUGATES_INTEGRATION_WORKER_ENABLED"] = "true",
                ["MotorIntegracao:AcmInboundWindow:Enabled"] = "true",
                ["MotorIntegracao:AcmInboundWindow:WindowId"] = window.WindowId,
                ["MotorIntegracao:AcmInboundWindow:AuthorizationReference"] = window.AuthorizationReference,
                ["MotorIntegracao:AcmInboundWindow:NotBeforeUtc"] = window.NotBeforeUtc!.Value.ToString("O"),
                ["MotorIntegracao:AcmInboundWindow:UntilUtc"] = window.UntilUtc!.Value.ToString("O"),
                ["MotorIntegracao:AcmInboundWindow:AllowedSapPartners:0"] = "0020000051",
                ["MotorIntegracao:AcmInboundWindow:AllowedSapMaterials:0"] = "7000000",
                ["Guardian:Email"] = "synthetic@example.invalid",
                ["Guardian:Password"] = "SYNTHETIC_NOT_A_CREDENTIAL",
                ["Guardian:Signature"] = "SYNTHETIC_NOT_A_CREDENTIAL"
            }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        Assert.True(InboundHttpHostComposition.Configurar(services, config, false).SapProductive);
        Assert.False(GuardianProtocolCommandEndpoint.Configurar(services, config));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var selected = new NpgsqlConnectionStringBuilder(source.ConnectionString);
        Assert.Equal(5433, selected.Port);
        Assert.Equal("fugates_acm_hml_app", selected.Username);
        Assert.Equal("fugates_dev", selected.Database);
        Assert.Equal("/home/michael/.fugates-acm-r165p/pgpass", selected.Passfile);
        Assert.Equal(SslMode.Require, selected.SslMode);
        Assert.IsType<RepositorioMotorIntegracaoPostgreSql>(scope.ServiceProvider.GetRequiredService<IMotorIntegracaoRepository>());
    }

    [Fact]
    public void Full_automatic_HTTP_composition_selects_dedicated_5433_datasource_and_exactly_one_worker_without_opening_DB()
    {
        var config = R165AGInboundAuthScopeTests.Config();
        var services = new ServiceCollection();
        services.AddSingleton(config);
        var routes = InboundHttpHostComposition.Configurar(services, config, false);
        Assert.True(routes.SapProductive);
        Assert.False(routes.SapOffline);
        Assert.False(GuardianProtocolCommandEndpoint.Configurar(services, config));
        var worker = Assert.Single(services, x => x.ServiceType == typeof(IHostedService));
        Assert.Equal(typeof(HospedagemIntegracaoWorker), worker.ImplementationType);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var datasource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var metadata = new NpgsqlConnectionStringBuilder(datasource.ConnectionString);
        Assert.Equal(5433, metadata.Port);
        Assert.Equal("127.0.0.1", metadata.Host);
        Assert.Equal("fugates_dev", metadata.Database);
        Assert.Equal("fugates_acm_hml_app", metadata.Username);
        Assert.Equal("fugates", metadata.SearchPath);
        Assert.True(NpgsqlDataSourceFactory.HasExplicitAcmPassfile(metadata));
        Assert.IsType<RepositorioMotorIntegracaoPostgreSql>(scope.ServiceProvider.GetRequiredService<IMotorIntegracaoRepository>());
        Assert.Equal(TimeSpan.FromSeconds(2), scope.ServiceProvider.GetRequiredService<IOptions<HostIntegracaoOptions>>().Value.PollInterval);
        // Deliberately do not call OpenConnection or start the hosted service.
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Port=5432;Database=fugates_dev;Username=fugates_acm_hml_app;Search Path=fugates;Passfile=/synthetic/pgpass")]
    [InlineData("Host=127.0.0.1;Port=5434;Database=fugates_dev;Username=fugates_acm_hml_app;Search Path=fugates;Passfile=/synthetic/pgpass")]
    [InlineData("Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_dev_app;Search Path=fugates;Passfile=/synthetic/pgpass")]
    public void Wrong_cluster_or_old_role_is_rejected_by_ACM_factory_before_any_connection(string value)
        => Assert.Throws<InvalidOperationException>(() => NpgsqlDataSourceFactory.CriarAcm(value));

    [Fact]
    public void Manual_command_and_automatic_worker_cannot_be_composed_together()
    {
        var config = R165AGInboundAuthScopeTests.Config(new()
        {
            ["AcmProtocolCommand:Enabled"] = "true", ["AcmProtocolCommand:Environment"] = "HML",
            ["AcmProtocolCommand:Contract"] = R165AGInboundAuthScopeTests.Number,
            ["AcmProtocolCommand:IntegrationId"] = "11111111-2222-3333-4444-555555555555",
            ["AcmProtocolCommand:OperatorSid"] = "S-1-5-21-1-2-3-1001",
            ["AcmProtocolCommand:BearerSecret"] = "SYNTHETIC_TEST_ONLY_LONGER_THAN_32_CHARACTERS"
        });
        var services = new ServiceCollection();
        InboundHttpHostComposition.Configurar(services, config, false);
        var error = Assert.Throws<InvalidOperationException>(() => GuardianProtocolCommandEndpoint.Configurar(services, config));
        Assert.Equal("ACM_PROTOCOL_COMMAND_HML_BINDING_REQUIRED", error.Message);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("invalid")]
    public void Enabled_manual_command_cannot_coexist_with_window_even_when_worker_is_disabled(string enabled)
    {
        var config = R165AGInboundWindowTests.Config(new()
        {
            ["MotorIntegracao:AcmInboundWindow:Enabled"] = enabled,
            ["FUGATES_INTEGRATION_WORKER_ENABLED"] = "false",
            ["AcmR57:Normal:GuardianEnvironment"] = "HML",
            ["AcmProtocolCommand:Enabled"] = "true", ["AcmProtocolCommand:Environment"] = "HML",
            ["AcmProtocolCommand:Contract"] = R165AGInboundAuthScopeTests.Number,
            ["AcmProtocolCommand:IntegrationId"] = "11111111-2222-3333-4444-555555555555",
            ["AcmProtocolCommand:OperatorSid"] = "S-1-5-21-1-2-3-1001",
            ["AcmProtocolCommand:BearerSecret"] = "SYNTHETIC_TEST_ONLY_LONGER_THAN_32_CHARACTERS"
        });
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => GuardianProtocolCommandEndpoint.Configurar(services, config));
        Assert.Equal("ACM_PROTOCOL_COMMAND_HML_BINDING_REQUIRED", error.Message);
        Assert.Empty(services);
    }
}
