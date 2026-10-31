using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.HmlFixture;

namespace FugaTES.Testes;

public sealed class HmlGuardianFixtureTests
{
    [Theory]
    [InlineData(null, "HML", "12345")]
    [InlineData("false", "HML", "12345")]
    [InlineData("true", "Production", "12345")]
    [InlineData("true", "HML", null)]
    [InlineData("true", "HML", "not-a-number")]
    public async Task Guards_fail_before_repository_calls(string? enabled, string environment, string? protocol)
    {
        var repo = new FakeRepository();
        var diagnostics = new FakeDiagnostics();
        await Assert.ThrowsAsync<InvalidOperationException>(() => FixtureRunner.ExecuteAsync(
            enabled, environment, protocol, Input(), repo, diagnostics, CancellationToken.None));
        Assert.Equal(0, repo.Calls);
        Assert.Equal(0, diagnostics.Calls);
    }

    [Fact]
    public async Task Collision_fails_closed_without_parent_creation()
    {
        var repo = new FakeRepository { ExistingProtocol = Snapshot() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => FixtureRunner.ExecuteAsync(
            "true", "HML", "12345", Input(), repo, new FakeDiagnostics(), CancellationToken.None));
        Assert.Equal(0, repo.Created);
        Assert.Equal(0, repo.Associated);
    }

    [Fact]
    public async Task Existing_document_fails_closed_without_parent_creation()
    {
        var repo = new FakeRepository { ExistingDocument = Snapshot() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => FixtureRunner.ExecuteAsync(
            "true", "HML", "12345", Input(), repo, new FakeDiagnostics(), CancellationToken.None));
        Assert.Equal(0, repo.Created);
        Assert.Equal(0, repo.Associated);
    }

    [Fact]
    public async Task Unmarked_snapshot_identity_fails_before_repository_calls()
    {
        var repo = new FakeRepository();
        await Assert.ThrowsAsync<InvalidOperationException>(() => FixtureRunner.ExecuteAsync(
            "true", "HML", "12345", Input() with { Material = "LIVE-MAT" },
            repo, new FakeDiagnostics(), CancellationToken.None));
        Assert.Equal(0, repo.Calls);
    }

    [Fact]
    public async Task Creates_one_parent_and_associates_once_without_callback_or_integration()
    {
        var repo = new FakeRepository();
        var diagnostics = new FakeDiagnostics();
        var result = await FixtureRunner.ExecuteAsync("true", "HML", "12345", Input(), repo,
            diagnostics, CancellationToken.None);
        Assert.Equal(1, repo.Created);
        Assert.Equal(1, repo.Associated);
        Assert.Equal(0, repo.Callbacks);
        Assert.Equal(0, diagnostics.LedgerCount);
        Assert.Equal(1, diagnostics.Calls);
        Assert.True(result.TicketGuardianIsNull);
        Assert.Equal("12345", result.ProtocoloGuardianTeste);
    }

    [Fact]
    public async Task Protocol_association_failure_does_not_attempt_manual_cleanup()
    {
        var repo = new FakeRepository { AssociationConflict = true };
        var diagnostics = new FakeDiagnostics();
        await Assert.ThrowsAsync<FixtureAssociationException>(() => FixtureRunner.ExecuteAsync(
            "true", "HML", "12345", Input(), repo, diagnostics, CancellationToken.None));
        Assert.Equal(1, repo.Created);
        Assert.Equal(1, repo.Associated);
        Assert.Equal(0, repo.Callbacks);
        Assert.Equal(0, diagnostics.Calls);
    }

    private static RecebimentoSapCpiMvp Input() => new("RECEBIMENTO", "FLX_RECEBIMENTO",
        "HML-MAT", "HML-EMISSOR", "HML-TRANSPORTADORA", "HML-CONTRATO",
        "HML-DOC", "COMPRA", "HML-CENTRO", "ENTRADA", "NF", "HML-PO", 1, null);

    private static CorrelacaoRecebimentoMvp Snapshot() =>
        new("HML-DOC", "12345", Input(), Estado: EstadoCorrelacaoGuardianSap.AguardandoTicket);

    private sealed class FakeDiagnostics : IFixtureReadback
    {
        public int Calls { get; private set; }
        public int LedgerCount => 0;
        public Task<FixtureEvidence> ReadAsync(string protocol, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new FixtureEvidence(Guid.NewGuid(), 1, LedgerCount));
        }
    }

    private sealed class FakeRepository : IRepositorioCorrelacaoRecebimento
    {
        public int Calls { get; private set; }
        public int Created { get; private set; }
        public int Associated { get; private set; }
        public int Callbacks { get; private set; }
        public bool AssociationConflict { get; init; }
        public CorrelacaoRecebimentoMvp? ExistingProtocol { get; init; }
        public CorrelacaoRecebimentoMvp? ExistingDocument { get; init; }
        private CorrelacaoRecebimentoMvp? current;

        public Task<CorrelacaoRecebimentoMvp?> ObterPorProtocoloAsync(string protocol, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(ExistingProtocol ?? (current?.ProtocoloGuardian == protocol ? current : null));
        }
        public Task<CorrelacaoRecebimentoMvp?> ObterPorDocumentoAsync(string document, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(ExistingDocument ?? (current?.DocumentoSap == document ? current : null));
        }
        public Task<ResultadoRegistroSnapshotMvp> RegistrarSnapshotAsync(RecebimentoSapCpiMvp input, CancellationToken token)
        {
            Calls++;
            Created++;
            current = new(input.DocumentoLogisticoReferencia, null, input);
            return Task.FromResult(new ResultadoRegistroSnapshotMvp(StatusRegistroSnapshotMvp.Criada, current));
        }
        public Task<ResultadoAssociacaoProtocoloMvp> AssociarProtocoloAsync(string document, string protocol, CancellationToken token, DateTimeOffset created)
        {
            Calls++;
            Associated++;
            current = current! with { ProtocoloGuardian = protocol, Estado = EstadoCorrelacaoGuardianSap.AguardandoTicket };
            return Task.FromResult(new ResultadoAssociacaoProtocoloMvp(
                AssociationConflict ? StatusAssociacaoProtocoloMvp.Conflito : StatusAssociacaoProtocoloMvp.Associado, current));
        }
        public Task<ResultadoAssociacaoContratoAcmMvp> AssociarContratoAcmAsync(string document, string acm, CancellationToken token) => throw new NotSupportedException();
        public Task<ResultadoRegistroCallbackMvp> RegistrarCallbackAsync(CallbackGuardianMvp callback, CancellationToken token)
        {
            Callbacks++;
            throw new NotSupportedException();
        }
    }
}
