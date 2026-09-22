using System.Security.Cryptography;
using System.Text;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using Xunit;

namespace FugaTES.Testes;

// INC005 GATE 005-B REV3 focal: resultado indeterminado unico, claim sem bypass e DDL/SQL documental.
public sealed class Inc005Rev3FocalTests
{
    private static readonly DateTimeOffset Instante = new(2099, 5, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly IdentidadeWorkerIntegracao Worker = new("worker-rev3");

    // ---- F1: RESULTADO INDETERMINADO UNICO ----

    [Fact]
    public async Task ProcessorRetornaIndeterminado_UsaCaminhoDedicado()
    {
        var (motor, repo) = MotorComIndeterminado();

        var ciclo = await motor.ExecutarUmCicloAsync(CancellationToken.None);

        Assert.Equal(CicloMotorIntegracaoResultado.ResultadoIndeterminado, ciclo);
        Assert.Equal(EstadoIntegracao.ResultadoIndeterminado, repo.Integracoes.Single().Estado);
        Assert.Contains(repo.Auditorias, e => e.Evento == "resultado_indeterminado");
        // O caminho generico de resultado conhecido (terminal/retry) nao foi usado.
        Assert.DoesNotContain(repo.Auditorias, e => e.Evento == "terminal" || e.Evento == "retry_agendado");
    }

    [Fact]
    public async Task ProcessorRetornaIndeterminado_FasePermaneceEnvioIniciado()
    {
        var (motor, repo) = MotorComIndeterminado();

        await motor.ExecutarUmCicloAsync(CancellationToken.None);

        Assert.Equal(FaseEnvioIntegracao.EnvioIniciado, repo.Integracoes.Single().FaseEnvio);
    }

    [Fact]
    public async Task ProcessorRetornaIndeterminado_DesfechoIndeterminada()
    {
        var (motor, repo) = MotorComIndeterminado();

        await motor.ExecutarUmCicloAsync(CancellationToken.None);

        Assert.Equal(DesfechoTentativaIntegracao.Indeterminada, repo.Tentativas.Single().Desfecho);
    }

    [Fact]
    public async Task ProcessorRetornaIndeterminado_ResultadoRecebidoEmNull()
    {
        var (motor, repo) = MotorComIndeterminado();

        await motor.ExecutarUmCicloAsync(CancellationToken.None);

        Assert.Null(repo.Tentativas.Single().ResultadoRecebidoEm);
    }

    [Fact]
    public async Task ProcessorRetornaIndeterminado_SemRetry()
    {
        var (motor, repo) = MotorComIndeterminado();

        await motor.ExecutarUmCicloAsync(CancellationToken.None);

        var integracao = repo.Integracoes.Single();
        Assert.Null(integracao.TipoDisparoPendente);
        Assert.Null(integracao.ProximaTentativaEm);
    }

    [Fact]
    public async Task PersistirResultadoGenerico_Indeterminado_Rejeita()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Pendente());
        var claim = await repo.ClaimarPrimeiroAsync(Worker, TimeSpan.FromMinutes(1), Instante, CancellationToken.None);
        await repo.MarcarEnvioIniciadoAsync(claim!.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, Instante, CancellationToken.None);

        var aceito = await repo.PersistirResultadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, new ResultadoProcessamentoExterno(ClassificacaoFalhaIntegracao.ResultadoIndeterminado), Instante, TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.False(aceito);
        Assert.Equal(EstadoIntegracao.EmProcessamento, repo.Integracoes.Single().Estado);
        Assert.Equal(FaseEnvioIntegracao.EnvioIniciado, repo.Integracoes.Single().FaseEnvio);
    }

    [Fact]
    public async Task RegistrarIndeterminadoFencedFalse_RetornaStaleWriter()
    {
        var (motor, repo) = MotorComIndeterminado();
        repo.RejeitarRegistroIndeterminado = true;

        var ciclo = await motor.ExecutarUmCicloAsync(CancellationToken.None);

        Assert.Equal(CicloMotorIntegracaoResultado.StaleWriter, ciclo);
    }

    [Fact]
    public async Task RegistrarIndeterminadoFencedFalse_NaoDeclaraEstadoPersistido()
    {
        var (motor, repo) = MotorComIndeterminado();
        repo.RejeitarRegistroIndeterminado = true;

        await motor.ExecutarUmCicloAsync(CancellationToken.None);

        var integracao = repo.Integracoes.Single();
        Assert.NotEqual(EstadoIntegracao.ResultadoIndeterminado, integracao.Estado);
        Assert.Equal(EstadoIntegracao.EmProcessamento, integracao.Estado);
    }

    // ---- F2: CLAIM SEM BYPASS (prova estrutural do port) ----

    [Fact]
    public void Port_NaoExpoeClaimGenerico_ExpoeClaimEspecifico()
    {
        var tipo = typeof(IMotorIntegracaoRepository);
        Assert.Null(tipo.GetMethod("ClaimarComTentativaAsync"));
        Assert.NotNull(tipo.GetMethod("ClaimarEspecificamenteAsync"));
        Assert.NotNull(tipo.GetMethod("SelecionarCandidatosPreClaimAsync"));
    }

    // ---- F3..F6: SQL DOCUMENTAL ESTATICO ----

    [Fact]
    public void Sql_ClaimEspecifico_FiltraPorIntegracaoId()
    {
        Assert.Contains("i.id = :integracao_id", Sql(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_ClaimEspecifico_SemFallbackParaOutraCandidata()
    {
        Assert.Contains("sem fallback para outra candidata", Sql(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sql_ClaimEspecifico_InsereTentativaAntesDeAtualizarTentativaAtual()
    {
        var claim = SecaoClaimEspecifico();
        var selectIntegracao = claim.IndexOf("-- from integracao i", StringComparison.OrdinalIgnoreCase);
        var lockIntegracao = claim.IndexOf("-- for update skip locked;", StringComparison.OrdinalIgnoreCase);
        var insertTentativa = claim.IndexOf("-- insert into tentativa_integracao", StringComparison.OrdinalIgnoreCase);
        var updateIntegracao = claim.IndexOf("-- update integracao i", StringComparison.OrdinalIgnoreCase);
        var tentativaAtual = claim.IndexOf("--     tentativa_atual_id = :nova_tentativa_id", StringComparison.OrdinalIgnoreCase);

        Assert.True(selectIntegracao >= 0);
        Assert.True(lockIntegracao > selectIntegracao);
        Assert.True(insertTentativa > lockIntegracao);
        Assert.True(updateIntegracao > insertTentativa);
        Assert.True(tentativaAtual > updateIntegracao);
        Assert.DoesNotContain("with alvo as", claim, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("from alvo", claim, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alvo.", claim, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DEFERRABLE", Sql(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sql_OwnershipCheck_Existe()
    {
        Assert.Contains("ck_ownership_por_estado", Sql(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_AgendamentoCheck_Existe()
    {
        Assert.Contains("ck_agendamento_por_estado", Sql(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_CorrelationId_NotNull()
    {
        Assert.Contains("correlation_id text not null", Sql(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_CorrelationId_NaoVazio()
    {
        Assert.Contains("length(btrim(correlation_id)) > 0", Sql(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_UniqueIntegracaoFencing_Permanece()
    {
        Assert.Contains("uq_tentativa_integracao_fencing on tentativa_integracao (integracao_id, fencing_token)", Sql(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_PermaneceDocumentalNaoExecutado()
    {
        Assert.Contains("NAO EXECUTAR", Sql(), StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private static (MotorIntegracaoServico Motor, RepositorioMotorIntegracaoFake Repo) MotorComIndeterminado()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Pendente());
        var processador = new ProcessadorExternoIntegracaoFake();
        processador.Configurar(new ResultadoProcessamentoExterno(ClassificacaoFalhaIntegracao.ResultadoIndeterminado));
        var motor = new MotorIntegracaoServico(
            repo,
            processador,
            new DetectorCandidatosIntegracaoFake(),
            new ValidadorGenericoIntegracao(),
            new IdentidadeFixaProvider("worker-rev3"),
            new PoliticaTimeoutFixa(TimeSpan.FromMinutes(10)),
            new TempoFixo(Instante),
            Opcoes());
        return (motor, repo);
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

    private static IntegracaoMotor Pendente()
    {
        var id = Guid.NewGuid();
        return new IntegracaoMotor(
            id,
            "SAP",
            "TOLEDO",
            "PRECADASTRO",
            "CHAVE-1",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString("N")))),
            "CORR-1",
            EstadoIntegracao.Pendente,
            TipoDisparoPendente.Automatico,
            Instante,
            0,
            null,
            null,
            FaseEnvioIntegracao.NaoIniciado,
            null);
    }

    private static string Sql()
    {
        var raiz = LocalizarRaizRepositorio();
        return File.ReadAllText(Path.Combine(raiz, "docs", "Incrementos", "005", "MOTOR_INTEGRACAO_POSTGRESQL18_DOCUMENTAL.sql"));
    }

    private static string SecaoClaimEspecifico()
    {
        var sql = Sql();
        var inicio = sql.IndexOf("-- ETAPA 2 - CLAIM ESPECIFICO", StringComparison.OrdinalIgnoreCase);
        var fim = sql.IndexOf("-- Escritas pos-processamento", StringComparison.OrdinalIgnoreCase);

        Assert.True(inicio >= 0);
        Assert.True(fim > inicio);

        return sql[inicio..fim];
    }

    private static string LocalizarRaizRepositorio()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (diretorio is not null && !File.Exists(Path.Combine(diretorio.FullName, "FugaTES.slnx")))
        {
            diretorio = diretorio.Parent;
        }

        return diretorio?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed class IdentidadeFixaProvider : IIdentidadeWorkerProvider
    {
        private readonly IdentidadeWorkerIntegracao identidade;

        public IdentidadeFixaProvider(string valor) => identidade = new IdentidadeWorkerIntegracao(valor);

        public IdentidadeWorkerIntegracao Obter() => identidade;
    }

    private sealed class PoliticaTimeoutFixa : IPoliticaTimeoutProcessamentoIntegracao
    {
        private readonly TimeSpan timeout;

        public PoliticaTimeoutFixa(TimeSpan timeout) => this.timeout = timeout;

        public TimeSpan ObterTimeout(ClaimIntegracaoMotor claim) => timeout;
    }

    private sealed class TempoFixo : TimeProvider
    {
        private readonly DateTimeOffset agora;

        public TempoFixo(DateTimeOffset agora) => this.agora = agora;

        public override DateTimeOffset GetUtcNow() => agora;
    }
}
