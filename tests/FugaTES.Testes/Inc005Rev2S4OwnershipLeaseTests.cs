using System.Security.Cryptography;
using System.Text;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using Xunit;

namespace FugaTES.Testes;

public sealed class Inc005Rev2S4OwnershipLeaseTests
{
    private static readonly DateTimeOffset BaseUtc = new(2099, 2, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly IdentidadeWorkerIntegracao Worker = new("worker-S4");

    [Fact]
    public async Task LeaseExpiradaSemTakeover_NaoMarcaEnvio()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));

        var ok = await repo.MarcarEnvioIniciadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, BaseUtc.AddMinutes(1), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(FaseEnvioIntegracao.NaoIniciado, repo.Integracoes.Single().FaseEnvio);
    }

    [Fact]
    public async Task LeaseExpiradaSemTakeover_NaoPersisteResultado()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));
        await repo.MarcarEnvioIniciadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, BaseUtc.AddSeconds(10), CancellationToken.None);

        var ok = await repo.PersistirResultadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, ResultadoProcessamentoExterno.Sucesso, BaseUtc.AddMinutes(1), TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(EstadoIntegracao.EmProcessamento, repo.Integracoes.Single().Estado);
    }

    [Fact]
    public async Task LeaseValida_MarcaEnvio()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));

        Assert.True(await repo.MarcarEnvioIniciadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, BaseUtc.AddSeconds(30), CancellationToken.None));
    }

    [Fact]
    public async Task RenovarLeaseValida_MesmoFencing()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));

        Assert.True(await repo.RenovarLeaseAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, TimeSpan.FromMinutes(2), BaseUtc.AddSeconds(30), CancellationToken.None));

        Assert.Equal(claim.FencingToken, repo.Integracoes.Single().FencingToken);
    }

    [Fact]
    public async Task RenovarLeaseValida_MesmaTentativa()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));

        Assert.True(await repo.RenovarLeaseAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, TimeSpan.FromMinutes(2), BaseUtc.AddSeconds(30), CancellationToken.None));

        Assert.Equal(claim.TentativaId, repo.Integracoes.Single().TentativaAtualId);
        Assert.Single(repo.Tentativas);
    }

    [Fact]
    public async Task RenovarLeaseStaleToken_Rejeita()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));

        Assert.False(await repo.RenovarLeaseAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken + 1, claim.Worker, TimeSpan.FromMinutes(2), BaseUtc.AddSeconds(30), CancellationToken.None));
    }

    [Fact]
    public async Task RenovarLeaseOutroWorker_Rejeita()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));

        Assert.False(await repo.RenovarLeaseAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, "outro", TimeSpan.FromMinutes(2), BaseUtc.AddSeconds(30), CancellationToken.None));
    }

    [Fact]
    public async Task RenovarLeaseTentativaDivergente_Rejeita()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));

        Assert.False(await repo.RenovarLeaseAsync(claim.IntegracaoId, Guid.NewGuid(), claim.FencingToken, claim.Worker, TimeSpan.FromMinutes(2), BaseUtc.AddSeconds(30), CancellationToken.None));
    }

    [Fact]
    public async Task RenovarLeaseExpirada_Rejeita()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));

        Assert.False(await repo.RenovarLeaseAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, TimeSpan.FromMinutes(2), BaseUtc.AddMinutes(1), CancellationToken.None));
    }

    [Fact]
    public async Task Heartbeat_ProcessamentoLongo_RenovaLease()
    {
        var tempo = new TempoManual(BaseUtc);
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var processador = new ProcessadorExternoIntegracaoFake();
        var pendente = processador.ManterPendente();
        var task = Motor(repo, processador, tempo).ExecutarUmCicloAsync(CancellationToken.None);
        await AteAsync(() => processador.QuantidadeChamadas == 1);

        tempo.Avancar(TimeSpan.FromSeconds(20));
        await Task.Yield();

        Assert.True(repo.Integracoes.Single().LeaseExpiraEm > BaseUtc.AddMinutes(1));
        pendente.SetResult(ResultadoProcessamentoExterno.Sucesso);
        Assert.Equal(CicloMotorIntegracaoResultado.Processado, await task);
    }

    [Fact]
    public async Task Heartbeat_NaoCriaNovoFencing()
    {
        var tempo = new TempoManual(BaseUtc);
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var processador = new ProcessadorExternoIntegracaoFake();
        var pendente = processador.ManterPendente();
        var task = Motor(repo, processador, tempo).ExecutarUmCicloAsync(CancellationToken.None);
        await AteAsync(() => processador.QuantidadeChamadas == 1);
        var token = repo.Integracoes.Single().FencingToken;

        tempo.Avancar(TimeSpan.FromSeconds(20));
        await Task.Yield();

        Assert.Equal(token, repo.Integracoes.Single().FencingToken);
        pendente.SetResult(ResultadoProcessamentoExterno.Sucesso);
        await task;
    }

    [Fact]
    public async Task Heartbeat_EncerraQuandoProcessorTermina()
    {
        var tempo = new TempoManual(BaseUtc);
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var processador = new ProcessadorExternoIntegracaoFake();

        Assert.Equal(CicloMotorIntegracaoResultado.Processado, await Motor(repo, processador, tempo).ExecutarUmCicloAsync(CancellationToken.None));
        var renovaAntes = repo.Auditorias.Count(e => e.Evento == "lease_renew");
        tempo.Avancar(TimeSpan.FromMinutes(5));
        await Task.Yield();

        Assert.Equal(renovaAntes, repo.Auditorias.Count(e => e.Evento == "lease_renew"));
    }

    [Fact]
    public async Task Heartbeat_PerdeOwnership_ResultNaoEhPersistido()
    {
        var tempo = new TempoManual(BaseUtc);
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var processador = new ProcessadorExternoIntegracaoFake();
        processador.ManterPendente();
        var task = Motor(repo, processador, tempo).ExecutarUmCicloAsync(CancellationToken.None);
        await AteAsync(() => processador.QuantidadeChamadas == 1);

        tempo.Avancar(TimeSpan.FromMinutes(2));
        await Task.Yield();

        Assert.Equal(CicloMotorIntegracaoResultado.StaleWriter, await task);
        Assert.True(processador.CancelamentoObservado);
        Assert.Equal(EstadoIntegracao.EmProcessamento, repo.Integracoes.Single().Estado);
    }

    [Fact]
    public async Task MarkerSemOwnership_Rejeita()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));

        Assert.False(await repo.MarcarEnvioIniciadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, "outro", BaseUtc.AddSeconds(10), CancellationToken.None));
    }

    [Fact]
    public async Task MarkerFaseNaoIniciado_Valido()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));

        Assert.True(await repo.MarcarEnvioIniciadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, BaseUtc.AddSeconds(10), CancellationToken.None));
        Assert.Equal(FaseEnvioIntegracao.EnvioIniciado, repo.Integracoes.Single().FaseEnvio);
    }

    [Fact]
    public async Task MarkerDuplicado_Rejeita()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));
        await repo.MarcarEnvioIniciadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, BaseUtc.AddSeconds(10), CancellationToken.None);

        Assert.False(await repo.MarcarEnvioIniciadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, BaseUtc.AddSeconds(20), CancellationToken.None));
    }

    [Fact]
    public async Task PersistirResultadoSemMarker_Rejeita()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));

        Assert.False(await repo.PersistirResultadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, ResultadoProcessamentoExterno.Sucesso, BaseUtc.AddSeconds(10), TimeSpan.FromMinutes(5), CancellationToken.None));
    }

    [Fact]
    public async Task PersistirResultadoComLeaseExpirada_Rejeita()
    {
        await LeaseExpiradaSemTakeover_NaoPersisteResultado();
    }

    [Fact]
    public async Task PersistirResultadoStale_Rejeita()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));
        await repo.MarcarEnvioIniciadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, BaseUtc.AddSeconds(10), CancellationToken.None);

        Assert.False(await repo.PersistirResultadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken + 1, claim.Worker, ResultadoProcessamentoExterno.Sucesso, BaseUtc.AddSeconds(20), TimeSpan.FromMinutes(5), CancellationToken.None));
    }

    [Fact]
    public async Task ResultadoConhecido_DefineFaseResultadoRecebido()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));
        await repo.MarcarEnvioIniciadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, BaseUtc.AddSeconds(10), CancellationToken.None);

        Assert.True(await repo.PersistirResultadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, ResultadoProcessamentoExterno.Sucesso, BaseUtc.AddSeconds(20), TimeSpan.FromMinutes(5), CancellationToken.None));
        Assert.Equal(FaseEnvioIntegracao.ResultadoRecebido, repo.Integracoes.Single().FaseEnvio);
    }

    [Fact]
    public async Task Sucesso_FinalizaTentativaSucesso()
    {
        var repo = await RepoProcessadoAsync(ResultadoProcessamentoExterno.Sucesso);

        Assert.Equal(DesfechoTentativaIntegracao.Sucesso, repo.Tentativas.Single().Desfecho);
        Assert.Equal(EstadoIntegracao.Concluida, repo.Integracoes.Single().Estado);
    }

    [Fact]
    public async Task FalhaConhecida_FinalizaTentativaFalha()
    {
        var repo = await RepoProcessadoAsync(new ResultadoProcessamentoExterno(ClassificacaoFalhaIntegracao.FalhaTransitoriaSegura));

        Assert.Equal(DesfechoTentativaIntegracao.Falha, repo.Tentativas.Single().Desfecho);
        Assert.Equal(EstadoIntegracao.AguardandoReprocessamento, repo.Integracoes.Single().Estado);
    }

    [Fact]
    public async Task ResultadoIndeterminado_FinalizaTentativaIndeterminada()
    {
        var repo = await RepoIndeterminadoPorTimeoutAsync();

        Assert.Equal(DesfechoTentativaIntegracao.Indeterminada, repo.Tentativas.Single().Desfecho);
        Assert.Equal(EstadoIntegracao.ResultadoIndeterminado, repo.Integracoes.Single().Estado);
    }

    [Fact]
    public async Task ResultadoIndeterminado_AutoRetryZero()
    {
        var repo = await RepoIndeterminadoPorTimeoutAsync();

        Assert.Null(repo.Integracoes.Single().TipoDisparoPendente);
        Assert.Null(repo.Integracoes.Single().ProximaTentativaEm);
    }

    [Fact]
    public async Task RecoveryPreSend_TentativaInterrompida()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));

        await repo.RecuperarLeasesExpiradasAsync(BaseUtc.AddMinutes(2), CancellationToken.None);

        Assert.Equal(DesfechoTentativaIntegracao.Interrompida, repo.Tentativas.Single(t => t.Id == claim.TentativaId).Desfecho);
    }

    [Fact]
    public async Task RecoveryPreSend_AgendaAgora()
    {
        var (repo, _) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));

        await repo.RecuperarLeasesExpiradasAsync(BaseUtc.AddMinutes(2), CancellationToken.None);

        var integracao = repo.Integracoes.Single();
        Assert.Equal(EstadoIntegracao.AguardandoReprocessamento, integracao.Estado);
        Assert.Equal(BaseUtc.AddMinutes(2), integracao.ProximaTentativaEm);
    }

    [Fact]
    public async Task RecoveryPostMarker_TentativaIndeterminada()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));
        await repo.MarcarEnvioIniciadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, BaseUtc.AddSeconds(10), CancellationToken.None);

        await repo.RecuperarLeasesExpiradasAsync(BaseUtc.AddMinutes(2), CancellationToken.None);

        Assert.Equal(DesfechoTentativaIntegracao.Indeterminada, repo.Tentativas.Single().Desfecho);
    }

    [Fact]
    public async Task RecoveryPostMarker_SemRetry()
    {
        var (repo, claim) = await RepoComClaimAsync(TimeSpan.FromMinutes(1));
        await repo.MarcarEnvioIniciadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, BaseUtc.AddSeconds(10), CancellationToken.None);

        await repo.RecuperarLeasesExpiradasAsync(BaseUtc.AddMinutes(2), CancellationToken.None);

        Assert.Null(repo.Integracoes.Single().ProximaTentativaEm);
    }

    [Fact]
    public async Task RecoveryResultadoConhecido_NaoReenvia()
    {
        var repo = await RepoProcessadoAsync(ResultadoProcessamentoExterno.Sucesso);

        var recuperadas = await repo.RecuperarLeasesExpiradasAsync(BaseUtc.AddMinutes(2), CancellationToken.None);

        Assert.Equal(0, recuperadas);
        Assert.Equal(EstadoIntegracao.Concluida, repo.Integracoes.Single().Estado);
    }

    [Fact]
    public async Task TimeoutPostMarker_ResultaIndeterminado()
    {
        var repo = await RepoIndeterminadoPorTimeoutAsync();

        Assert.Equal(EstadoIntegracao.ResultadoIndeterminado, repo.Integracoes.Single().Estado);
    }

    [Fact]
    public async Task TimeoutPostMarker_NaoAgendaRetry()
    {
        await ResultadoIndeterminado_AutoRetryZero();
    }

    [Fact]
    public async Task ExcecaoPostMarker_ResultaIndeterminado()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var motor = Motor(repo, new ProcessadorExternoIntegracaoFakeExcecao(), new TempoManual(BaseUtc));

        Assert.Equal(CicloMotorIntegracaoResultado.ResultadoIndeterminado, await motor.ExecutarUmCicloAsync(CancellationToken.None));
        Assert.Equal(EstadoIntegracao.ResultadoIndeterminado, repo.Integracoes.Single().Estado);
    }

    [Fact]
    public async Task CancellationPostMarker_NaoViraSucesso()
    {
        var tempo = new TempoManual(BaseUtc);
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var processador = new ProcessadorExternoIntegracaoFake();
        processador.ManterPendente();
        using var cts = new CancellationTokenSource();
        var task = Motor(repo, processador, tempo).ExecutarUmCicloAsync(cts.Token);
        await AteAsync(() => processador.QuantidadeChamadas == 1);

        cts.Cancel();

        Assert.Equal(CicloMotorIntegracaoResultado.CanceladoExterno, await task);
        Assert.NotEqual(EstadoIntegracao.Concluida, repo.Integracoes.Single().Estado);
    }

    [Fact]
    public async Task AtomicidadeFalhaAposMutacaoClaim_RollbackIntegral()
    {
        var repo = new RepositorioMotorIntegracaoFake { FalharAposMutacaoClaimAntesTentativa = true };
        var original = Integracao(EstadoIntegracao.Pendente, fencingToken: 7);
        repo.Semear(original);

        var claim = await repo.ClaimarPrimeiroAsync(Worker, TimeSpan.FromMinutes(1), BaseUtc, CancellationToken.None);

        Assert.Null(claim);
        Assert.Equal(original, repo.Integracoes.Single());
        Assert.Empty(repo.Tentativas);
    }

    [Fact]
    public async Task TimeProviderControlado_Lease()
    {
        var tempo = new TempoManual(BaseUtc);
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        await Motor(repo, new ProcessadorExternoIntegracaoFake(), tempo).ExecutarUmCicloAsync(CancellationToken.None);

        Assert.Equal(BaseUtc, repo.Tentativas.Single().CriadaEm);
    }

    [Fact]
    public async Task TimeProviderControlado_Heartbeat()
    {
        await Heartbeat_ProcessamentoLongo_RenovaLease();
    }

    [Fact]
    public async Task TimeProviderControlado_Timeout()
    {
        await TimeoutPostMarker_ResultaIndeterminado();
    }

    [Fact]
    public void ThreadSleep_TestesS4_Zero()
    {
        var fonte = File.ReadAllText(Path.Combine(LocalizarRaizRepositorio(), "tests", "FugaTES.Testes", "Inc005Rev2S4OwnershipLeaseTests.cs"));

        Assert.DoesNotContain("Thread." + "Sleep", fonte, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TxDuranteProcessor_NaoExiste()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var processador = new ProcessadorExternoIntegracaoFake(() => repo.TransacaoAtiva);

        await Motor(repo, processador, new TempoManual(BaseUtc)).ExecutarUmCicloAsync(CancellationToken.None);

        Assert.False(processador.TransacaoAtivaObservada);
    }

    private static async Task<RepositorioMotorIntegracaoFake> RepoProcessadoAsync(ResultadoProcessamentoExterno resultado)
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var processador = new ProcessadorExternoIntegracaoFake();
        processador.Configurar(resultado);
        await Motor(repo, processador, new TempoManual(BaseUtc)).ExecutarUmCicloAsync(CancellationToken.None);
        return repo;
    }

    private static async Task<RepositorioMotorIntegracaoFake> RepoIndeterminadoPorTimeoutAsync()
    {
        var tempo = new TempoManual(BaseUtc);
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var processador = new ProcessadorExternoIntegracaoFake();
        processador.ManterPendente();
        var task = Motor(repo, processador, tempo).ExecutarUmCicloAsync(CancellationToken.None);
        await AteAsync(() => processador.QuantidadeChamadas == 1);
        tempo.Avancar(TimeSpan.FromSeconds(45));
        Assert.Equal(CicloMotorIntegracaoResultado.ResultadoIndeterminado, await task);
        return repo;
    }

    private static async Task<(RepositorioMotorIntegracaoFake Repo, ClaimIntegracaoMotor Claim)> RepoComClaimAsync(TimeSpan leaseDuration)
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var claim = await repo.ClaimarPrimeiroAsync(Worker, leaseDuration, BaseUtc, CancellationToken.None);
        return (repo, claim!);
    }

    private static MotorIntegracaoServico Motor(RepositorioMotorIntegracaoFake repo, IProcessadorExternoIntegracao processador, TimeProvider tempo)
    {
        var options = new MotorIntegracaoOptions
        {            LeaseDuration = TimeSpan.FromMinutes(1),
            LeaseRenewInterval = TimeSpan.FromSeconds(20),            RetryBackoff = TimeSpan.FromMinutes(5),        };

        return new MotorIntegracaoServico(
            repo,
            processador,
            new DetectorCandidatosIntegracaoFake(),
            new ValidadorGenericoIntegracao(),
            new IdentidadeFixaProvider(Worker.Valor),
            new PoliticaTimeoutFixa(TimeSpan.FromSeconds(45)),
            tempo,
            options);
    }

    private static IntegracaoMotor Integracao(EstadoIntegracao estado, long fencingToken = 0)
    {
        var id = Guid.NewGuid();
        return new IntegracaoMotor(
            id,
            "SAP",
            "TOLEDO",
            "PRECADASTRO",
            "CHAVE-S4",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString("N")))),
            "CORR-S4",
            estado,
            estado is EstadoIntegracao.Pendente or EstadoIntegracao.AguardandoReprocessamento ? TipoDisparoPendente.Automatico : null,
            BaseUtc,
            fencingToken,
            null,
            null,
            FaseEnvioIntegracao.NaoIniciado,
            null);
    }

    private static async Task AteAsync(Func<bool> condicao)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condicao())
            {
                return;
            }

            await Task.Yield();
        }

        throw new TimeoutException("Condicao deterministica nao atingida.");
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

        public IdentidadeFixaProvider(string valor)
        {
            identidade = new IdentidadeWorkerIntegracao(valor);
        }

        public IdentidadeWorkerIntegracao Obter()
        {
            return identidade;
        }
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

    private sealed class ProcessadorExternoIntegracaoFakeExcecao : IProcessadorExternoIntegracao
    {
        public Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("falha-pos-marker");
        }
    }

    private sealed class TempoManual : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<TemporizadorManual> timers = [];
        private DateTimeOffset agora;

        public TempoManual(DateTimeOffset agora)
        {
            this.agora = agora;
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate)
            {
                return agora;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TemporizadorManual(this, callback, state, dueTime, period);
            lock (gate)
            {
                timers.Add(timer);
            }

            return timer;
        }

        public void Avancar(TimeSpan intervalo)
        {
            List<TemporizadorManual> disparar;
            lock (gate)
            {
                agora = agora.Add(intervalo);
                disparar = timers.Where(timer => timer.DeveDisparar(agora)).ToList();
            }

            foreach (var timer in disparar)
            {
                timer.Disparar();
            }
        }

        private void Remover(TemporizadorManual timer)
        {
            lock (gate)
            {
                timers.Remove(timer);
            }
        }

        private sealed class TemporizadorManual : ITimer
        {
            private readonly TempoManual dono;
            private readonly TimerCallback callback;
            private readonly object? state;
            private TimeSpan period;
            private DateTimeOffset? venceEm;
            private bool disposed;

            public TemporizadorManual(TempoManual dono, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                this.dono = dono;
                this.callback = callback;
                this.state = state;
                this.period = period;
                venceEm = dueTime == Timeout.InfiniteTimeSpan ? null : dono.GetUtcNow().Add(dueTime);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed)
                {
                    return false;
                }

                this.period = period;
                venceEm = dueTime == Timeout.InfiniteTimeSpan ? null : dono.GetUtcNow().Add(dueTime);
                return true;
            }

            public bool DeveDisparar(DateTimeOffset agora)
            {
                return !disposed && venceEm is not null && venceEm <= agora;
            }

            public void Disparar()
            {
                if (disposed)
                {
                    return;
                }

                if (period == Timeout.InfiniteTimeSpan)
                {
                    Dispose();
                }
                else
                {
                    venceEm = dono.GetUtcNow().Add(period);
                }

                callback(state);
            }

            public void Dispose()
            {
                disposed = true;
                dono.Remover(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
