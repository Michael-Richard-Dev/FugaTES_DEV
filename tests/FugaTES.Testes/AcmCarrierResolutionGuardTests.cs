using Microsoft.Extensions.DependencyInjection;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.CasosDeUso.Mvp;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.HostIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;

namespace FugaTES.Testes;

public sealed class AcmCarrierResolutionGuardTests
{
    [Theory]
    [InlineData("0000001031", "BP-EMISSOR")]
    [InlineData("0000098765", "CRM010")]
    [InlineData("0000012345", "OTHER-PARTNER")]
    public async Task No_carrier_blocks_data_without_pre_registration_or_sap_work(string number, string partner)
    {
        var f = new Fixture(number, partner);
        await f.Router.ProcessarAsync(f.Claim, default);
        Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna, f.Projection.Result!.Status);
        Assert.Equal("BLOCKED_DATA", f.Projection.Result.ResultadoFuncional);
        Assert.Equal("ACM_CARRIER_NOT_RESOLVED", f.Projection.Result.ErroCodigo);
        Assert.False(f.Projection.Result.ErroResultadoIndeterminado);
        Assert.Equal(0, f.Guardian.PreRegistrationPosts);
        Assert.Equal(0, f.Guardian.ContractWrites);
        Assert.Empty(f.Motor.Integracoes);
        Assert.Equal(f.Version, f.Versions.Version);
        Assert.Equal(f.Version.IntegracaoId, f.Links.Link!.IntegracaoIdOrigem);
        Assert.Equal(number, f.Links.Link.CodigoContratoConfirmado);
        Assert.Equal("OC" + number.TrimStart('0'), f.Links.Link.NumeroReferenciaSolicitado);
    }

    [Fact]
    public async Task Default_resolver_never_infers_issuing_partner_or_CRM_carrier()
    {
        foreach (var partner in new[] { "CRM010", "BP-EMISSOR", "ARBITRARY" })
        {
            var f = new Fixture("0000098765", partner);
            Assert.Null(await new ResolverTransportadoraAcmPendente().ResolverAsync(f.Version, default));
        }
    }

    [Fact]
    public async Task Resolver_replacement_allows_resume_without_changing_H4_or_correlation()
    {
        var resolver = new Resolver();
        var f = new Fixture("0000098765", "CRM010", resolver);
        await f.Router.ProcessarAsync(f.Claim, default);
        var link = f.Links.Link;
        Assert.Equal("BLOCKED_DATA", f.Projection.Result!.ResultadoFuncional);
        resolver.Result = new("TRANSACTION-CARRIER", "TRANSACTION-REF-42");
        await f.Router.ProcessarAsync(f.Claim with { FencingToken = 2 }, default);
        Assert.Equal("ACM_CARRIER_RESOLVED", f.Projection.Result!.ResultadoFuncional);
        Assert.Equal("ACM_PRECADASTRO_DISPATCH_PENDING", f.Projection.Result.ErroCodigo);
        Assert.Equal(link, f.Links.Link);
        Assert.Equal(f.Version, resolver.LastVersion);
        Assert.Equal(2, f.Guardian.Reads);
        Assert.Equal(0, f.Guardian.ContractWrites);
        Assert.Equal(0, f.Guardian.PreRegistrationPosts);
        Assert.Empty(f.Motor.Integracoes);
    }

    [Theory]
    [InlineData("", "PROOF")]
    [InlineData("CARRIER", "")]
    [InlineData("CARRIER", " ")]
    public async Task Unproven_or_empty_carrier_identity_remains_blocked(string code, string source)
    {
        var f = new Fixture("0000098765", "BP-EMISSOR", new Resolver { Result = new(code, source) });
        await f.Router.ProcessarAsync(f.Claim, default);
        Assert.Equal("BLOCKED_DATA", f.Projection.Result!.ResultadoFuncional);
        Assert.Equal("ACM_CARRIER_NOT_RESOLVED", f.Projection.Result.ErroCodigo);
        Assert.Equal(0, f.Guardian.PreRegistrationPosts);
        Assert.Empty(f.Motor.Integracoes);
    }

    [Fact]
    public async Task Existing_protocol_ticket_and_callback_identity_are_preserved()
    {
        var f = new Fixture("0000098765", "BP-EMISSOR");
        f.Links.Link = new(f.Version.NumeroContrato, f.Version.Versao, f.Version.IntegracaoId,
            "OC98765", f.Version.NumeroContrato, f.Version.NumeroContrato, 71, "TICKET-71", 991, 812, new string('B', 64));
        var original = f.Links.Link;
        await f.Router.ProcessarAsync(f.Claim, default);
        Assert.Equal(original, f.Links.Link);
        Assert.Equal("BLOCKED_DATA", f.Projection.Result!.ResultadoFuncional);
        Assert.Equal(f.Claim.CorrelationId, f.Projection.Result.Correlacao);
    }

    [Fact]
    public void Composition_preserves_a_functionally_approved_resolver_override()
    {
        var approved = new Resolver { Result = new("TRANSACTION-CARRIER", "TRANSACTION-REF") };
        var services = new ServiceCollection();
        services.AddSingleton<IResolverTransportadoraAcm>(approved);
        services.AddMvpOrchestration();
        using var provider = services.BuildServiceProvider();
        Assert.Same(approved, provider.GetRequiredService<IResolverTransportadoraAcm>());
    }

    [Fact]
    public async Task Default_composition_is_fail_closed()
    {
        var services = new ServiceCollection();
        services.AddMvpOrchestration();
        using var provider = services.BuildServiceProvider();
        var f = new Fixture("0000098765", "CRM010");
        Assert.Null(await provider.GetRequiredService<IResolverTransportadoraAcm>().ResolverAsync(f.Version, default));
    }

    private sealed class Resolver : IResolverTransportadoraAcm
    {
        public TransportadoraAcmComprovada? Result;
        public VersaoContratoSapToledo? LastVersion;
        public Task<TransportadoraAcmComprovada?> ResolverAsync(VersaoContratoSapToledo versao, CancellationToken ct)
        { LastVersion = versao; return Task.FromResult(Result); }
    }

    private sealed class Fixture
    {
        public readonly RepositorioMotorIntegracaoFake Motor = new();
        public readonly Versions Versions;
        public readonly Projection Projection = new();
        public readonly Links Links = new();
        public readonly Guardian Guardian;
        public readonly VersaoContratoSapToledo Version;
        public readonly ClaimIntegracaoMotor Claim;
        public readonly ProcessadorFluxosMvp Router;
        public Fixture(string number, string partner, IResolverTransportadoraAcm? resolver = null)
        {
            var snapshot = AcmProximityTemporaryR6Tests.Snapshot(number) with { IdParceiro = partner };
            Version = new(ResultadoRegistroVersaoContrato.JaExistente, number, 5, new string('A', 64), Guid.NewGuid(), snapshot);
            Versions = new(Version);
            var mapper = new MapeadorContratoAcmGuardian(new(null, "HML"));
            Guardian = new(mapper.Mapear(Version));
            Claim = new(Version.IntegracaoId, Guid.NewGuid(), "CARRIER-OFFLINE", "SAP", "TOLEDO",
                ProcessarContratoAcm.OperacaoInboundH4, number, Version.FingerprintSha256, 1, "OFFLINE", DateTimeOffset.UtcNow.AddMinutes(1));
            var useCase = new ProcessarContratoAcm(Motor, Projection, new DecisoesMvpConfirmadas(), TimeProvider.System,
                Versions, mapper, Guardian, vinculos: Links, transportadoras: resolver);
            Router = new([useCase]);
        }
    }

    private sealed class Projection : IResultadoFluxoMvpStore
    {
        public ResultadoFluxoMvp? Result;
        public Task ExigirDisponibilidadeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<ResultadoFluxoMvp?> ObterAsync(ClaimIntegracaoMotor claim, CancellationToken ct) => Task.FromResult(Result);
        public Task<bool> PersistirAsync(ClaimIntegracaoMotor claim, ResultadoFluxoMvp result, CancellationToken ct)
        { Result = result.Normalizar(); return Task.FromResult(true); }
    }

    private sealed class Versions(VersaoContratoSapToledo version) : IContratoSapToledoVersaoStore
    {
        public VersaoContratoSapToledo Version = version;
        public Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id, CancellationToken ct) => Task.FromResult<VersaoContratoSapToledo?>(Version);
        public Task<bool> TemPredecessorNaoTerminalAsync(string number, long version, CancellationToken ct) => Task.FromResult(false);
        public Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot snapshot, string fingerprint, string payload, string correlation, CancellationToken ct)
            => throw new InvalidOperationException("Inbound must be preserved.");
    }

    private sealed class Guardian(ContratoGuardianSet26 actual) : IGuardianContratoSincronizacaoGateway, IPreCadastroGuardianSet26Gateway
    {
        public int ContractWrites, PreRegistrationPosts, Reads;
        public Task<ConsultaContratoGuardian> ConsultarAsync(string code, CancellationToken ct)
        { Reads++; return Task.FromResult(new ConsultaContratoGuardian(EstadoConsultaContratoGuardian.Existente, actual)); }
        public Task CadastrarAsync(ContratoGuardianSet26 command, CancellationToken ct) { ContractWrites++; throw new InvalidOperationException(); }
        public Task<ResultadoContratoGuardianComResposta> CadastrarComRespostaAsync(ContratoGuardianSet26 command, CancellationToken ct)
        { ContractWrites++; throw new InvalidOperationException(); }
        public Task<ResultadoContratoGuardianComResposta> AlterarComRespostaAsync(ContratoGuardianSet26 previous, ContratoGuardianSet26 command, CancellationToken ct)
        { ContractWrites++; throw new InvalidOperationException(); }
        public Task<ProtocoloGuardianSet26> EnviarAsync(PreCadastroRecebimentoGuardianSet26 command, CancellationToken ct)
        { PreRegistrationPosts++; throw new InvalidOperationException("Carrier is unavailable."); }
    }

    private sealed class Links : IAcmGuardianVinculoStore
    {
        public AcmGuardianVinculo? Link;
        public Task<AcmGuardianVinculo> ReservarAsync(VersaoContratoSapToledo version, string reference, string code, CancellationToken ct)
        {
            Link ??= new(version.NumeroContrato, version.Versao, version.IntegracaoId, reference, code, null, null, null, null, null, null);
            return Task.FromResult(Link);
        }
        public Task<AcmGuardianVinculo?> ObterPorIntegracaoAsync(Guid id, CancellationToken ct) => Task.FromResult(Link);
        public Task<AcmGuardianVinculo> RegistrarCodigoConfirmadoAsync(Guid id, string code, CancellationToken ct)
        { Link = Link! with { CodigoContratoConfirmado = code }; return Task.FromResult(Link); }
        public Task<AcmGuardianVinculo> RegistrarProtocoloConfirmadoAsync(Guid id, long protocol, string? ticket, CancellationToken ct) => throw new InvalidOperationException();
        public Task<AcmGuardianVinculo> VincularCallbackComprovadoAsync(Guid id, long callback, long sequence, CancellationToken ct) => throw new InvalidOperationException();
        public Task<AcmGuardianVinculo?> ResolverCallbackAsync(long callback, long sequence, CancellationToken ct) => throw new InvalidOperationException();
        public Task<StatusEventoAcmGuardian> RegistrarFingerprintCallbackAsync(long callback, long sequence, string hash, CancellationToken ct) => throw new InvalidOperationException();
    }
}
