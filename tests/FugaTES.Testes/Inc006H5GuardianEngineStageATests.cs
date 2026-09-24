using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Callback.Http;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Infraestrutura.Recebimento;
using FugaTES.Persistencia.Recebimento;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text.Json;

namespace FugaTES.Testes;

public sealed class Inc006H5GuardianEngineStageATests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly GuardianSet26Options Options = new("/pre-cadastro", "https://fugates.invalid/callback");

    [Fact]
    public async Task RegistrationAndEngine_UseDurableContextAndCompleteOneAttempt()
    {
        var guardian = new RecordingGuardian();
        using var provider = Services(guardian);
        using var scope = provider.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<RegistrarPreCadastroGuardianMotorCasoDeUso>();
        var motor = scope.ServiceProvider.GetRequiredService<MotorIntegracaoServico>();
        var repository = provider.GetRequiredService<RepositorioMotorIntegracaoFake>();
        var input = Input();

        await registration.RegistrarAsync(input, CancellationToken.None);
        Assert.Equal(0, guardian.Calls);
        var integration = Assert.Single(repository.Integracoes);
        Assert.Equal(EstadoIntegracao.Pendente, integration.Estado);
        guardian.BeforeSend = () => Assert.Contains(repository.Auditorias,
            audit => audit.Evento == "envio_iniciado" && audit.TentativaId is not null);
        input = null!;

        var result = await motor.ExecutarPorIdAsync(integration.Id, CancellationToken.None);

        Assert.Equal(CicloMotorIntegracaoResultado.Processado, result);
        Assert.Equal(EstadoIntegracao.Concluida, Assert.Single(repository.Integracoes).Estado);
        Assert.Single(repository.Tentativas);
        Assert.Equal(1, guardian.Calls);
        var request = Assert.IsType<PreCadastroRecebimentoGuardianSet26>(guardian.LastRequest);
        Assert.Equal(0, request.Id);
        Assert.Equal("FLX_RECEBIMENTO", request.CodigoFluxo);
        Assert.Equal("MAT-1", request.CodigoItem);
        Assert.Equal("BP-E", request.CodigoEmissor);
        Assert.Equal("BP-T", request.CodigoTransportadora);
        Assert.Equal("CONTRATO-1", request.CodigoContrato);
        Assert.Equal(1, request.IdAgenda);
        Assert.Equal("DOC-001", request.NumeroReferencia);
        Assert.Equal(Options.UrlParaRetorno, request.UrlParaRetorno);
        Assert.Equal([(1000, "COMPRA"), (1002, "3011"), (1003, "ENTRADA")],
            request.CamposAdicionais.Select(c => (c.Numero, c.Valor)));
        var correlation = await scope.ServiceProvider.GetRequiredService<IRepositorioCorrelacaoRecebimento>()
            .ObterPorDocumentoAsync("DOC-001", CancellationToken.None);
        Assert.Equal("12345", correlation!.ProtocoloGuardian);
        Assert.Equal(Created, correlation.ProtocoloCriadoEm);
        Assert.Equal("COMPRA", correlation.Entrada.TipoContrato);
        Assert.Equal(Options.UrlParaRetorno, correlation.Entrada.UrlParaRetorno);
    }

    [Fact]
    public async Task PostSuccessPersistenceFailure_ProcessorReturnsExistingIndeterminateContract()
    {
        var inner = new RepositorioCorrelacaoRecebimentoMemoria();
        await inner.RegistrarSnapshotAsync(Input() with { UrlParaRetorno = Options.UrlParaRetorno }, CancellationToken.None);
        var guardian = new RecordingGuardian();
        var processor = new ProcessadorPreCadastroGuardianMotor(new FailingProtocolRepository(inner), guardian);

        var result = await processor.ProcessarAsync(Claim(), CancellationToken.None);

        Assert.Equal(ClassificacaoFalhaIntegracao.ResultadoIndeterminado, result.Falha);
        Assert.Equal(1, guardian.Calls);
    }

    [Fact]
    public async Task PersistenceFailureAfterGuardianSuccess_BecomesIndeterminateWithoutAutomaticSecondPost()
    {
        var guardian = new RecordingGuardian();
        var inner = new RepositorioCorrelacaoRecebimentoMemoria();
        using var provider = Services(guardian, new FailingProtocolRepository(inner));
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RegistrarPreCadastroGuardianMotorCasoDeUso>()
            .RegistrarAsync(Input(), CancellationToken.None);
        var repository = provider.GetRequiredService<RepositorioMotorIntegracaoFake>();
        var integration = Assert.Single(repository.Integracoes);
        var motor = scope.ServiceProvider.GetRequiredService<MotorIntegracaoServico>();

        var result = await motor.ExecutarPorIdAsync(integration.Id, CancellationToken.None);
        Assert.Equal(CicloMotorIntegracaoResultado.ResultadoIndeterminado, result);
        Assert.Equal(EstadoIntegracao.ResultadoIndeterminado, Assert.Single(repository.Integracoes).Estado);
        Assert.Equal(1, guardian.Calls);
        Assert.Equal(CicloMotorIntegracaoResultado.NenhumTrabalho,
            await motor.ExecutarUmCicloAsync(CancellationToken.None));
        Assert.Equal(CicloMotorIntegracaoResultado.ClaimNaoAdquirido,
            await motor.ExecutarPorIdAsync(integration.Id, CancellationToken.None));
        Assert.Equal(1, guardian.Calls);
    }

    [Fact]
    public async Task MissingRequiredContext_FailsBeforeExternalEffect()
    {
        var guardian = new RecordingGuardian();
        using var provider = Services(guardian);
        using var scope = provider.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<RegistrarPreCadastroGuardianMotorCasoDeUso>();
        await Assert.ThrowsAsync<ArgumentException>(() => registration.RegistrarAsync(
            Input() with { TipoContrato = "" }, CancellationToken.None));
        Assert.Empty(provider.GetRequiredService<RepositorioMotorIntegracaoFake>().Integracoes);
        Assert.Equal(0, guardian.Calls);
    }

    [Fact]
    public async Task SerializedSnapshot_RehydratesRequestInNewProcessorInstance()
    {
        var snapshot = Input() with { UrlParaRetorno = Options.UrlParaRetorno };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        snapshot = null!;
        var restored = JsonSerializer.Deserialize<RecebimentoSapCpiMvp>(bytes)!;
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();
        await repository.RegistrarSnapshotAsync(restored, CancellationToken.None);
        restored = null!;
        var guardian = new RecordingGuardian();
        var processor = new ProcessadorPreCadastroGuardianMotor(repository, guardian);

        var result = await processor.ProcessarAsync(Claim(), CancellationToken.None);

        Assert.Null(result.Falha);
        Assert.Equal("COMPRA", guardian.LastRequest!.CamposAdicionais.Single(c => c.Numero == 1000).Valor);
        Assert.Equal(Options.UrlParaRetorno, guardian.LastRequest.UrlParaRetorno);
        Assert.Equal(1, guardian.Calls);
    }

    [Fact]
    public async Task HistoricalSnapshotWithoutReturnUrl_FailsBeforeExternalEffect()
    {
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();
        await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None);
        var guardian = new RecordingGuardian();
        var processor = new ProcessadorPreCadastroGuardianMotor(repository, guardian);

        var result = await processor.ProcessarAsync(Claim(), CancellationToken.None);

        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva, result.Falha);
        Assert.Equal(0, guardian.Calls);
    }

    [Fact]
    public async Task HistoricalProtocolWithoutCreationTime_NeverTriggersAnotherPost()
    {
        var guardian = new RecordingGuardian();
        var inner = new RepositorioCorrelacaoRecebimentoMemoria();
        using var provider = Services(guardian, new PartialProtocolRepository(inner));
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RegistrarPreCadastroGuardianMotorCasoDeUso>()
            .RegistrarAsync(Input(), CancellationToken.None);
        var integration = Assert.Single(provider.GetRequiredService<RepositorioMotorIntegracaoFake>().Integracoes);

        var result = await scope.ServiceProvider.GetRequiredService<MotorIntegracaoServico>()
            .ExecutarPorIdAsync(integration.Id, CancellationToken.None);

        Assert.Equal(CicloMotorIntegracaoResultado.ResultadoIndeterminado, result);
        Assert.Equal(0, guardian.Calls);
    }

    [Fact]
    public async Task SameContextIsIdempotent_DifferentTypeOrReturnUrlConflicts()
    {
        var guardian = new RecordingGuardian();
        using var provider = Services(guardian);
        using var scope = provider.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<RegistrarPreCadastroGuardianMotorCasoDeUso>();
        Assert.Equal(StatusRegistroSnapshotMvp.Criada,
            (await registration.RegistrarAsync(Input(), CancellationToken.None)).Status);
        Assert.Equal(StatusRegistroSnapshotMvp.Idempotente,
            (await registration.RegistrarAsync(Input(), CancellationToken.None)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registration.RegistrarAsync(
            Input() with { TipoContrato = "VENDA" }, CancellationToken.None));
        var correlations = scope.ServiceProvider.GetRequiredService<IRepositorioCorrelacaoRecebimento>();
        var changedUrl = Input() with { UrlParaRetorno = "https://other.invalid/callback" };
        Assert.Equal(StatusRegistroSnapshotMvp.Conflito,
            (await correlations.RegistrarSnapshotAsync(changedUrl, CancellationToken.None)).Status);
        Assert.Single(provider.GetRequiredService<RepositorioMotorIntegracaoFake>().Integracoes);
        Assert.Equal(0, guardian.Calls);
    }

    [Fact]
    public void PostgreSqlSnapshotSqlPersistsAndRehydratesBothMissingValues()
    {
        var type = typeof(RepositorioCorrelacaoGuardianSapPostgreSql);
        var insert = (string)type.GetField("InsertSnapshotSql", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;
        Assert.Contains("tipo_contrato", insert, StringComparison.Ordinal);
        Assert.Contains("url_para_retorno", insert, StringComparison.Ordinal);
        var map = type.GetMethod("Map", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(map);
    }

    private static ServiceProvider Services(RecordingGuardian guardian, IRepositorioCorrelacaoRecebimento? correlation = null)
    {
        var services = new ServiceCollection();
        if (correlation is not null) services.AddSingleton(correlation);
        GuardianEngineStageAComposition.ConfigurarOffline(services, guardian, Options);
        return services.BuildServiceProvider();
    }

    private static RecebimentoSapCpiMvp Input() => new("RECEBIMENTO", "FLX_RECEBIMENTO", "MAT-1",
        "BP-E", "BP-T", "CONTRATO-1", "DOC-001", "COMPRA", "3011", "ENTRADA", "NF", "PO-1", 1, null);

    private static ClaimIntegracaoMotor Claim() => new(Guid.NewGuid(), Guid.NewGuid(), "DOC-001", "SAP",
        "GUARDIAN", GuardianPreCadastroMotorWork.Operacao, "DOC-001",
        GuardianPreCadastroMotorWork.Fingerprint("DOC-001"), 1, "STAGE-A", DateTimeOffset.UtcNow.AddMinutes(1));

    private sealed class RecordingGuardian : IPreCadastroGuardianSet26Gateway
    {
        public int Calls { get; private set; }
        public PreCadastroRecebimentoGuardianSet26? LastRequest { get; private set; }
        public Action? BeforeSend { get; set; }

        public Task<ProtocoloGuardianSet26> EnviarAsync(PreCadastroRecebimentoGuardianSet26 request, CancellationToken ct)
        {
            BeforeSend?.Invoke();
            Calls++;
            LastRequest = request;
            return Task.FromResult(new ProtocoloGuardianSet26("12345", Created));
        }
    }

    private sealed class FailingProtocolRepository(RepositorioCorrelacaoRecebimentoMemoria inner) : IRepositorioCorrelacaoRecebimento
    {
        public Task<ResultadoRegistroSnapshotMvp> RegistrarSnapshotAsync(RecebimentoSapCpiMvp input, CancellationToken ct) => inner.RegistrarSnapshotAsync(input, ct);
        public Task<CorrelacaoRecebimentoMvp?> ObterPorDocumentoAsync(string document, CancellationToken ct) => inner.ObterPorDocumentoAsync(document, ct);
        public Task<ResultadoAssociacaoContratoAcmMvp> AssociarContratoAcmAsync(string document, string acm, CancellationToken ct) => inner.AssociarContratoAcmAsync(document, acm, ct);
        public Task<ResultadoAssociacaoProtocoloMvp> AssociarProtocoloAsync(string document, string protocol, CancellationToken ct, DateTimeOffset createdAt) => throw new InvalidOperationException("Synthetic failure after Guardian success.");
        public Task<CorrelacaoRecebimentoMvp?> ObterPorProtocoloAsync(string protocol, CancellationToken ct) => inner.ObterPorProtocoloAsync(protocol, ct);
        public Task<ResultadoRegistroCallbackMvp> RegistrarCallbackAsync(CallbackGuardianMvp callback, CancellationToken ct) => inner.RegistrarCallbackAsync(callback, ct);
    }

    private sealed class PartialProtocolRepository(RepositorioCorrelacaoRecebimentoMemoria inner) : IRepositorioCorrelacaoRecebimento
    {
        public Task<ResultadoRegistroSnapshotMvp> RegistrarSnapshotAsync(RecebimentoSapCpiMvp input, CancellationToken ct) => inner.RegistrarSnapshotAsync(input, ct);
        public async Task<CorrelacaoRecebimentoMvp?> ObterPorDocumentoAsync(string document, CancellationToken ct)
        {
            var correlation = await inner.ObterPorDocumentoAsync(document, ct);
            return correlation is null ? null : correlation with { ProtocoloGuardian = "12345", ProtocoloCriadoEm = null };
        }
        public Task<ResultadoAssociacaoContratoAcmMvp> AssociarContratoAcmAsync(string document, string acm, CancellationToken ct) => inner.AssociarContratoAcmAsync(document, acm, ct);
        public Task<ResultadoAssociacaoProtocoloMvp> AssociarProtocoloAsync(string document, string protocol, CancellationToken ct, DateTimeOffset createdAt) => inner.AssociarProtocoloAsync(document, protocol, ct, createdAt);
        public Task<CorrelacaoRecebimentoMvp?> ObterPorProtocoloAsync(string protocol, CancellationToken ct) => inner.ObterPorProtocoloAsync(protocol, ct);
        public Task<ResultadoRegistroCallbackMvp> RegistrarCallbackAsync(CallbackGuardianMvp callback, CancellationToken ct) => inner.RegistrarCallbackAsync(callback, ct);
    }
}
