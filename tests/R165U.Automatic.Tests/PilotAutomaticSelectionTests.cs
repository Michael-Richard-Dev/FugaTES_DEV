using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class PilotAutomaticSelectionTests
{
    [Fact]
    public async Task Scoped_worker_processes_only_selected_contract_and_leaves_backlog_untouched()
    {
        var f = new AutomaticFixture();
        var older = f.Add("1147").Version;
        var pilot = f.Add("1155").Version;
        var repository = new RepositorioMotorIntegracaoFake();
        Seed(repository, older);
        Seed(repository, pilot);
        var processor = new RecordingProcessor();
        var detector = new RecordingDetector();
        var motor = Motor(repository, processor, detector, f.Versions);

        Assert.Equal(CicloMotorIntegracaoResultado.Processado, await motor.ExecutarUmCicloAsync(default));
        Assert.Equal(pilot.IntegracaoId, Assert.Single(processor.Claims).IntegracaoId);
        Assert.Equal(CicloMotorIntegracaoResultado.NenhumTrabalho, await motor.ExecutarUmCicloAsync(default));
        Assert.Equal(EstadoIntegracao.Pendente, (await repository.ObterPorIdAsync(older.IntegracaoId, default))!.Estado);
        Assert.Equal(0, detector.Calls);
    }

    [Fact]
    public async Task Scoped_worker_requires_matching_durable_H4_context_before_claim()
    {
        var f = new AutomaticFixture();
        var wrongCentre = f.Add("1155", s => s with { Centro = "9999" }).Version;
        var repository = new RepositorioMotorIntegracaoFake();
        Seed(repository, wrongCentre);
        var processor = new RecordingProcessor();
        var motor = Motor(repository, processor, new RecordingDetector(), f.Versions);

        Assert.Equal(CicloMotorIntegracaoResultado.NenhumTrabalho, await motor.ExecutarUmCicloAsync(default));
        Assert.Empty(processor.Claims);
        Assert.Empty(repository.Tentativas);
    }

    [Fact]
    public void Partial_pilot_scope_is_invalid_instead_of_enabling_the_whole_queue()
    {
        var options = Configuration(new Dictionary<string, string?>
        {
            ["AcmPilotNumeroContrato"] = "1155"
        }).Get<MotorIntegracaoOptions>()!;
        Assert.False(options.EhValida());
    }

    internal static MotorIntegracaoServico Motor(RepositorioMotorIntegracaoFake repository,
        IProcessadorExternoIntegracao processor, IDetectorCandidatosIntegracao detector,
        IContratoSapToledoVersaoStore versions, string number = "1155")
    {
        var options = Configuration(new Dictionary<string, string?>
        {
            ["AcmPilotNumeroContrato"] = number,
            ["AcmPilotCentro"] = "3011",
            ["AcmPilotItem"] = "10"
        }).Get<MotorIntegracaoOptions>()!;
        var services = new ServiceCollection()
            .AddSingleton<IMotorIntegracaoRepository>(repository)
            .AddSingleton(processor)
            .AddSingleton(detector)
            .AddSingleton<IContratoSapToledoVersaoStore>(versions)
            .AddSingleton<IValidadorIntegracao, ValidadorGenericoIntegracao>()
            .AddSingleton<IIdentidadeWorkerProvider, Identity>()
            .AddSingleton<IPoliticaTimeoutProcessamentoIntegracao, Timeout>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton(options)
            .BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<MotorIntegracaoServico>(services);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values)
    {
        values["LeaseDuration"] = "00:01:00";
        values["LeaseRenewInterval"] = "00:00:10";
        values["RetryBackoff"] = "00:01:00";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    internal static void Seed(RepositorioMotorIntegracaoFake repository,
        FugaTES.Aplicacao.Contratos.ContratosSapToledo.VersaoContratoSapToledo version)
        => repository.Semear(new(version.IntegracaoId, "SAP", "TOLEDO", "SAP_CONTRATO_TOLEDO",
            version.NumeroContrato, version.FingerprintSha256, "SYNTHETIC-" + version.IntegracaoId,
            EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, DateTimeOffset.UtcNow.AddMinutes(-1),
            0, null, null, FaseEnvioIntegracao.NaoIniciado, null));

    internal sealed class RecordingProcessor : IProcessadorExternoIntegracao
    {
        public List<ClaimIntegracaoMotor> Claims { get; } = [];
        public Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim, CancellationToken ct)
        {
            Claims.Add(claim);
            return Task.FromResult(ResultadoProcessamentoExterno.Sucesso);
        }
    }

    private sealed class RecordingDetector : IDetectorCandidatosIntegracao
    {
        public int Calls;
        public Task<IReadOnlyList<CandidatoIntegracao>> DetectarAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<CandidatoIntegracao>>([]);
        }
    }
    private sealed class Identity : IIdentidadeWorkerProvider
    {
        public IdentidadeWorkerIntegracao Obter() => new("SYNTHETIC_SCOPED_WORKER");
    }
    private sealed class Timeout : IPoliticaTimeoutProcessamentoIntegracao
    {
        public TimeSpan ObterTimeout(ClaimIntegracaoMotor claim) => TimeSpan.FromSeconds(5);
    }
}
