using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Callback.Http;
using FugaTES.Persistencia.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Infraestrutura.ContratosSapToledo;
using FugaTES.Infraestrutura.MotorIntegracao;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace FugaTES.Testes;

public sealed class Inc006SapInboundDurableAckTests
{
    private const string BasicUsername = "SYNTHETIC_R165AH_DURABLE_USER";
    private const string BasicPassword = "SYNTHETIC_R165AH_DURABLE_NOT_A_CREDENTIAL";

    [Fact]
    public async Task Novo_replay_e_nova_versao_nao_processam_inline()
    {
        var store = new RecordingStore();
        var sut = new AceitarContratoSapToledoDuravelCasoDeUso(new ContratoSapToledoCanonicalizer(), store);

        var first = await sut.ExecutarAsync(Snapshot(), "corr-1", CancellationToken.None);
        var afterRestart = new AceitarContratoSapToledoDuravelCasoDeUso(new ContratoSapToledoCanonicalizer(), store);
        var replay = await afterRestart.ExecutarAsync(Snapshot(), "corr-2", CancellationToken.None);
        var changed = await sut.ExecutarAsync(Snapshot() with { Material = "MAT-B" }, "corr-3", CancellationToken.None);

        Assert.Equal((1L, false), (first.Versao, first.Reentrega));
        Assert.Equal((1L, true), (replay.Versao, replay.Reentrega));
        Assert.Equal((2L, false), (changed.Versao, changed.Reentrega));
        Assert.Equal(2, store.Integrations);
    }

    [Fact]
    public async Task Http_produtivo_retorna_aceite_replay_e_nova_versao()
    {
        var store = new RecordingStore();
        using var provider = Provider(store);
        var first = await PostAsync(provider, "MAT-A");
        var replay = await PostAsync(provider, "MAT-A");
        var changed = await PostAsync(provider, "MAT-B");

        Assert.Equal(201, first.Status);
        Assert.Equal("ACEITO", (string?)first.Body["resultado"]);
        Assert.Equal("CONTRATO-A", (string?)first.Body["numeroContrato"]);
        Assert.Equal(1, (int?)first.Body["versao"]);
        Assert.Equal(false, (bool?)first.Body["reentrega"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)first.Body["correlationId"]));
        Assert.Equal(201, replay.Status);
        Assert.Equal("REENTREGA_IDEMPOTENTE", (string?)replay.Body["resultado"]);
        Assert.Equal(1, (int?)replay.Body["versao"]);
        Assert.Equal(true, (bool?)replay.Body["reentrega"]);
        Assert.Equal(201, changed.Status);
        Assert.Equal(2, (int?)changed.Body["versao"]);
        Assert.Equal(2, store.Integrations);
    }

    [Fact]
    public async Task Http_produtivo_falha_fechado_antes_do_ack()
    {
        var store = new RecordingStore { Fail = true };
        using var provider = Provider(store);
        Assert.Equal(500, (await PostAsync(provider, "MAT-A")).Status);
        Assert.Equal(0, store.Integrations);
        store.Fail = false;
        store.FailAtCommit = true;
        Assert.Equal(500, (await PostAsync(provider, "MAT-A")).Status);
        Assert.Equal(0, store.Integrations);
        store.FailAtCommit = false;
        Assert.Equal(415, (await PostAsync(provider, "MAT-A", contentType: "text/plain")).Status);
        Assert.Equal(403, (await PostAsync(provider, "MAT-A", remoteIp: IPAddress.Parse("192.0.2.1"))).Status);
        Assert.Equal(0, store.Integrations);
    }

    [Fact]
    public void Composicao_produtiva_resolve_somente_repositorios_duraveis()
    {
        var services = new ServiceCollection();
        Assert.True(SapContractInboundProductiveComposition.ConfigurarServicos(services, ProductiveConfig()));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<ContratoSapToledoVersaoStorePostgreSql>(
            provider.GetRequiredService<IContratoSapToledoVersaoStore>());
        Assert.NotNull(provider.GetRequiredService<IMotorIntegracaoRepository>());
        Assert.Empty(provider.GetServices<IContratoToledoGateway>());
        Assert.DoesNotContain(services, x => x.ImplementationType == typeof(ContratoSapToledoVersaoStoreFake)
            || x.ImplementationType == typeof(RepositorioMotorIntegracaoFake));
    }

    [Fact]
    public void Composicao_produtiva_incompleta_ou_mista_falha_fechado()
    {
        Assert.Throws<InvalidOperationException>(() => SapContractInboundProductiveComposition.ConfigurarServicos(
            new ServiceCollection(), ProductiveConfig(includeConnection: false)));

        Assert.Throws<InvalidOperationException>(() => SapContractInboundProductiveComposition.ConfigurarServicos(
            new ServiceCollection(), ProductiveConfig(offline: true)));

        var fakeSelected = new ServiceCollection();
        fakeSelected.AddSingleton<IContratoSapToledoVersaoStore, ContratoSapToledoVersaoStoreFake>();
        Assert.Throws<InvalidOperationException>(() => SapContractInboundProductiveComposition.ConfigurarServicos(
            fakeSelected, ProductiveConfig()));
    }

    [Fact]
    public void Startup_sap_only_nao_exige_configuracao_callback()
    {
        var services = new ServiceCollection();
        var routes = InboundHttpHostComposition.Configurar(services, ProductiveConfig(), isDevelopment: false);
        Assert.Null(routes.Callback);
        Assert.True(routes.SapProductive);
        Assert.False(routes.SapOffline);
        Assert.DoesNotContain(services, x => x.ServiceType == typeof(FugaTES.Aplicacao.Contratos.Recebimento.IRepositorioCorrelacaoRecebimento));
    }

    [Fact]
    public void Startup_produtivo_recusa_offline_em_hml()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SapContractInbound:OfflineEnabled"] = "true"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => InboundHttpHostComposition.Configurar(
            new ServiceCollection(), config, isDevelopment: false));
    }

    [Fact]
    public async Task Host_sap_only_constroi_sem_config_callback_e_sem_rede()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(ProductiveConfig().AsEnumerable());
        var routes = InboundHttpHostComposition.Configurar(builder.Services, builder.Configuration, false);
        await using var app = builder.Build();
        HealthEndpoint.Map(app);
        if (routes.SapProductive) SapContractInboundEndpoint.MapDurable(app);

        Assert.True(routes.SapProductive);
        Assert.Null(routes.Callback);
        Assert.NotNull(app.Services.GetRequiredService<IContratoSapToledoVersaoStore>());
    }

    [Fact]
    public void Store_postgresql_registra_integracao_versao_e_commit_antes_de_retornar()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "FugaTES.Persistencia",
            "ContratosSapToledo", "ContratoSapToledoVersaoStorePostgreSql.cs"));
        Assert.Contains("RegistrarCandidatoNaTransacaoAsync(connection,tx", source, StringComparison.Ordinal);
        Assert.Contains("await tx.CommitAsync(ct);return new(ResultadoRegistroVersaoContrato.Criada", source, StringComparison.Ordinal);
        Assert.Contains("await tx.CommitAsync(ct);return new(ResultadoRegistroVersaoContrato.JaExistente", source, StringComparison.Ordinal);
        Assert.Contains("await tx.RollbackAsync(ct)", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Motor_fake_nao_declára_sucesso_para_operacao_H4()
    {
        var claim = new ClaimIntegracaoMotor(Guid.NewGuid(), Guid.NewGuid(), "corr", "SAP", "TOLEDO",
            "SAP_CONTRATO_TOLEDO", "CONTRATO-A", new string('A', 64), 1, "worker",
            DateTimeOffset.UtcNow.AddMinutes(1));
        var result = await new ProcessadorExternoIntegracaoFake().ProcessarAsync(claim, CancellationToken.None);
        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva, result.Falha);
    }

    private static IConfiguration ProductiveConfig(bool includeConnection = true, bool offline = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["SapContractInbound:ProductiveEnabled"] = "true",
            ["SapContractInbound:SecurityMode"] = "LoopbackOnly",
            ["SapInbound:BasicAuth:Username"] = BasicUsername,
            ["SapInbound:BasicAuth:Password"] = BasicPassword
        };
        if (includeConnection) values["ConnectionStrings:FugaTES"] = "Host=example.invalid;Database=fugates;Username=fugates";
        if (offline) values["SapContractInbound:OfflineEnabled"] = "true";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FugaTES.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static ServiceProvider Provider(RecordingStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(ProductiveConfig());
        services.AddSingleton<IContratoSapToledoVersaoStore>(store);
        services.AddSingleton<ContratoSapToledoCanonicalizer>();
        services.AddScoped<AceitarContratoSapToledoDuravelCasoDeUso>();
        return services.BuildServiceProvider();
    }

    private static async Task<(int Status, JsonObject Body)> PostAsync(ServiceProvider provider, string material,
        string contentType = "application/json", IPAddress? remoteIp = null)
    {
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Connection.RemoteIpAddress = remoteIp ?? IPAddress.Loopback;
        context.Request.Method = "POST";
        context.Request.Headers.Authorization = "Basic " +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(BasicUsername + ":" + BasicPassword));
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            $$"""{"Contrato":[{"NumeroContrato":"CONTRATO-A","Material":"{{material}}"}]}"""));
        context.Response.Body = new MemoryStream();
        await SapContractInboundEndpoint.HandleDurableAsync(context);
        context.Response.Body.Position = 0;
        var body = (await JsonNode.ParseAsync(context.Response.Body))?.AsObject() ?? [];
        return (context.Response.StatusCode, body);
    }

    private static ContratoSapToledoSnapshot Snapshot() => new(
        "Toledo", "CONTRATO-A", "1", null, null, null, null, null, null, null,
        null, null, null, "MAT-A", null, null, null, null, null, null, null, null, []);

    private sealed class RecordingStore : IContratoSapToledoVersaoStore
    {
        private readonly List<VersaoContratoSapToledo> versions = [];
        public int Integrations => versions.Count;
        public bool Fail { get; set; }
        public bool FailAtCommit { get; set; }

        public Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot snapshot,
            string fingerprint, string payload, string correlationId, CancellationToken token)
        {
            if (Fail) throw new InvalidOperationException("Synthetic pre-commit failure.");
            var existing = versions.SingleOrDefault(x => x.NumeroContrato == snapshot.NumeroContrato &&
                x.FingerprintSha256 == fingerprint);
            if (existing is not null)
                return Task.FromResult(existing with { Resultado = ResultadoRegistroVersaoContrato.JaExistente });
            var version = new VersaoContratoSapToledo(ResultadoRegistroVersaoContrato.Criada,
                snapshot.NumeroContrato, versions.Count + 1, fingerprint, Guid.NewGuid(), snapshot);
            if (FailAtCommit) throw new InvalidOperationException("Synthetic commit failure.");
            versions.Add(version);
            return Task.FromResult(version);
        }

        public Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id, CancellationToken token) =>
            Task.FromResult<VersaoContratoSapToledo?>(versions.SingleOrDefault(x => x.IntegracaoId == id));

        public Task<bool> TemPredecessorNaoTerminalAsync(string numero, long versao, CancellationToken token) =>
            Task.FromResult(false);
    }
}
