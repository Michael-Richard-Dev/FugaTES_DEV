using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using Xunit;

namespace FugaTES.Testes;

// INC005 GATE 005-B REV2 - Estagio S2.
// Prova a migracao do modelo (Origem/Destino/Operacao/ChaveNegocio/Fingerprint/CorrelationId)
// e a matriz de pausa corrigida (bidirecional; operacao independe de sistema; duplicidade vigente).
public sealed class Inc005Rev2S2ModeloPausaTests
{
    private static readonly DateTimeOffset Instante = new(2099, 1, 15, 10, 0, 0, TimeSpan.Zero);

    // ---- Modelo ----

    [Fact]
    public void IntegracaoMotor_PreservaCamposDeRoteamento()
    {
        var integracao = Pendente("SAP", "TOLEDO", "PRECADASTRO", chaveNegocio: "CHAVE-001", correlationId: "CORR-001", fingerprint: "FP-001");

        Assert.Equal("SAP", integracao.Origem);
        Assert.Equal("TOLEDO", integracao.Destino);
        Assert.Equal("PRECADASTRO", integracao.Operacao);
        Assert.Equal("CHAVE-001", integracao.ChaveNegocio);
        Assert.Equal("FP-001", integracao.FingerprintSha256);
        Assert.Equal("CORR-001", integracao.CorrelationId);
    }

    [Fact]
    public void IntegracaoMotor_Bidirecional_SemSistemaExternoUnico()
    {
        var sapParaToledo = Pendente("SAP", "TOLEDO", "PRECADASTRO");
        var toledoParaSap = Pendente("TOLEDO", "SAP", "CONTRATO");

        Assert.NotEqual(sapParaToledo.Origem, sapParaToledo.Destino);
        Assert.Equal("SAP", sapParaToledo.Origem);
        Assert.Equal("SAP", toledoParaSap.Destino);
        // Nao ha campo unico "SistemaExterno" no modelo migrado.
        Assert.Null(typeof(IntegracaoMotor).GetProperty("SistemaExterno"));
    }

    // ---- Matriz de escopo (fail-closed) ----

    [Fact]
    public async Task PausaGeral_SemSistemaSemOperacao_Valida()
    {
        Assert.True(await Criar(Pausa(EscopoPausaProcessamento.Geral, null, null)));
    }

    [Fact]
    public async Task PausaGeral_ComSistema_Rejeita()
    {
        Assert.False(await Criar(Pausa(EscopoPausaProcessamento.Geral, "SAP", null)));
    }

    [Fact]
    public async Task PausaGeral_ComOperacao_Rejeita()
    {
        Assert.False(await Criar(Pausa(EscopoPausaProcessamento.Geral, null, "PRECADASTRO")));
    }

    [Fact]
    public async Task PausaSistemaExterno_ComSistema_Valida()
    {
        Assert.True(await Criar(Pausa(EscopoPausaProcessamento.SistemaExterno, "SAP", null)));
    }

    [Fact]
    public async Task PausaSistemaExterno_ComOperacao_Rejeita()
    {
        Assert.False(await Criar(Pausa(EscopoPausaProcessamento.SistemaExterno, "SAP", "PRECADASTRO")));
    }

    [Fact]
    public async Task PausaSistemaExterno_SemSistema_Rejeita()
    {
        Assert.False(await Criar(Pausa(EscopoPausaProcessamento.SistemaExterno, null, null)));
    }

    [Fact]
    public async Task PausaOperacao_ComOperacao_Valida()
    {
        Assert.True(await Criar(Pausa(EscopoPausaProcessamento.Operacao, null, "PRECADASTRO")));
    }

    [Fact]
    public async Task PausaOperacao_ComSistema_Rejeita()
    {
        Assert.False(await Criar(Pausa(EscopoPausaProcessamento.Operacao, "SAP", "PRECADASTRO")));
    }

    [Fact]
    public async Task PausaOperacao_SemOperacao_Rejeita()
    {
        Assert.False(await Criar(Pausa(EscopoPausaProcessamento.Operacao, null, null)));
    }

    // ---- Bloqueio bidirecional e independencia de sistema ----

    [Fact]
    public async Task PausaSistemaExterno_BloqueiaQuandoOrigemCoincide()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Pendente("SAP", "TOLEDO", "PRECADASTRO"));
        Assert.True(await repo.CriarPausaAsync(Pausa(EscopoPausaProcessamento.SistemaExterno, "SAP", null), "corr", CancellationToken.None));

        Assert.Null(await Claimar(repo));
    }

    [Fact]
    public async Task PausaSistemaExterno_BloqueiaQuandoDestinoCoincide()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Pendente("INTERNO", "SAP", "PRECADASTRO"));
        Assert.True(await repo.CriarPausaAsync(Pausa(EscopoPausaProcessamento.SistemaExterno, "SAP", null), "corr", CancellationToken.None));

        Assert.Null(await Claimar(repo));
    }

    [Fact]
    public async Task PausaSistemaExterno_NaoBloqueiaQuandoNenhumCoincide()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Pendente("FAKE_A", "FAKE_B", "PRECADASTRO"));
        Assert.True(await repo.CriarPausaAsync(Pausa(EscopoPausaProcessamento.SistemaExterno, "SAP", null), "corr", CancellationToken.None));

        Assert.NotNull(await Claimar(repo));
    }

    [Fact]
    public async Task PausaOperacao_IndependeDoSistema()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Pendente("FAKE_A", "FAKE_B", "PRECADASTRO"));
        Assert.True(await repo.CriarPausaAsync(Pausa(EscopoPausaProcessamento.Operacao, null, "PRECADASTRO"), "corr", CancellationToken.None));

        Assert.Null(await Claimar(repo));
    }

    // ---- Duplicidade vigente ----

    [Fact]
    public async Task PausaDuplicadaGeral_Rejeita()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        Assert.True(await repo.CriarPausaAsync(Pausa(EscopoPausaProcessamento.Geral, null, null), "corr", CancellationToken.None));
        Assert.False(await repo.CriarPausaAsync(Pausa(EscopoPausaProcessamento.Geral, null, null), "corr", CancellationToken.None));
    }

    [Fact]
    public async Task PausaDuplicadaSistema_Rejeita()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        Assert.True(await repo.CriarPausaAsync(Pausa(EscopoPausaProcessamento.SistemaExterno, "SAP", null), "corr", CancellationToken.None));
        Assert.False(await repo.CriarPausaAsync(Pausa(EscopoPausaProcessamento.SistemaExterno, "SAP", null), "corr", CancellationToken.None));
        // Sistema distinto continua permitido.
        Assert.True(await repo.CriarPausaAsync(Pausa(EscopoPausaProcessamento.SistemaExterno, "TOLEDO", null), "corr", CancellationToken.None));
    }

    [Fact]
    public async Task PausaDuplicadaOperacao_Rejeita()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        Assert.True(await repo.CriarPausaAsync(Pausa(EscopoPausaProcessamento.Operacao, null, "PRECADASTRO"), "corr", CancellationToken.None));
        Assert.False(await repo.CriarPausaAsync(Pausa(EscopoPausaProcessamento.Operacao, null, "PRECADASTRO"), "corr", CancellationToken.None));
        // Operacao distinta continua permitida.
        Assert.True(await repo.CriarPausaAsync(Pausa(EscopoPausaProcessamento.Operacao, null, "CONTRATO"), "corr", CancellationToken.None));
    }

    // ---- Retomada e auditoria global ----

    [Fact]
    public async Task RetomarPausa_LiberaEscopo()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Pendente("SAP", "TOLEDO", "PRECADASTRO"));
        var pausa = Pausa(EscopoPausaProcessamento.SistemaExterno, "SAP", null);
        Assert.True(await repo.CriarPausaAsync(pausa, "corr", CancellationToken.None));
        Assert.Null(await Claimar(repo));

        Assert.True(await repo.RetomarPausaAsync(pausa.Id, Instante, "corr", CancellationToken.None));

        Assert.NotNull(await Claimar(repo));
    }

    [Fact]
    public async Task AuditoriaPausaRetomada_IntegracaoIdNull()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var pausa = Pausa(EscopoPausaProcessamento.Geral, null, null);
        Assert.True(await repo.CriarPausaAsync(pausa, "corr", CancellationToken.None));
        Assert.True(await repo.RetomarPausaAsync(pausa.Id, Instante, "corr", CancellationToken.None));

        Assert.DoesNotContain(repo.Auditorias, evento => evento.IntegracaoId == Guid.Empty);
        Assert.Contains(repo.Auditorias, evento => evento.Evento == "pausa" && evento.IntegracaoId is null);
        Assert.Contains(repo.Auditorias, evento => evento.Evento == "retomada" && evento.IntegracaoId is null);
    }

    // ---- Helpers ----

    private static async Task<bool> Criar(PausaProcessamentoMotor pausa)
    {
        var repo = new RepositorioMotorIntegracaoFake();
        return await repo.CriarPausaAsync(pausa, "corr", CancellationToken.None);
    }

    private static Task<ClaimIntegracaoMotor?> Claimar(RepositorioMotorIntegracaoFake repo)
    {
        return repo.ClaimarPrimeiroAsync(new IdentidadeWorkerIntegracao("worker-S2"), TimeSpan.FromMinutes(1), Instante, CancellationToken.None);
    }

    private static PausaProcessamentoMotor Pausa(EscopoPausaProcessamento escopo, string? sistema, string? operacao)
    {
        return new PausaProcessamentoMotor(Guid.NewGuid(), escopo, sistema, operacao, true, Instante, null);
    }

    private static IntegracaoMotor Pendente(
        string origem,
        string destino,
        string operacao,
        string chaveNegocio = "CHAVE-SINTETICA",
        string correlationId = "CORRELATION-SINTETICA",
        string? fingerprint = null)
    {
        var id = Guid.NewGuid();
        return new IntegracaoMotor(
            id,
            origem,
            destino,
            operacao,
            chaveNegocio,
            fingerprint ?? "FP-" + id.ToString("N"),
            correlationId,
            EstadoIntegracao.Pendente,
            TipoDisparoPendente.Automatico,
            Instante,
            0,
            null,
            null,
            FaseEnvioIntegracao.NaoIniciado,
            null);
    }
}
