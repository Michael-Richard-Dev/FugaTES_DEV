using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using Xunit;

namespace FugaTES.Testes;

// INC005 GATE 005-B REV2 - Estagio S1.
// Prova apenas as fundacoes aditivas: detector/candidato, resultado de validacao,
// desfecho de tentativa e saneamento da auditoria global. Nao exercita pipeline.
public sealed class Inc005Rev2S1ContratosTests
{
    private static readonly DateTimeOffset Instante = new(2099, 1, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DetectorFakeVazio_RetornaZero()
    {
        var detector = new DetectorCandidatosIntegracaoFake();

        var candidatos = await detector.DetectarAsync(CancellationToken.None);

        Assert.Empty(candidatos);
    }

    [Fact]
    public async Task DetectorFake_RetornaCandidatosNaOrdemContratada()
    {
        var detector = new DetectorCandidatosIntegracaoFake();
        detector.EnfileirarVarios(Candidato("A"), Candidato("B"), Candidato("C"));

        var candidatos = await detector.DetectarAsync(CancellationToken.None);

        Assert.Equal(new[] { "A", "B", "C" }, candidatos.Select(candidato => candidato.ChaveNegocio).ToArray());
    }

    [Fact]
    public async Task Candidato_PreservaCamposExatos()
    {
        var detector = new DetectorCandidatosIntegracaoFake();
        var esperado = new CandidatoIntegracao("SAP", "TOLEDO", "PRECADASTRO", "CHAVE-001", "FINGERPRINT-SINTETICO", "CORRELATION-001");
        detector.Enfileirar(esperado);

        var candidato = Assert.Single(await detector.DetectarAsync(CancellationToken.None));

        Assert.Equal("SAP", candidato.Origem);
        Assert.Equal("TOLEDO", candidato.Destino);
        Assert.Equal("PRECADASTRO", candidato.Operacao);
        Assert.Equal("CHAVE-001", candidato.ChaveNegocio);
        Assert.Equal("FINGERPRINT-SINTETICO", candidato.FingerprintSha256);
        Assert.Equal("CORRELATION-001", candidato.CorrelationId);
    }

    [Fact]
    public async Task Detector_NaoCalculaFingerprint()
    {
        // Fingerprint deliberadamente nao e um hash: se o detector recalculasse, o valor mudaria.
        const string fingerprintNaoHash = "NAO-E-UM-HASH-123";
        var detector = new DetectorCandidatosIntegracaoFake();
        detector.Enfileirar(Candidato("X") with { FingerprintSha256 = fingerprintNaoHash });

        var candidato = Assert.Single(await detector.DetectarAsync(CancellationToken.None));

        Assert.Equal(fingerprintNaoHash, candidato.FingerprintSha256);
    }

    [Fact]
    public void ResultadoValidacao_AprovadaEReprovadaSeparadas()
    {
        var aprovada = ResultadoValidacaoIntegracao.Aprovar();
        var reprovada = ResultadoValidacaoIntegracao.Reprovar("estrutura-invalida-sanitizada");

        Assert.True(aprovada.Aprovada);
        Assert.Null(aprovada.DiagnosticoSanitizado);
        Assert.False(reprovada.Aprovada);
        Assert.Equal("estrutura-invalida-sanitizada", reprovada.DiagnosticoSanitizado);

        // Tipo distinto de ResultadoProcessamentoExterno: validacao nao e processamento.
        Assert.NotEqual(typeof(ResultadoProcessamentoExterno), typeof(ResultadoValidacaoIntegracao));
    }

    [Fact]
    public void ResultadoValidacao_ReprovarExigeDiagnostico()
    {
        Assert.Throws<ArgumentException>(() => ResultadoValidacaoIntegracao.Reprovar("  "));
    }

    [Fact]
    public void DesfechoTentativa_TemSemanticaS1()
    {
        Assert.Equal(
            new[] { "EmExecucao", "Sucesso", "Falha", "Interrompida", "Indeterminada" },
            Enum.GetNames<DesfechoTentativaIntegracao>());
    }

    [Fact]
    public void AuditoriaGlobal_AceitaIntegracaoIdNull()
    {
        var global = new AuditoriaIntegracaoMotor(Guid.NewGuid(), null, "evento_global", Instante, null, null, null, "CORRELATION-001");

        Assert.Null(global.IntegracaoId);
    }

    [Fact]
    public async Task AuditoriaGlobal_NaoUsaGuidEmptyComoSentinel()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var pausa = new PausaProcessamentoMotor(Guid.NewGuid(), EscopoPausaProcessamento.Geral, null, null, true, Instante, null);

        Assert.True(await repo.CriarPausaAsync(pausa, "CORRELATION-001", CancellationToken.None));
        Assert.True(await repo.RetomarPausaAsync(pausa.Id, Instante, "CORRELATION-001", CancellationToken.None));

        Assert.DoesNotContain(repo.Auditorias, evento => evento.IntegracaoId == Guid.Empty);
        Assert.Contains(repo.Auditorias, evento => evento.Evento == "pausa" && evento.IntegracaoId is null);
        Assert.Contains(repo.Auditorias, evento => evento.Evento == "retomada" && evento.IntegracaoId is null);
    }

    private static CandidatoIntegracao Candidato(string chaveNegocio)
    {
        return new CandidatoIntegracao("SAP", "TOLEDO", "PRECADASTRO", chaveNegocio, "FINGERPRINT-" + chaveNegocio, "CORRELATION-" + chaveNegocio);
    }
}
