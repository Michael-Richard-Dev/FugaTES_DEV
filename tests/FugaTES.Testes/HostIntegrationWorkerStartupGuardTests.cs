using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.HostIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FugaTES.Testes;

public sealed class HostIntegrationWorkerStartupGuardTests
{
    public static IEnumerable<object?[]> DisabledValues()
    {
        foreach (var mode in new[] { "Fake", "Guardian" })
            foreach (var value in new string?[] { null, "false", "FALSE", "", "invalid", "1", "yes" })
                yield return [mode, value];
    }

    [Theory]
    [MemberData(nameof(DisabledValues))]
    public async Task AusenteFalsoInvalidoNaoRegistraNemConsomeFila(string mode, string? value)
    {
        using var scenario = new Scenario(mode, value);
        var services = scenario.Host.Services.GetServices<IHostedService>().ToArray();
        Assert.DoesNotContain(services, s => s is HospedagemIntegracaoWorker);
        await scenario.Host.StartAsync();
        try
        {
            Assert.True(scenario.Independent.Started);
            Assert.Equal(0, scenario.Detector.Calls);
            Assert.Equal(0, scenario.Processor.Calls);
            Assert.Equal(EstadoIntegracao.Pendente, Assert.Single(scenario.Repository.Integracoes).Estado);
            Assert.Empty(scenario.Repository.Tentativas);
            Assert.Empty(scenario.Repository.Auditorias);
        }
        finally { await scenario.Host.StopAsync(); }
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    public async Task TrueRegistraWorkerEExecutaCicloComDependenciasSimuladas(string value)
    {
        using var scenario = new Scenario("Fake", value);
        Assert.Single(scenario.Host.Services.GetServices<IHostedService>().OfType<HospedagemIntegracaoWorker>());
        await scenario.Host.StartAsync();
        try
        {
            await scenario.Processor.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await scenario.Host.StopAsync(); }
        Assert.True(scenario.Detector.Calls > 0);
        Assert.Equal(1, scenario.Processor.Calls);
        Assert.Equal(EstadoIntegracao.Concluida, Assert.Single(scenario.Repository.Integracoes).Estado);
        Assert.Single(scenario.Repository.Tentativas);
    }

    private sealed class Scenario : IDisposable
    {
        public readonly RepositorioMotorIntegracaoFake Repository = new();
        public readonly Detector Detector = new();
        public readonly Processor Processor = new();
        public readonly IndependentService Independent = new();
        public readonly IHost Host;
        public Scenario(string mode, string? value)
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder([]);
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FUGATES_INTEGRATION_WORKER_ENABLED"] = value,
                ["HostIntegracao:PersistenceMode"] = mode == "Guardian" ? "PostgreSql" : "Fake",
                ["HostIntegracao:ExternalMode"] = mode,
                ["HostIntegracao:PollInterval"] = "00:00:30",
                ["HostIntegracao:ShutdownGracePeriod"] = "00:00:05",
                ["MotorIntegracao:LeaseDuration"] = "00:01:00",
                ["MotorIntegracao:LeaseRenewInterval"] = "00:00:20",
                ["MotorIntegracao:RetryBackoff"] = "00:05:00",
                ["TimeoutProcessamentoIntegracao:Timeout"] = "00:00:10",
                ["ConnectionStrings:FugaTES"] = "Host=example.invalid;Database=fugates;Username=offline",
                ["Guardian:BaseUrl"] = "https://guardian.invalid",
                ["Guardian:Timeout"] = "00:00:10",
                ["Guardian:Email"] = "explicitly-fake-fixture",
                ["Guardian:Password"] = "explicitly-fake-fixture",
                ["Guardian:Signature"] = "explicitly-fake-fixture",
                ["Sap:Enabled"] = "false"
            });
            HostIntegracaoComposition.Configurar(builder);
            builder.Services.Replace(ServiceDescriptor.Singleton<IMotorIntegracaoRepository>(Repository));
            builder.Services.Replace(ServiceDescriptor.Singleton<IDetectorCandidatosIntegracao>(Detector));
            builder.Services.Replace(ServiceDescriptor.Singleton<IProcessadorExternoIntegracao>(Processor));
            builder.Services.AddSingleton<IHostedService>(Independent);
            Repository.Semear(new(Guid.NewGuid(), "SAP", "TOLEDO", "STARTUP_GUARD_TEST", "SYNTHETIC",
                new string('A', 64), "OFFLINE-STARTUP-GUARD", EstadoIntegracao.Pendente,
                TipoDisparoPendente.Automatico, DateTimeOffset.UtcNow.AddMinutes(-1), 0,
                null, null, FaseEnvioIntegracao.NaoIniciado, null));
            Host = builder.Build();
        }
        public void Dispose() => Host.Dispose();
    }

    private sealed class Detector : IDetectorCandidatosIntegracao
    {
        public int Calls;
        public Task<IReadOnlyList<CandidatoIntegracao>> DetectarAsync(CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult<IReadOnlyList<CandidatoIntegracao>>([]); }
    }

    private sealed class Processor : IProcessadorExternoIntegracao
    {
        public int Calls;
        public readonly TaskCompletionSource Invoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim, CancellationToken ct)
        { Interlocked.Increment(ref Calls); Invoked.TrySetResult(); return Task.FromResult(ResultadoProcessamentoExterno.Sucesso); }
    }

    private sealed class IndependentService : IHostedService
    {
        public bool Started;
        public Task StartAsync(CancellationToken ct) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
