using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.HostIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Infraestrutura.Recebimento;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Text;

namespace FugaTES.Testes;

public sealed class Inc006H5HostGuardianCompositionTests
{
    [Fact]
    public async Task FakeMode_GuardianOperationNeverReportsGenericSuccess()
    {
        var fake = new ProcessadorExternoIntegracaoFake();

        var result = await fake.ProcessarAsync(GuardianClaim(), CancellationToken.None);

        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva, result.Falha);
    }

    [Fact]
    public void GuardianMode_MissingRuntimeConfigurationFailsBeforeHostBuild()
    {
        var builder = Builder(GuardianConfiguration());

        Assert.Throws<InvalidOperationException>(() =>
            HostIntegracaoComposition.ConfigurarServicos(builder.Services, builder.Configuration));
    }

    [Theory]
    [InlineData("Guardian:Email")]
    [InlineData("Guardian:Password")]
    public void GuardianMode_MissingRuntimeCredentialFailsBeforeHostBuild(string missingKey)
    {
        var config = GuardianConfiguration();
        AddGuardianConfiguration(config);
        config.Remove(missingKey);
        var builder = Builder(config);

        Assert.Throws<InvalidOperationException>(() =>
            HostIntegracaoComposition.ConfigurarServicos(builder.Services, builder.Configuration));
    }

    [Fact]
    public void GuardianMode_WithDummyRuntimeConfigurationResolvesNonFakeProcessor()
    {
        var config = GuardianConfiguration();
        AddGuardianConfiguration(config);
        var builder = Builder(config);
        HostIntegracaoComposition.ConfigurarServicos(builder.Services, builder.Configuration);
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        Assert.IsNotType<ProcessadorExternoIntegracaoFake>(
            scope.ServiceProvider.GetRequiredService<IProcessadorExternoIntegracao>());
    }

    [Fact]
    public void FakeMode_BaselineProcessorRemainsFake()
    {
        var builder = Builder(BaseConfiguration());
        HostIntegracaoComposition.ConfigurarServicos(builder.Services, builder.Configuration);
        using var host = builder.Build();

        Assert.IsType<ProcessadorExternoIntegracaoFake>(
            host.Services.GetRequiredService<IProcessadorExternoIntegracao>());
    }

    [Fact]
    public async Task GuardianMode_OtherOperationFailsClosedWithoutHttp()
    {
        var config = GuardianConfiguration();
        AddGuardianConfiguration(config);
        var builder = Builder(config);
        HostIntegracaoComposition.ConfigurarServicos(builder.Services, builder.Configuration);
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IProcessadorExternoIntegracao>()
            .ProcessarAsync(GuardianClaim() with { Operacao = "OUTRA_OPERACAO" }, CancellationToken.None);

        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva, result.Falha);
    }

    [Fact]
    public async Task GuardianMode_HostMotorUsesGuardianProcessorWithOfflineHttpHandler()
    {
        var config = GuardianConfiguration();
        AddGuardianConfiguration(config);
        var builder = Builder(config);
        HostIntegracaoComposition.ConfigurarServicos(builder.Services, builder.Configuration);
        var motorRepository = new RepositorioMotorIntegracaoFake();
        var correlations = new RepositorioCorrelacaoRecebimentoMemoria();
        var ticketEvidence = new UnexpectedAcmTicketEvidenceStore();
        var handler = new GuardianHandler(motorRepository);
        builder.Services.AddSingleton<IMotorIntegracaoRepository>(motorRepository);
        builder.Services.AddScoped<IRepositorioCorrelacaoRecebimento>(_ => correlations);
        builder.Services.AddSingleton<IAcmSapTicketEvidenceStore>(ticketEvidence);
        builder.Services.AddSingleton(new HttpClient(handler));
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var input = new RecebimentoSapCpiMvp("RECEBIMENTO", "FLX_RECEBIMENTO", "MAT-1", "BP-E", "BP-T",
            "CONTRATO-1", "DOC-001", "COMPRA", "3011", "ENTRADA", "NF", "PO-1", 1, null);
        var registration = new RegistrarPreCadastroGuardianMotorCasoDeUso(correlations, motorRepository,
            new GuardianSet26Options("/api/set26/pre-cadastro", "https://fugates.invalid/callback"));
        await registration.RegistrarAsync(input, CancellationToken.None);
        Assert.Equal(0, handler.PreCadastroCalls);

        var integration = Assert.Single(motorRepository.Integracoes);
        var result = await scope.ServiceProvider.GetRequiredService<MotorIntegracaoServico>()
            .ExecutarPorIdAsync(integration.Id, CancellationToken.None);

        Assert.Equal(CicloMotorIntegracaoResultado.Processado, result);
        Assert.Equal(EstadoIntegracao.Concluida, Assert.Single(motorRepository.Integracoes).Estado);
        Assert.Equal(1, handler.PreCadastroCalls);
        Assert.Equal("12345", (await correlations.ObterPorDocumentoAsync("DOC-001", CancellationToken.None))!.ProtocoloGuardian);
        Assert.Equal(0, ticketEvidence.Calls);
    }

    private static ClaimIntegracaoMotor GuardianClaim() => new(Guid.NewGuid(), Guid.NewGuid(),
        "DOC-001", "SAP", "GUARDIAN", GuardianPreCadastroMotorWork.Operacao, "DOC-001",
        GuardianPreCadastroMotorWork.Fingerprint("DOC-001"), 1, "TEST", DateTimeOffset.UtcNow.AddMinutes(1));

    private static HostApplicationBuilder Builder(Dictionary<string, string?> config)
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(config);
        return builder;
    }

    private static Dictionary<string, string?> BaseConfiguration() => new()
    {
        ["HostIntegracao:PollInterval"] = "00:00:01",
        ["HostIntegracao:ShutdownGracePeriod"] = "00:00:05",
        ["HostIntegracao:ExternalMode"] = "Fake",
        ["HostIntegracao:PersistenceMode"] = "Fake",
        ["MotorIntegracao:LeaseDuration"] = "00:01:00",
        ["MotorIntegracao:LeaseRenewInterval"] = "00:00:20",
        ["MotorIntegracao:RetryBackoff"] = "00:05:00",
        ["TimeoutProcessamentoIntegracao:Timeout"] = "00:00:45"
    };

    private static Dictionary<string, string?> GuardianConfiguration()
    {
        var config = BaseConfiguration();
        config["HostIntegracao:ExternalMode"] = "Guardian";
        config["HostIntegracao:PersistenceMode"] = "PostgreSql";
        config["ConnectionStrings:FugaTES"] = "Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_acm_hml_app;Search Path=fugates;Passfile=/synthetic/not-used/pgpass";
        config["AcmR57:Enabled"] = "true";
        return config;
    }

    private static void AddGuardianConfiguration(Dictionary<string, string?> config)
    {
        config["Guardian:BaseUrl"] = "https://guardian.invalid";
        config["Guardian:Timeout"] = "00:00:10";
        config["Guardian:PreCadastroEndpoint"] = "/api/v1/Guardian/ProtocoloPreCadastro";
        config["Guardian:Email"] = "test-user";
        config["Guardian:Password"] = "test-credential";
        config["Guardian:Signature"] = "test-signature";
    }

    private sealed class UnexpectedAcmTicketEvidenceStore : IAcmSapTicketEvidenceStore
    {
        public int Calls { get; private set; }

        public Task PreservarAsync(CorrelacaoRecebimentoMvp correlacao, PesagemSapMvp pesagem, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Pre-cadastro must not persist ACM SAP ticket evidence.");
        }
    }

    private sealed class GuardianHandler(RepositorioMotorIntegracaoFake motor) : HttpMessageHandler
    {
        public int PreCadastroCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/CreateToken", StringComparison.Ordinal))
                return Task.FromResult(Json("{\"token\":\"test-token\"}"));
            if (path.EndsWith("/ProtocoloPreCadastro", StringComparison.Ordinal))
            {
                Assert.Contains(motor.Auditorias, audit => audit.Evento == "envio_iniciado");
                PreCadastroCalls++;
                return Task.FromResult(Json("{\"id\":12345,\"dataCadastro\":\"2026-09-24T12:00:00Z\"}"));
            }
            throw new InvalidOperationException("Unexpected offline HTTP route.");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
