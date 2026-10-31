using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.HostIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

public sealed class NormalCompositionTests
{
    internal static ServiceCollection Compose(bool normal)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMotorIntegracaoRepository, RepositorioMotorIntegracaoFake>();
        services.AddSingleton(TimeProvider.System);
        services.AddMvpOrchestration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AcmR57:Enabled"]="true", ["AcmR57:Normal:Enabled"]=normal.ToString() }).Build());
        return services;
    }

    [Fact]
    public void Approved_normal_composition_registers_generic_gate_without_one_shot()
    {
        using var sp = Compose(true).BuildServiceProvider();
        Assert.Equal("AcmR57NormalRuntimeGate", sp.GetRequiredService<IAcmR57RuntimeGate>().GetType().Name);
    }

    [Fact]
    public void Approved_normal_composition_has_real_per_stage_preparation()
    {
        using var sp = Compose(true).BuildServiceProvider();
        Assert.Equal("AcmR57NormalPreparation", sp.GetRequiredService<IAcmR57Preparacao>().GetType().Name);
    }

    [Fact]
    public void Wiring_never_activates_workers_and_legacy_default_is_preserved()
    {
        var services = Compose(true);
        Assert.DoesNotContain(services, x => x.ServiceType == typeof(IHostedService));
        using var sp = Compose(false).BuildServiceProvider();
        Assert.IsType<AcmR57RuntimeDisabled>(sp.GetRequiredService<IAcmR57RuntimeGate>());
        Assert.IsType<AcmR57PreparacaoPendente>(sp.GetRequiredService<IAcmR57Preparacao>());
    }
}
