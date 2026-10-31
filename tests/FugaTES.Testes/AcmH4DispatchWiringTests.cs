using FugaTES.Aplicacao.CasosDeUso.Mvp;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Servicos.SAP;
using FugaTES.HostIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Persistencia.ContratosSapToledo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FugaTES.Testes;

public sealed class AcmH4DispatchWiringTests
{
    [Fact]
    public async Task HostRegistraStoreH4SemAbrirConexao()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HostIntegracao:PersistenceMode"] = "PostgreSql",
            ["HostIntegracao:ExternalMode"] = "Guardian",
            ["ConnectionStrings:FugaTES"] = "Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_acm_hml_app;Search Path=fugates;Passfile=/synthetic/not-used/pgpass",
            ["AcmR57:Enabled"] = "true",
            ["Guardian:BaseUrl"] = "https://guardian.invalid",
            ["Guardian:Timeout"] = "00:00:10",
            ["Guardian:Email"] = "offline-fixture",
            ["Guardian:Password"] = "explicitly-fake-fixture",
            ["Guardian:Signature"] = "explicitly-fake-fixture",
            ["MotorIntegracao:LeaseDuration"] = "00:01:00",
            ["MotorIntegracao:LeaseRenewInterval"] = "00:00:20",
            ["MotorIntegracao:RetryBackoff"] = "00:05:00",
            ["HostIntegracao:PollInterval"] = "00:00:01",
            ["HostIntegracao:ShutdownGracePeriod"] = "00:00:05",
            ["TimeoutProcessamentoIntegracao:Timeout"] = "00:00:45"
        }).Build();
        services.AddSingleton<IConfiguration>(config);
        HostIntegracaoComposition.ConfigurarServicos(services, config);
        Assert.Contains(services, d => d.ServiceType == typeof(IContratoSapToledoVersaoStore)
            && d.ImplementationType == typeof(ContratoSapToledoVersaoStorePostgreSql));
        var f = new Fixture();
        services.Replace(ServiceDescriptor.Singleton<IMotorIntegracaoRepository>(f.Motor));
        services.Replace(ServiceDescriptor.Singleton<IContratoSapToledoVersaoStore>(f.Contracts));
        services.Replace(ServiceDescriptor.Singleton<IResultadoFluxoMvpStore>(f.Results));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var useCase = scope.ServiceProvider.GetRequiredService<ProcessarContratoAcm>();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AcmR57Pipeline>());
        var router = scope.ServiceProvider.GetRequiredService<IProcessadorExternoIntegracao>();
        Assert.IsType<ProcessadorExternoIntegracaoRoteado>(router);
        // R57 rejects the synthetic fingerprint before accessing any durable journal/link.
        var blocked = await useCase.ExecutarAsync(f.Claim, default);
        Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna, blocked.Status);
        Assert.Equal("R57_INBOUND_IDENTITY_INVALID", blocked.Erro);
        Assert.Equal(f.Claim.CorrelationId, blocked.Correlacao);
        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaInternaPreEnvio,
            (await router.ProcessarAsync(f.Claim, default)).Falha);
        Assert.Contains(f.Claim.IntegracaoId, f.Contracts.Lookups);
        Assert.Null(f.Results.LastClaim);
        Assert.Null(f.Results.Result);
        Assert.Equal(0, f.Results.Writes);
        Assert.Empty(f.Motor.Integracoes);
    }

    [Fact]
    public async Task InboundInvocaCasoRealPreservaClaimEBloqueioFuncional()
    {
        var f = new Fixture();
        var result = await f.Router.ProcessarAsync(f.Claim, default);
        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaInternaPreEnvio, result.Falha);
        Assert.Contains(f.Claim.IntegracaoId, f.Contracts.Lookups);
        Assert.Same(f.Claim, f.Results.LastClaim);
        Assert.Equal(StatusFluxoMvp.AguardandoDecisaoFuncional, f.Results.Result!.Status);
        Assert.Equal("ACM_ESTADO", f.Results.Result.DecisaoPendente);
        Assert.Equal(0, f.Guardian.Calls);
    }

    [Fact]
    public void ProjecaoAceitaOperacaoH4SemTrocarOperacaoDoClaim()
        => Assert.Equal("ACM_IN", ResultadoFluxoMvp.MapearFluxo("SAP_CONTRATO_TOLEDO"));

    [Theory]
    [InlineData("ID")]
    [InlineData("CONTRACT")]
    [InlineData("FINGERPRINT")]
    [InlineData("MISSING")]
    [InlineData("CENTRO")]
    public async Task VinculoIncorretoFailClosed(string defect)
    {
        var f = new Fixture();
        var v = f.Contracts.Version!;
        f.Contracts.Version = defect switch
        {
            "ID" => v with { IntegracaoId = Guid.NewGuid() },
            "CONTRACT" => v with { NumeroContrato = "OUTRO" },
            "FINGERPRINT" => v with { FingerprintSha256 = new string('B', 64) },
            "CENTRO" => v with { Snapshot = v.Snapshot with { Centro = "3008" } },
            _ => null
        };
        await f.Router.ProcessarAsync(f.Claim, default);
        Assert.Contains(f.Claim.IntegracaoId, f.Contracts.Lookups);
        Assert.Null(f.Results.Result);
        Assert.Equal(0, f.Guardian.Calls);
    }

    [Fact]
    public async Task ReplayTerminalNaoCriaTrabalhoOuChamadaExterna()
    {
        var f = new Fixture();
        f.Results.Result = new(StatusFluxoMvp.Concluido, f.Claim.CorrelationId, "MVP_ACMINCONTRATO");
        var first = await f.Router.ProcessarAsync(f.Claim, default);
        var second = await f.Router.ProcessarAsync(f.Claim, default);
        Assert.Equal(ResultadoProcessamentoExterno.Sucesso, first);
        Assert.Equal(ResultadoProcessamentoExterno.Sucesso, second);
        Assert.Equal(0, f.Results.Writes);
        Assert.Empty(f.Motor.Integracoes);
        Assert.Equal(0, f.Guardian.Calls);
    }

    [Fact]
    public async Task CenarioAprovadoAvancaAteGuardaFinanceiraSemEnviarGuardian()
    {
        var f = new Fixture();
        var version = f.Contracts.Version!;
        var claim = f.Claim with { ChaveNegocio = "0000001031" };
        f.Contracts.Version = version with { NumeroContrato = claim.ChaveNegocio,
            Snapshot = AcmGuardianReferenceMapperTests.Snapshot() };
        await f.Router.ProcessarAsync(claim, default);
        Assert.Equal("ACM_VALOR_PROXIMIDADE_FIM", f.Results.Result!.DecisaoPendente);
        Assert.Equal(0, f.Guardian.Calls);
        Assert.Empty(f.Motor.Integracoes);
    }

    [Fact]
    public async Task ContratoSemRegraGenericaPermaneceBloqueado()
    {
        var f = new Fixture();
        var claim = f.Claim with { ChaveNegocio = "0000001031" };
        f.Contracts.Version = f.Contracts.Version! with { NumeroContrato = claim.ChaveNegocio,
            Versao = 2, Snapshot = AcmGuardianReferenceMapperTests.Snapshot() };
        await f.Router.ProcessarAsync(claim, default);
        Assert.Equal("ACM_VALOR_PROXIMIDADE_FIM", f.Results.Result!.DecisaoPendente);
        Assert.Equal(StatusFluxoMvp.AguardandoDecisaoFuncional, f.Results.Result.Status);
        Assert.Same(claim, f.Results.LastClaim);
        Assert.Equal(0, f.Guardian.Calls);
        Assert.Empty(f.Motor.Integracoes);
    }

    [Fact]
    public async Task ClaimMvpLegadoContinuaRoteado()
    {
        var f = new Fixture();
        var identity = f.Claim.IntegracaoId.ToString();
        var claim = f.Claim with { Origem = f.UseCase.Origem, Destino = f.UseCase.Destino,
            Operacao = f.UseCase.Operacao, ChaveNegocio = identity, FingerprintSha256 = f.UseCase.Fingerprint(identity) };
        await f.Router.ProcessarAsync(claim, default);
        Assert.Equal("ACM_ESTADO", f.Results.Result!.DecisaoPendente);
        Assert.Equal(0, f.Guardian.Calls);
    }

    [Fact]
    public async Task Hml_temporary_rule_preserves_H4_dispatch_and_reaches_durable_link_guard()
    {
        var f = new Fixture(hml: true);
        var claim = f.Claim with { ChaveNegocio = "0000098765" };
        f.Contracts.Version = f.Contracts.Version! with { NumeroContrato = claim.ChaveNegocio,
            Snapshot = AcmProximityTemporaryR6Tests.Snapshot(claim.ChaveNegocio) };
        await f.Router.ProcessarAsync(claim, default);
        Assert.Same(claim, f.Results.LastClaim);
        Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna, f.Results.Result!.Status);
        Assert.Equal("ACM_H4_GUARDIAN_STORE_UNAVAILABLE", f.Results.Result.Erro);
        Assert.Equal(0, f.Guardian.Calls);
        Assert.Empty(f.Motor.Integracoes);
    }

    private sealed class Fixture
    {
        public readonly RepositorioMotorIntegracaoFake Motor = new();
        public readonly Contracts Contracts = new();
        public readonly Results Results = new();
        public readonly Guardian Guardian = new();
        public readonly ClaimIntegracaoMotor Claim;
        public readonly ProcessarContratoAcm UseCase;
        public readonly ProcessadorFluxosMvp Router;
        public Fixture(bool hml = false)
        {
            Claim = new(Guid.NewGuid(), Guid.NewGuid(), "ACM-H4-EXPLICIT-OFFLINE", "SAP", "TOLEDO",
                "SAP_CONTRATO_TOLEDO", "CONTRATO-SINTETICO", new string('A', 64), 1,
                "OFFLINE", DateTimeOffset.UtcNow.AddMinutes(1));
            var snapshot = new ContratoSapToledoSnapshot(null, Claim.ChaveNegocio, null, null,
                null, null, null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, "3011", null, []);
            Contracts.Version = new(ResultadoRegistroVersaoContrato.JaExistente, Claim.ChaveNegocio,
                1, Claim.FingerprintSha256, Claim.IntegracaoId, snapshot);
            UseCase = new(Motor, Results, new DecisoesMvpConfirmadas(), TimeProvider.System, Contracts, new MapeadorContratoAcmGuardian(hml ? new(null, "HML") : null), Guardian);
            Router = new([UseCase]);
        }
    }

    private sealed class Contracts : IContratoSapToledoVersaoStore
    {
        public VersaoContratoSapToledo? Version;
        public readonly List<Guid> Lookups = [];
        public Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id, CancellationToken ct)
        { Lookups.Add(id); return Task.FromResult(Version); }
        public Task<bool> TemPredecessorNaoTerminalAsync(string numero, long version, CancellationToken ct) => Task.FromResult(false);
        public Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot snapshot,
            string fingerprint, string payload, string correlation, CancellationToken ct) => throw new InvalidOperationException("Not permitted in dispatch.");
    }

    private sealed class Results : IResultadoFluxoMvpStore
    {
        public ResultadoFluxoMvp? Result;
        public ClaimIntegracaoMotor? LastClaim;
        public int Writes;
        public Task ExigirDisponibilidadeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<ResultadoFluxoMvp?> ObterAsync(ClaimIntegracaoMotor claim, CancellationToken ct)
        { LastClaim = claim; return Task.FromResult(Result); }
        public Task<bool> PersistirAsync(ClaimIntegracaoMotor claim, ResultadoFluxoMvp result, CancellationToken ct)
        { LastClaim = claim; Result = result.Normalizar(); Writes++; return Task.FromResult(true); }
    }

    private sealed class Guardian : IMapeadorContratoAcmGuardian, ICadastraContratoGuardianGateway
    {
        public int Calls;
        public ContratoGuardianSet26 Mapear(ContratoSapToledoSnapshot snapshot)
            => throw new DecisaoFuncionalPendenteSapException("ACM_ESTADO");
        public Task CadastrarAsync(ContratoGuardianSet26 contract, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("External calls are prohibited."); }
    }
}
