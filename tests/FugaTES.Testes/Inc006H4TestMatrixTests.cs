using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Callback.Http;
using FugaTES.Infraestrutura.ContratosSapToledo;
using FugaTES.Infraestrutura.MotorIntegracao;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FugaTES.Testes;

public sealed class Inc006H4TestMatrixTests
{
    [Fact]
    public async Task ReplayIndeterminado_PreservaVersaoIntegracaoESemEfeitoExterno()
    {
        var fixture = new Fixture();
        var original = await fixture.RegisterAsync(Snapshot("A"));
        await fixture.SetStateAsync(original.IntegracaoId, EstadoIntegracao.ResultadoIndeterminado);
        var before = fixture.Inner.Integracoes.Count;

        var result = await fixture.UseCase.ExecutarAsync(Snapshot("A"), "replay", CancellationToken.None);
        var replay = await fixture.RegisterAsync(Snapshot("A"));

        Assert.Equal(ResultadoContratoSapToledo.ResultadoIndeterminado, result);
        Assert.Equal(ResultadoRegistroVersaoContrato.JaExistente, replay.Resultado);
        Assert.Equal(original.Versao, replay.Versao);
        Assert.Equal(original.IntegracaoId, replay.IntegracaoId);
        Assert.Equal(before, fixture.Inner.Integracoes.Count);
        Assert.Empty(fixture.Events);

        using var services = HttpServices(fixture);
        var context = new DefaultHttpContext { RequestServices = services, Response = { Body = new MemoryStream() } };
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("test:test"));
        context.Request.ContentType = "application/json";
        var item = new SapContractInboundItemHttpDto("Toledo", "0000001008", "AC3P", "Descricao",
            "11/09/2026", "11/09/2026", "31/12/2026", 10, "31/12/2026", "T", 100000m, 10000m,
            "0020000051", "000000000007000001", "E", "0000000042", "001", "001", "A", 1.5m,
            "3011", "10", []);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new SapContractInboundHttpDto([item]))));
        // Endpoint mapping is tested independently; its snapshot must match the persisted canonical version.
        await SapContractInboundEndpoint.HandleAsync(context);
        Assert.Equal(500, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = JsonNode.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync());
        Assert.Equal("Erro interno.", (string?)body?["mensagem"]);
        Assert.Empty(fixture.Events);
        Assert.Equal(before, fixture.Inner.Integracoes.Count);
    }

    [Theory]
    [InlineData(EstadoIntegracao.ResultadoIndeterminado)]
    [InlineData(EstadoIntegracao.Pendente)]
    public async Task PredecessorNaoTerminal_BloqueiaVersaoSeguinte(EstadoIntegracao predecessorState)
    {
        var fixture = new Fixture();
        var first = await fixture.RegisterAsync(Snapshot("A"));
        await fixture.SetStateAsync(first.IntegracaoId, predecessorState);
        var second = await fixture.RegisterAsync(Snapshot("B"));
        Assert.Equal(2, second.Versao);

        var result = await fixture.UseCase.ExecutarAsync(Snapshot("B"), "second", CancellationToken.None);

        Assert.Equal(ResultadoContratoSapToledo.ClaimBloqueado, result);
        Assert.Equal(EstadoIntegracao.Pendente, (await fixture.Inner.ObterPorIdAsync(second.IntegracaoId, CancellationToken.None))!.Estado);
        Assert.Empty(fixture.Events);
        Assert.DoesNotContain(fixture.Inner.Tentativas, attempt => attempt.IntegracaoId == second.IntegracaoId);
    }

    [Fact]
    public void Migration_ClaimGuard_ContractsNonterminalPredecessor()
    {
        var root = FindRoot();
        var sql = File.ReadAllText(Path.Combine(root, "docs", "Incrementos", "006", "MIGRATION_006H4_CONTRATO_SAP_TOLEDO_VERSOES.sql"));
        Assert.Contains("before update of estado", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new.estado = 'EM_PROCESSAMENTO'", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'RESULTADO_INDETERMINADO'", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("anterior.versao < v_versao", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if not found then\n        return new;", sql.Replace("\r\n", "\n"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("return null;", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("execute function fugates.fn_h4_guard_claim_contrato_sap_toledo()", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SharedMotorPipeline_OrdersFocalClaimMarkerProcessorAndResult()
    {
        var fixture = new Fixture();
        var version = await fixture.RegisterAsync(Snapshot("A"));

        var result = await fixture.Motor.ExecutarPorIdAsync(version.IntegracaoId, CancellationToken.None);

        Assert.Equal(CicloMotorIntegracaoResultado.Processado, result);
        Assert.Equal(["ClaimarEspecificamenteAsync", "MarcarEnvioIniciadoAsync", "Processador", "PersistirResultadoAsync"], fixture.Events);
        Assert.Equal(version.IntegracaoId, Assert.Single(fixture.ClaimTargets));
        Assert.Equal(1, fixture.Events.Count(x => x == "MarcarEnvioIniciadoAsync"));
        Assert.Equal(1, fixture.Events.Count(x => x == "Processador"));
    }

    private static ServiceProvider HttpServices(Fixture fixture)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SapInbound:BasicAuth:Username"] = "test", ["SapInbound:BasicAuth:Password"] = "test"
        }).Build();
        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton(fixture.UseCase)
            .BuildServiceProvider();
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FugaTES.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Solution root not found.");
    }

    private static ContratoSapToledoSnapshot Snapshot(string observation) =>
        new("Toledo", "0000001008", "AC3P", "Descricao", "11/09/2026", "11/09/2026", "31/12/2026",
            10, "31/12/2026", "T", 100000m, 10000m, "0020000051", "000000000007000001", "E",
            "0000000042", "001", "001", observation, 1.5m, "3011", "10", Array.Empty<string?>());

    private sealed class Fixture
    {
        public RepositorioMotorIntegracaoFake Inner { get; } = new();
        public List<string> Events { get; } = [];
        public List<Guid> ClaimTargets { get; } = [];
        public IContratoSapToledoVersaoStore Store { get; }
        public MotorIntegracaoServico Motor { get; }
        public ProcessarContratoSapToledoIdempotenteCasoDeUso UseCase { get; }
        private readonly ContratoSapToledoCanonicalizer canonicalizer = new();

        public Fixture()
        {
            var proxy = DispatchProxy.Create<IMotorIntegracaoRepository, RecordingRepository>();
            var recording = (RecordingRepository)proxy;
            recording.Inner = Inner;
            recording.Events = Events;
            recording.ClaimTargets = ClaimTargets;
            Store = new ContratoSapToledoVersaoStoreFake(Inner);
            Motor = new MotorIntegracaoServico(proxy, new RecordingProcessor(Events), new EmptyDetector(),
                new ValidadorGenericoIntegracao(), new WorkerProvider(), new TimeoutPolicy(), TimeProvider.System,
                new MotorIntegracaoOptions { LeaseDuration = TimeSpan.FromMinutes(1), LeaseRenewInterval = TimeSpan.FromSeconds(20), RetryBackoff = TimeSpan.FromMinutes(5) });
            UseCase = new ProcessarContratoSapToledoIdempotenteCasoDeUso(canonicalizer, Store, proxy, Motor);
        }

        public async Task<VersaoContratoSapToledo> RegisterAsync(ContratoSapToledoSnapshot snapshot)
        {
            var value = canonicalizer.Canonicalizar(snapshot);
            return await Store.RegistrarOuObterAsync(value.Snapshot, value.FingerprintSha256, value.PayloadCanonicoJson, "fixture", CancellationToken.None);
        }

        public async Task SetStateAsync(Guid id, EstadoIntegracao state)
        {
            var current = Inner.Integracoes.Single(x => x.Id == id);
            if (state == EstadoIntegracao.Pendente) return;
            var now = DateTimeOffset.UtcNow;
            var claim = await Inner.ClaimarEspecificamenteAsync(id, current.Estado, current.TipoDisparoPendente,
                current.FingerprintSha256, new IdentidadeWorkerIntegracao("H4-SETUP"), TimeSpan.FromMinutes(1), now, CancellationToken.None);
            Assert.NotNull(claim);
            Assert.True(await Inner.MarcarEnvioIniciadoAsync(id, claim.TentativaId, claim.FencingToken, claim.Worker, now, CancellationToken.None));
            Assert.True(await Inner.RegistrarResultadoIndeterminadoAsync(id, claim.TentativaId, claim.FencingToken, claim.Worker, now, CancellationToken.None));
        }
    }

    public class RecordingRepository : DispatchProxy
    {
        public IMotorIntegracaoRepository Inner { get; set; } = null!;
        public List<string> Events { get; set; } = null!;
        public List<Guid> ClaimTargets { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) throw new InvalidOperationException();
            if (targetMethod.Name is "ClaimarEspecificamenteAsync" or "MarcarEnvioIniciadoAsync" or "PersistirResultadoAsync")
            {
                Events.Add(targetMethod.Name);
                if (targetMethod.Name == "ClaimarEspecificamenteAsync") ClaimTargets.Add((Guid)args![0]!);
            }
            return targetMethod.Invoke(Inner, args);
        }
    }

    private sealed class RecordingProcessor(List<string> events) : IProcessadorExternoIntegracao
    {
        public Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim, CancellationToken ct)
        {
            events.Add("Processador");
            return Task.FromResult(ResultadoProcessamentoExterno.Sucesso);
        }
    }

    private sealed class EmptyDetector : IDetectorCandidatosIntegracao
    {
        public Task<IReadOnlyList<CandidatoIntegracao>> DetectarAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CandidatoIntegracao>>([]);
    }
    private sealed class WorkerProvider : IIdentidadeWorkerProvider { public IdentidadeWorkerIntegracao Obter() => new("H4-MATRIX"); }
    private sealed class TimeoutPolicy : IPoliticaTimeoutProcessamentoIntegracao { public TimeSpan ObterTimeout(ClaimIntegracaoMotor claim) => TimeSpan.FromMinutes(1); }
}
