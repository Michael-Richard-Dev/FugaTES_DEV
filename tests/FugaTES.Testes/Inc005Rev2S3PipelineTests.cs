using System.Security.Cryptography;
using System.Text;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using Xunit;

namespace FugaTES.Testes;

// INC005 GATE 005-B REV2 - Estagio S3.
// Pipeline DETECTAR -> REGISTRAR -> VALIDAR -> CLAIM ESPECIFICO, elegibilidade temporal e TOCTOU.
public sealed class Inc005Rev2S3PipelineTests
{
    private static readonly DateTimeOffset Instante = new(2099, 1, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly IdentidadeWorkerIntegracao Worker = new("worker-S3");

    // ---- DETECTAR / REGISTRAR ----

    [Fact]
    public async Task DetectarNovo_RegistraUmaIntegracaoPendente()
    {
        var repo = new RepositorioMotorIntegracaoFake();

        var resultado = await repo.RegistrarCandidatoAsync(Candidato(), Instante, CancellationToken.None);

        Assert.Equal(ResultadoRegistroIntegracao.Criada, resultado);
        var integracao = Assert.Single(repo.Integracoes);
        Assert.Equal(EstadoIntegracao.Pendente, integracao.Estado);
        Assert.Equal(TipoDisparoPendente.Automatico, integracao.TipoDisparoPendente);
        Assert.Equal(Instante, integracao.ProximaTentativaEm);
    }

    [Fact]
    public async Task DetectarDuplicado_NaoCriaSegundaIntegracao()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var candidato = Candidato();

        Assert.Equal(ResultadoRegistroIntegracao.Criada, await repo.RegistrarCandidatoAsync(candidato, Instante, CancellationToken.None));
        Assert.Equal(ResultadoRegistroIntegracao.JaExistente, await repo.RegistrarCandidatoAsync(candidato, Instante, CancellationToken.None));

        Assert.Single(repo.Integracoes);
    }

    [Fact]
    public async Task DetectarDuplicado_NaoResetaEstadoExistente()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var candidato = Candidato();
        await repo.RegistrarCandidatoAsync(candidato, Instante, CancellationToken.None);
        var claim = await repo.ClaimarPrimeiroAsync(Worker, TimeSpan.FromMinutes(1), Instante, CancellationToken.None);
        Assert.NotNull(claim);

        Assert.Equal(ResultadoRegistroIntegracao.JaExistente, await repo.RegistrarCandidatoAsync(candidato, Instante.AddMinutes(1), CancellationToken.None));

        var integracao = Assert.Single(repo.Integracoes);
        Assert.Equal(EstadoIntegracao.EmProcessamento, integracao.Estado);
        Assert.Equal(claim!.FencingToken, integracao.FencingToken);
    }

    [Fact]
    public async Task Registrar_PreservaContextoDoCandidato()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var candidato = new CandidatoIntegracao("TOLEDO", "SAP", "CONTRATO", "CHAVE-XYZ", ValidFingerprint(), "CORR-XYZ");

        await repo.RegistrarCandidatoAsync(candidato, Instante, CancellationToken.None);

        var integracao = Assert.Single(repo.Integracoes);
        Assert.Equal("TOLEDO", integracao.Origem);
        Assert.Equal("SAP", integracao.Destino);
        Assert.Equal("CONTRATO", integracao.Operacao);
        Assert.Equal("CHAVE-XYZ", integracao.ChaveNegocio);
        Assert.Equal(candidato.FingerprintSha256, integracao.FingerprintSha256);
        Assert.Equal("CORR-XYZ", integracao.CorrelationId);
    }

    [Fact]
    public async Task Fingerprint_NaoEhRecalculado()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        const string fingerprintFornecido = "FINGERPRINT-DETECTOR-NAO-RECALCULA";
        var candidato = Candidato() with { FingerprintSha256 = fingerprintFornecido };

        await repo.RegistrarCandidatoAsync(candidato, Instante, CancellationToken.None);

        Assert.Equal(fingerprintFornecido, repo.Integracoes.Single().FingerprintSha256);
    }

    // ---- Elegibilidade temporal ----

    [Fact]
    public async Task RetryAntesDoPrazo_NaoSelecionaNemClaima()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integ(EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, Instante.AddMinutes(10)));

        var elegiveis = await repo.SelecionarCandidatosPreClaimAsync(Instante, CancellationToken.None);
        var claim = await repo.ClaimarPrimeiroAsync(Worker, TimeSpan.FromMinutes(1), Instante, CancellationToken.None);

        Assert.Empty(elegiveis);
        Assert.Null(claim);
    }

    [Fact]
    public async Task RetryNoPrazo_PodeProsseguir()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integ(EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, Instante));

        var elegiveis = await repo.SelecionarCandidatosPreClaimAsync(Instante, CancellationToken.None);

        Assert.Single(elegiveis);
    }

    // ---- Validacao pre-claim ----

    [Fact]
    public async Task ValidacaoAprovada_PermiteClaimEspecifico()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var alvo = Integ(EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, Instante);
        repo.Semear(alvo);
        Assert.True(new ValidadorGenericoIntegracao().Validar(alvo).Aprovada);

        var claim = await repo.ClaimarEspecificamenteAsync(alvo.Id, alvo.Estado, alvo.TipoDisparoPendente, alvo.FingerprintSha256, Worker, TimeSpan.FromMinutes(1), Instante, CancellationToken.None);

        Assert.NotNull(claim);
        Assert.Equal(EstadoIntegracao.EmProcessamento, repo.Integracoes.Single().Estado);
        Assert.Single(repo.Tentativas);
    }

    [Fact]
    public async Task ValidacaoReprovada_PreservaEstadoNaoClaimaNaoCriaTentativaNaoIncrementaFencingNaoChamaProcessador()
    {
        var (motor, repo, processador) = MotorCom(Integ(EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, Instante, chave: "", fencing: 7));

        var ciclo = await motor.ExecutarUmCicloAsync(CancellationToken.None);

        var integracao = repo.Integracoes.Single();
        Assert.Equal(CicloMotorIntegracaoResultado.ValidacaoReprovada, ciclo);
        Assert.Equal(EstadoIntegracao.Pendente, integracao.Estado);
        Assert.Equal(TipoDisparoPendente.Automatico, integracao.TipoDisparoPendente);
        Assert.Equal(Instante, integracao.ProximaTentativaEm);
        Assert.Equal(7, integracao.FencingToken);
        Assert.Empty(repo.Tentativas);
        Assert.Equal(0, processador.QuantidadeChamadas);
    }

    [Fact]
    public async Task ValidacaoReprovada_RegistraAuditoriaSanitizada()
    {
        var (motor, repo, _) = MotorCom(Integ(EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, Instante, chave: ""));
        var id = repo.Integracoes.Single().Id;

        await motor.ExecutarUmCicloAsync(CancellationToken.None);

        var evento = repo.Auditorias.Single(e => e.Evento == "VALIDACAO_REPROVADA");
        Assert.Equal(id, evento.IntegracaoId);
        Assert.False(string.IsNullOrWhiteSpace(evento.Diagnostico));
        Assert.Equal("chave-negocio-vazia", evento.Diagnostico);
    }

    [Fact]
    public void FalhaValidacao_NaoExisteEmResultadoProcessamentoExterno()
    {
        Assert.DoesNotContain("FalhaValidacao", Enum.GetNames<ClassificacaoFalhaIntegracao>());
    }

    // ---- Claim especifico / TOCTOU ----

    [Fact]
    public async Task ClaimEspecifico_NaoTrocaIntegracaoValidada()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var a = Integ(EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, Instante, id: new Guid("00000000-0000-0000-0000-0000000000AA"));
        var b = Integ(EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, Instante, id: new Guid("00000000-0000-0000-0000-0000000000BB"));
        repo.Semear(a);
        repo.Semear(b);

        var claim = await repo.ClaimarEspecificamenteAsync(a.Id, a.Estado, a.TipoDisparoPendente, a.FingerprintSha256, Worker, TimeSpan.FromMinutes(1), Instante, CancellationToken.None);

        Assert.Equal(a.Id, claim!.IntegracaoId);
        Assert.Equal(EstadoIntegracao.Pendente, repo.Integracoes.Single(i => i.Id == b.Id).Estado);
    }

    [Fact]
    public async Task ClaimEspecifico_TOCTOU_EstadoMudou_Rejeita()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var alvo = Integ(EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, Instante);
        repo.Semear(alvo);
        // Outro worker venceu a corrida: estado agora EM_PROCESSAMENTO.
        await repo.ClaimarPrimeiroAsync(new IdentidadeWorkerIntegracao("outro"), TimeSpan.FromMinutes(5), Instante, CancellationToken.None);

        var claim = await repo.ClaimarEspecificamenteAsync(alvo.Id, EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, alvo.FingerprintSha256, Worker, TimeSpan.FromMinutes(1), Instante, CancellationToken.None);

        Assert.Null(claim);
    }

    [Fact]
    public async Task ClaimEspecifico_TOCTOU_DisparoMudou_Rejeita()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var alvo = Integ(EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, Instante);
        repo.Semear(alvo);
        await repo.IntegrarAgoraAsync(alvo.Id, Instante, "corr", CancellationToken.None); // tipo_disparo -> IntegrarAgora

        var claim = await repo.ClaimarEspecificamenteAsync(alvo.Id, EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, alvo.FingerprintSha256, Worker, TimeSpan.FromMinutes(1), Instante, CancellationToken.None);

        Assert.Null(claim);
    }

    [Fact]
    public async Task ClaimEspecifico_TOCTOU_AgendaMudou_Rejeita()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        // Agenda no futuro relativa ao instante do claim: revalidacao temporal rejeita.
        var alvo = Integ(EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, Instante.AddMinutes(10));
        repo.Semear(alvo);

        var claim = await repo.ClaimarEspecificamenteAsync(alvo.Id, alvo.Estado, alvo.TipoDisparoPendente, alvo.FingerprintSha256, Worker, TimeSpan.FromMinutes(1), Instante, CancellationToken.None);

        Assert.Null(claim);
    }

    [Fact]
    public async Task ClaimEspecifico_TOCTOU_PausaCriada_Rejeita()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var alvo = Integ(EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, Instante, origem: "SAP");
        repo.Semear(alvo);
        await repo.CriarPausaAsync(new PausaProcessamentoMotor(Guid.NewGuid(), EscopoPausaProcessamento.SistemaExterno, "SAP", null, true, Instante, null), "corr", CancellationToken.None);

        var claim = await repo.ClaimarEspecificamenteAsync(alvo.Id, alvo.Estado, alvo.TipoDisparoPendente, alvo.FingerprintSha256, Worker, TimeSpan.FromMinutes(1), Instante, CancellationToken.None);

        Assert.Null(claim);
    }

    [Fact]
    public async Task ClaimContexto_PreservaOrigemDestinoOperacaoChaveFingerprintCorrelation()
    {
        var alvo = Integ(EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, Instante, origem: "SAP", destino: "TOLEDO", operacao: "PRECADASTRO", chave: "CHAVE-CTX", correlation: "CORR-CTX");
        var (motor, _, processador) = MotorCom(alvo);

        await motor.ExecutarUmCicloAsync(CancellationToken.None);

        var claim = Assert.Single(processador.Chamadas);
        Assert.Equal("SAP", claim.Origem);
        Assert.Equal("TOLEDO", claim.Destino);
        Assert.Equal("PRECADASTRO", claim.Operacao);
        Assert.Equal("CHAVE-CTX", claim.ChaveNegocio);
        Assert.Equal(alvo.FingerprintSha256, claim.FingerprintSha256);
        Assert.Equal("CORR-CTX", claim.CorrelationId);
    }

    // ---- Reprocessar / Integrar Agora ----

    [Fact]
    public async Task Reprocessar_TornaElegivelAgora()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var alvo = Integ(EstadoIntegracao.AguardandoAcaoManual, null, null);
        repo.Semear(alvo);

        Assert.True(await repo.ReprocessarAsync(alvo.Id, Instante, "corr", CancellationToken.None));

        var integracao = repo.Integracoes.Single();
        Assert.Equal(EstadoIntegracao.AguardandoReprocessamento, integracao.Estado);
        Assert.Equal(TipoDisparoPendente.Reprocessamento, integracao.TipoDisparoPendente);
        Assert.Equal(Instante, integracao.ProximaTentativaEm);
        Assert.Single(await repo.SelecionarCandidatosPreClaimAsync(Instante, CancellationToken.None));
    }

    [Fact]
    public async Task Reprocessar_ContextoInvalido_NaoBypassaValidacao()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var alvo = Integ(EstadoIntegracao.AguardandoAcaoManual, null, null, chave: "");
        repo.Semear(alvo);
        await repo.ReprocessarAsync(alvo.Id, Instante, "corr", CancellationToken.None);
        var (motor, processador) = MotorPara(repo);

        var ciclo = await motor.ExecutarUmCicloAsync(CancellationToken.None);

        Assert.Equal(CicloMotorIntegracaoResultado.ValidacaoReprovada, ciclo);
        Assert.Empty(repo.Tentativas);
        Assert.Equal(0, processador.QuantidadeChamadas);
    }

    [Fact]
    public async Task IntegrarAgora_AntecipaAgendaFutura()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var alvo = Integ(EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, Instante.AddHours(5));
        repo.Semear(alvo);
        Assert.Empty(await repo.SelecionarCandidatosPreClaimAsync(Instante, CancellationToken.None));

        Assert.True(await repo.IntegrarAgoraAsync(alvo.Id, Instante, "corr", CancellationToken.None));

        var integracao = repo.Integracoes.Single();
        Assert.Equal(Instante, integracao.ProximaTentativaEm);
        Assert.Equal(TipoDisparoPendente.IntegrarAgora, integracao.TipoDisparoPendente);
        Assert.Single(await repo.SelecionarCandidatosPreClaimAsync(Instante, CancellationToken.None));
    }

    [Fact]
    public async Task IntegrarAgora_ContextoInvalido_NaoBypassaValidacao()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var alvo = Integ(EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, Instante, chave: "");
        repo.Semear(alvo);
        await repo.IntegrarAgoraAsync(alvo.Id, Instante, "corr", CancellationToken.None);
        var (motor, processador) = MotorPara(repo);

        var ciclo = await motor.ExecutarUmCicloAsync(CancellationToken.None);

        Assert.Equal(CicloMotorIntegracaoResultado.ValidacaoReprovada, ciclo);
        Assert.Empty(repo.Tentativas);
        Assert.Equal(0, processador.QuantidadeChamadas);
    }

    // ---- Helpers ----

    private static (MotorIntegracaoServico Motor, RepositorioMotorIntegracaoFake Repo, ProcessadorExternoIntegracaoFake Processador) MotorCom(IntegracaoMotor semente)
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(semente);
        var (motor, processador) = MotorPara(repo);
        return (motor, repo, processador);
    }

    private static (MotorIntegracaoServico Motor, ProcessadorExternoIntegracaoFake Processador) MotorPara(RepositorioMotorIntegracaoFake repo)
    {
        var processador = new ProcessadorExternoIntegracaoFake();
        var motor = new MotorIntegracaoServico(
            repo,
            processador,
            new DetectorCandidatosIntegracaoFake(),
            new ValidadorGenericoIntegracao(),
            new IdentidadeFixaProvider("worker-S3"),
            new PoliticaTimeoutFixa(TimeSpan.FromSeconds(30)),
            new TempoFixo(Instante),
            Opcoes());
        return (motor, processador);
    }

    private static MotorIntegracaoOptions Opcoes()
    {
        return new MotorIntegracaoOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(1),
            LeaseRenewInterval = TimeSpan.FromSeconds(20),
            RetryBackoff = TimeSpan.FromMinutes(5),
        };
    }

    private static CandidatoIntegracao Candidato()
    {
        return new CandidatoIntegracao("SAP", "TOLEDO", "PRECADASTRO", "CHAVE-1", ValidFingerprint(), "CORR-1");
    }

    private static IntegracaoMotor Integ(
        EstadoIntegracao estado,
        TipoDisparoPendente? tipo,
        DateTimeOffset? proximaTentativaEm,
        string origem = "SAP",
        string destino = "TOLEDO",
        string operacao = "PRECADASTRO",
        string chave = "CHAVE-1",
        string correlation = "CORR-1",
        Guid? id = null,
        long fencing = 0)
    {
        var idFinal = id ?? Guid.NewGuid();
        return new IntegracaoMotor(
            idFinal,
            origem,
            destino,
            operacao,
            chave,
            ValidFingerprint(idFinal),
            correlation,
            estado,
            tipo,
            proximaTentativaEm,
            fencing,
            null,
            null,
            FaseEnvioIntegracao.NaoIniciado,
            null);
    }

    private static string ValidFingerprint(Guid? semente = null)
    {
        var origem = (semente ?? Guid.NewGuid()).ToString("N");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origem)));
    }

    private sealed class PoliticaTimeoutFixa : IPoliticaTimeoutProcessamentoIntegracao
    {
        private readonly TimeSpan timeout;

        public PoliticaTimeoutFixa(TimeSpan timeout)
        {
            this.timeout = timeout;
        }

        public TimeSpan ObterTimeout(ClaimIntegracaoMotor claim)
        {
            return timeout;
        }
    }

    private sealed class IdentidadeFixaProvider : IIdentidadeWorkerProvider
    {
        private readonly IdentidadeWorkerIntegracao identidade;

        public IdentidadeFixaProvider(string valor)
        {
            identidade = new IdentidadeWorkerIntegracao(valor);
        }

        public IdentidadeWorkerIntegracao Obter()
        {
            return identidade;
        }
    }

    private sealed class TempoFixo : TimeProvider
    {
        private readonly DateTimeOffset agora;

        public TempoFixo(DateTimeOffset agora)
        {
            this.agora = agora;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return agora;
        }
    }
}
