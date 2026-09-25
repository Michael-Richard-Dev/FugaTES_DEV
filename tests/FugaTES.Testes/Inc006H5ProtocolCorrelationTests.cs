using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Infraestrutura.Recebimento;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Callback.Http;
using FugaTES.Persistencia.Recebimento;
using System.Reflection;
using System.Text.Json;

namespace FugaTES.Testes;

public sealed class Inc006H5ProtocolCorrelationTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 24, 11, 38, 28, TimeSpan.FromHours(-3));

    [Fact]
    public async Task ProtocolAndCreationTime_ArePersistedAndEquivalentInstantIsIdempotent()
    {
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();
        await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None);

        var first = await repository.AssociarProtocoloAsync("DOC-001", "12345", CancellationToken.None, Created);
        var replay = await repository.AssociarProtocoloAsync("DOC-001", "12345", CancellationToken.None, Created.ToUniversalTime());

        Assert.Equal(StatusAssociacaoProtocoloMvp.Associado, first.Status);
        Assert.Equal(StatusAssociacaoProtocoloMvp.Idempotente, replay.Status);
        Assert.Equal(Created.ToUniversalTime(), replay.Correlacao.ProtocoloCriadoEm);
        Assert.Equal("12345", replay.Correlacao.ProtocoloGuardian);
    }

    [Fact]
    public async Task TimestampPrecision_MatchesPostgreSqlMicrosecondsOnReplay()
    {
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();
        await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None);
        await repository.AssociarProtocoloAsync("DOC-001", "12345", CancellationToken.None, Created.AddTicks(7));

        var replay = await repository.AssociarProtocoloAsync("DOC-001", "12345", CancellationToken.None, Created);

        Assert.Equal(StatusAssociacaoProtocoloMvp.Idempotente, replay.Status);
        Assert.Equal(Created.ToUniversalTime(), replay.Correlacao.ProtocoloCriadoEm);
    }

    [Fact]
    public async Task MissingCreationTime_IsRejectedBeforeAssociation()
    {
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();
        await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.AssociarProtocoloAsync("DOC-001", "12345", CancellationToken.None, default));

        Assert.Null((await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None)).Correlacao.ProtocoloGuardian);
    }

    [Theory]
    [InlineData("99999", 0)]
    [InlineData("12345", 1)]
    public async Task DifferentProtocolOrMateriallyDifferentTime_FailsClosed(string protocol, int additionalMinutes)
    {
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();
        await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None);
        await repository.AssociarContratoAcmAsync("DOC-001", "ACM-1", CancellationToken.None);
        await repository.AssociarProtocoloAsync("DOC-001", "12345", CancellationToken.None, Created);

        var result = await repository.AssociarProtocoloAsync("DOC-001", protocol, CancellationToken.None, Created.AddMinutes(additionalMinutes));

        Assert.Equal(StatusAssociacaoProtocoloMvp.Conflito, result.Status);
        Assert.Equal("12345", result.Correlacao.ProtocoloGuardian);
        Assert.Equal(Created.ToUniversalTime(), result.Correlacao.ProtocoloCriadoEm);
    }

    [Theory]
    [InlineData("12345", false)]
    [InlineData("other", true)]
    public async Task ProtocolCloud_IsReconciledBeforeSap(string protocolCloud, bool conflict)
    {
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();
        await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None);
        await repository.AssociarContratoAcmAsync("DOC-001", "ACM-1", CancellationToken.None);
        await repository.AssociarProtocoloAsync("DOC-001", "12345", CancellationToken.None, Created);
        var sap = new PesagemSapMvpGatewayFake();
        var sut = new ProcessarCallbackGuardianMvpCasoDeUso(repository, new RegistroCallbackPesagemSapMemoria(repository, new FugaTES.Infraestrutura.MotorIntegracao.RepositorioMotorIntegracaoFake()));
        var callback = new CallbackGuardianMvp("12345", "TICKET-1", 1200m, 200m, 1000m, "CONCLUIDO", protocolCloud);

        if (conflict)
            await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ExecutarAsync(callback, CancellationToken.None));
        else
        {
            var result = await sut.ExecutarAsync(callback, CancellationToken.None);
            Assert.Equal(StatusCallbackGuardianMvp.Aceito, result.Status);
            Assert.Equal("12345", (await repository.ObterPorProtocoloAsync("12345", CancellationToken.None))!.ProtocoloGuardian);
        }
        Assert.Equal(0, sap.QuantidadeChamadas);
    }

    [Fact]
    public async Task PostSuccessWithPersistenceFailure_IsClassifiedIndeterminate()
    {
        var inner = new RepositorioCorrelacaoRecebimentoMemoria();
        var repository = new FailingProtocolRepository(inner);
        var guardian = new RecordingGuardian();
        var motor = new RepositorioMotorIntegracaoFake();
        var sut = new ProcessarRecebimentoMvpCasoDeUso(
            new RegistrarPreCadastroGuardianMotorCasoDeUso(repository, motor,
                new GuardianSet26Options("/pre-cadastro", "/callback")));

        var accepted = await sut.ExecutarAsync(Input(), CancellationToken.None);
        Assert.True(accepted.TrabalhoRegistrado);
        Assert.Equal(0, guardian.Calls);
        var processor = new ProcessadorPreCadastroGuardianMotor(repository, guardian);
        var claim = new ClaimIntegracaoMotor(Guid.NewGuid(), Guid.NewGuid(), "DOC-001", "SAP", "GUARDIAN",
            GuardianPreCadastroMotorWork.Operacao, "DOC-001", GuardianPreCadastroMotorWork.Fingerprint("DOC-001"),
            1, "TEST", DateTimeOffset.UtcNow.AddMinutes(1));
        var result = await processor.ProcessarAsync(claim, CancellationToken.None);
        Assert.Equal(ClassificacaoFalhaIntegracao.ResultadoIndeterminado, result.Falha);
        Assert.Equal(1, guardian.Calls);
    }

    [Fact]
    public void VoTicketProtocolCloud_IsMappedAsCorrelationEvidence()
    {
        const string json = """
            {"protocoloGuardian":"12345","sequencial":"TICKET-1","pesoBruto":1200,"tara":200,"pesoLiquido":1000,"estado":"CONCLUIDO","VO_TICKET":{"ProtocoloCloud":"12345"}}
            """;
        var callback = JsonSerializer.Deserialize<GuardianCallbackHttpDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Mapear();
        Assert.Equal("12345", callback.ProtocoloCloud);
    }

    [Fact]
    public void PostgreSqlAssociation_IsAtomicAndParameterizedForProtocolAndTimestamp()
    {
        var sql = (string)typeof(RepositorioCorrelacaoGuardianSapPostgreSql)
            .GetField("AssociateProtocolSql", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        Assert.Contains("protocolo_guardian = @protocolo_guardian", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("protocolo_criado_em = @protocolo_criado_em", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("protocolo_guardian is null and protocolo_criado_em is null", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void H5Sql_IsIncrementalReadOnlyValidatedAndRollbackGuardsData()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FugaTES.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var root = Path.Combine(directory.FullName, "docs", "Incrementos", "006");
        var migration = File.ReadAllText(Path.Combine(root, "MIGRATION_006H5_PROTOCOLO_GUARDIAN_CRIADO_EM.sql"));
        var validation = File.ReadAllText(Path.Combine(root, "VALIDATION_006H5_PROTOCOLO_GUARDIAN_CRIADO_EM.sql"));
        var rollback = File.ReadAllText(Path.Combine(root, "ROLLBACK_006H5_PROTOCOLO_GUARDIAN_CRIADO_EM.sql"));
        Assert.Contains("add column protocolo_criado_em timestamptz null", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("response.dataCadastro", migration, StringComparison.Ordinal);
        Assert.Contains("begin transaction read only", validation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column_count_23", validation, StringComparison.Ordinal);
        Assert.Contains("add column tipo_contrato text null", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("add column url_para_retorno text null", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tipo_contrato_type_and_nullability", validation, StringComparison.Ordinal);
        Assert.Contains("url_para_retorno_type_and_nullability", validation, StringComparison.Ordinal);
        Assert.Contains("rollback;", validation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("where protocolo_criado_em is not null", rollback, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tipo_contrato is not null or url_para_retorno is not null", rollback, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("drop column protocolo_criado_em", rollback, StringComparison.OrdinalIgnoreCase);
    }

    private static RecebimentoSapCpiMvp Input() => new("RECEBIMENTO", "FLX_RECEBIMENTO", "MAT-1",
        "BP-E", "BP-T", "CONTRATO-1", "DOC-001", "COMPRA", "3011", "ENTRADA", "NF", "PO-1", 1, "ACM-1");

    private sealed class RecordingGuardian : IPreCadastroGuardianSet26Gateway
    {
        public int Calls { get; private set; }
        public Task<ProtocoloGuardianSet26> EnviarAsync(PreCadastroRecebimentoGuardianSet26 preCadastro, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ProtocoloGuardianSet26("12345", Created));
        }
    }

    private sealed class FailingProtocolRepository(RepositorioCorrelacaoRecebimentoMemoria inner) : IRepositorioCorrelacaoRecebimento
    {
        public Task<ResultadoRegistroSnapshotMvp> RegistrarSnapshotAsync(RecebimentoSapCpiMvp input, CancellationToken ct) => inner.RegistrarSnapshotAsync(input, ct);
        public Task<CorrelacaoRecebimentoMvp?> ObterPorDocumentoAsync(string document, CancellationToken ct) => inner.ObterPorDocumentoAsync(document, ct);
        public Task<ResultadoAssociacaoContratoAcmMvp> AssociarContratoAcmAsync(string document, string acm, CancellationToken ct) => inner.AssociarContratoAcmAsync(document, acm, ct);
        public Task<ResultadoAssociacaoProtocoloMvp> AssociarProtocoloAsync(string document, string protocol, CancellationToken ct, DateTimeOffset createdAt) => throw new InvalidOperationException("Synthetic persistence failure after external success.");
        public Task<CorrelacaoRecebimentoMvp?> ObterPorProtocoloAsync(string protocol, CancellationToken ct) => inner.ObterPorProtocoloAsync(protocol, ct);
        public Task<ResultadoRegistroCallbackMvp> RegistrarCallbackAsync(CallbackGuardianMvp callback, CancellationToken ct) => inner.RegistrarCallbackAsync(callback, ct);
    }
}
