using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Callback.Http;
using FugaTES.HostIntegracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

public sealed class PilotInboundCompositionTests
{
    [Fact]
    public void Productive_inbound_can_host_the_existing_scoped_motor_after_durable_acceptance()
    {
        var services = new ServiceCollection();
        Assert.True(SapContractInboundProductiveComposition.ConfigurarServicos(services, Config()));
        Assert.Contains(services, d => d.ServiceType == typeof(MotorIntegracaoServico));
        Assert.Contains(services, d => d.ServiceType == typeof(AcmR57Pipeline));
        Assert.Single(services, d => d.ServiceType == typeof(IContratoSapToledoVersaoStore));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(HospedagemIntegracaoWorker));
    }

    [Fact]
    public void Inert_candidate_keeps_HTTP_and_motor_composition_without_starting_worker()
    {
        var services = new ServiceCollection();
        SapContractInboundProductiveComposition.ConfigurarServicos(services,
            Config(new() { ["FUGATES_INTEGRATION_WORKER_ENABLED"] = "false" }));
        Assert.Contains(services, d => d.ServiceType == typeof(MotorIntegracaoServico));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Theory]
    [InlineData("MotorIntegracao:AcmPilotNumeroContrato", null)]
    [InlineData("MotorIntegracao:AcmPilotCentro", null)]
    [InlineData("HostIntegracao:PersistenceMode", "Fake")]
    [InlineData("HostIntegracao:ExternalMode", "Fake")]
    [InlineData("AcmR57:Normal:Enabled", "false")]
    [InlineData("SapContractInbound:AutomaticProcessingEnabled", "invalid")]
    public void Incomplete_automatic_binding_never_falls_back_to_an_unscoped_or_fake_motor(string key, string? value)
        => Assert.Throws<InvalidOperationException>(() =>
            SapContractInboundProductiveComposition.ConfigurarServicos(new ServiceCollection(), Config(new() { [key] = value })));

    internal static IConfiguration Config(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["SapContractInbound:ProductiveEnabled"] = "true",
            ["SapContractInbound:AutomaticProcessingEnabled"] = "true",
            ["SapContractInbound:SecurityMode"] = "LoopbackOnly",
            ["SapInbound:BasicAuth:Username"] = R165AGInboundAuthScopeTests.User,
            ["SapInbound:BasicAuth:Password"] = R165AGInboundAuthScopeTests.Password,
            ["ConnectionStrings:FugaTES"] = "Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_acm_hml_app;Search Path=fugates;Passfile=/synthetic/not-used/pgpass",
            ["HostIntegracao:PersistenceMode"] = "PostgreSql",
            ["HostIntegracao:ExternalMode"] = "Guardian",
            ["HostIntegracao:PollInterval"] = "00:00:01",
            ["HostIntegracao:ShutdownGracePeriod"] = "00:00:10",
            ["MotorIntegracao:LeaseDuration"] = "00:01:00",
            ["MotorIntegracao:LeaseRenewInterval"] = "00:00:10",
            ["MotorIntegracao:RetryBackoff"] = "00:01:00",
            ["MotorIntegracao:AcmPilotNumeroContrato"] = "0000001155",
            ["MotorIntegracao:AcmPilotCentro"] = "3011",
            ["TimeoutProcessamentoIntegracao:Timeout"] = "00:00:20",
            ["AcmR57:Enabled"] = "true",
            ["AcmR57:Normal:Enabled"] = "true",
            ["FUGATES_INTEGRATION_WORKER_ENABLED"] = "true"
        };
        values["Guardian:BaseUrl"] = "https://synthetic.example.invalid";
        values["Guardian:Timeout"] = "00:00:05";
        values["Guardian:Email"] = "synthetic@example.invalid";
        values["Guardian:Password"] = "SYNTHETIC_NOT_A_CREDENTIAL";
        values["Guardian:Signature"] = "SYNTHETIC_NOT_A_CREDENTIAL";
        if (overrides is not null) foreach (var entry in overrides) values[entry.Key] = entry.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
