using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.HostIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Infraestrutura.Recebimento;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class R165RAutomaticCompositionTests
{
    private static ServiceCollection Compose(bool enabled)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMotorIntegracaoRepository, RepositorioMotorIntegracaoFake>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IRepositorioCorrelacaoRecebimento, RepositorioCorrelacaoRecebimentoMemoria>();
        services.AddMvpOrchestration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["AcmR57:Enabled"] = enabled ? "true" : "false" }).Build());
        return services;
    }

    [Fact]
    public void Existing_callback_correlation_is_resolved_in_normal_acm_composition_without_one_shot()
    {
        using var provider = Compose(true).BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<AcmR104CallbackCorrelation>(scope.ServiceProvider.GetRequiredService<IAcmR104CallbackCorrelation>());
    }

    [Fact]
    public void Wiring_correction_does_not_approve_business_sources_or_runtime_writes()
    {
        using var provider = Compose(true).BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<AcmR57RuntimeDisabled>(scope.ServiceProvider.GetRequiredService<IAcmR57RuntimeGate>());
        Assert.IsType<AcmR57PreparacaoPendente>(scope.ServiceProvider.GetRequiredService<IAcmR57Preparacao>());
        Assert.DoesNotContain(Compose(true), x => x.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService));
    }

    [Fact]
    public void Disabled_r57_does_not_add_correlation_or_pipeline()
    {
        var services = Compose(false);
        Assert.DoesNotContain(services, x => x.ServiceType == typeof(IAcmR104CallbackCorrelation));
        Assert.DoesNotContain(services, x => x.ServiceType == typeof(AcmR57Pipeline));
    }
}
