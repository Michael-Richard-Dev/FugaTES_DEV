using System.Globalization;
using FugaTES.Aplicacao.CasosDeUso.MotorIntegracao;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;

namespace FugaTES.Testes;

public sealed class Inc005MotorIntegracaoOfflineTests
{
    [Fact]
    public void EstadosExatos_ETransicoesDesconhecidasSaoFailClosed()
    {
        var estados = Enum.GetNames<EstadoIntegracao>();

        Assert.Equal(8, estados.Length);
        Assert.Contains(nameof(EstadoIntegracao.Pendente), estados);
        Assert.Contains(nameof(EstadoIntegracao.EmProcessamento), estados);
        Assert.Contains(nameof(EstadoIntegracao.AguardandoReprocessamento), estados);
        Assert.Contains(nameof(EstadoIntegracao.AguardandoAcaoManual), estados);
        Assert.Contains(nameof(EstadoIntegracao.ResultadoIndeterminado), estados);
        Assert.Contains(nameof(EstadoIntegracao.Concluida), estados);
        Assert.Contains(nameof(EstadoIntegracao.FalhaDefinitiva), estados);
        Assert.Contains(nameof(EstadoIntegracao.Cancelada), estados);
        Assert.True(MaquinaEstadoIntegracao.PodeClaimar(EstadoIntegracao.Pendente));
        Assert.True(MaquinaEstadoIntegracao.PodeClaimar(EstadoIntegracao.AguardandoReprocessamento));
        Assert.False(MaquinaEstadoIntegracao.PodeClaimar(EstadoIntegracao.Cancelada));
        Assert.False(MaquinaEstadoIntegracao.PodeReprocessar(EstadoIntegracao.ResultadoIndeterminado));
    }

    [Fact]
    public void TipoDisparoPendente_SegueMatrizGaiaRev3()
    {
        Assert.True(MaquinaEstadoIntegracao.PodeAplicarTipoDisparo(EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico));
        Assert.True(MaquinaEstadoIntegracao.PodeAplicarTipoDisparo(EstadoIntegracao.Pendente, TipoDisparoPendente.IntegrarAgora));
        Assert.False(MaquinaEstadoIntegracao.PodeAplicarTipoDisparo(EstadoIntegracao.Pendente, TipoDisparoPendente.Reprocessamento));
        Assert.True(MaquinaEstadoIntegracao.PodeAplicarTipoDisparo(EstadoIntegracao.AguardandoReprocessamento, TipoDisparoPendente.Automatico));
        Assert.True(MaquinaEstadoIntegracao.PodeAplicarTipoDisparo(EstadoIntegracao.AguardandoReprocessamento, TipoDisparoPendente.Reprocessamento));
        Assert.False(MaquinaEstadoIntegracao.PodeAplicarTipoDisparo(EstadoIntegracao.AguardandoReprocessamento, TipoDisparoPendente.IntegrarAgora));

        foreach (var estado in Enum.GetValues<EstadoIntegracao>().Except([EstadoIntegracao.Pendente, EstadoIntegracao.AguardandoReprocessamento]))
        {
            Assert.True(MaquinaEstadoIntegracao.PodeAplicarTipoDisparo(estado, null));
            Assert.False(MaquinaEstadoIntegracao.PodeAplicarTipoDisparo(estado, TipoDisparoPendente.Automatico));
            Assert.False(MaquinaEstadoIntegracao.PodeAplicarTipoDisparo(estado, TipoDisparoPendente.IntegrarAgora));
            Assert.False(MaquinaEstadoIntegracao.PodeAplicarTipoDisparo(estado, TipoDisparoPendente.Reprocessamento));
        }
    }

    [Theory]
    [InlineData(ClassificacaoFalhaIntegracao.FalhaInternaPreEnvio, EstadoIntegracao.AguardandoReprocessamento)]
    [InlineData(ClassificacaoFalhaIntegracao.FalhaTransitoriaSegura, EstadoIntegracao.AguardandoReprocessamento)]
    [InlineData(ClassificacaoFalhaIntegracao.ResultadoIndeterminado, EstadoIntegracao.ResultadoIndeterminado)]
    [InlineData(ClassificacaoFalhaIntegracao.FalhaDefinitiva, EstadoIntegracao.FalhaDefinitiva)]
    public void ClassificacaoFalha_DeterminaTransicaoPermitida(ClassificacaoFalhaIntegracao falha, EstadoIntegracao estado)
    {
        Assert.Equal(estado, MaquinaEstadoIntegracao.AplicarResultado(falha));
    }

    [Fact]
    public async Task Claim_DisputaDoisWorkersTemVencedorUnicoETentativaAtomica()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var agora = Instante();

        var claimA = await repo.ClaimarPrimeiroAsync(new IdentidadeWorkerIntegracao("worker-A"), TimeSpan.FromMinutes(5), agora, CancellationToken.None);
        var claimB = await repo.ClaimarPrimeiroAsync(new IdentidadeWorkerIntegracao("worker-B"), TimeSpan.FromMinutes(5), agora, CancellationToken.None);

        Assert.NotNull(claimA);
        Assert.Null(claimB);
        Assert.Single(repo.Tentativas);
        Assert.Equal(EstadoIntegracao.EmProcessamento, repo.Integracoes.Single().Estado);
        Assert.Equal(claimA!.TentativaId, repo.Integracoes.Single().TentativaAtualId);
        Assert.Contains(repo.Auditorias, evento => evento.Evento == "claim");
        Assert.Contains(repo.Auditorias, evento => evento.Evento == "tentativa_criada");
    }

    [Fact]
    public async Task ClaimFalhaAntesCommit_NaoPersisteClaimNemTentativaOrfa()
    {
        var repo = new RepositorioMotorIntegracaoFake { FalharAntesCommitClaim = true };
        repo.Semear(Integracao(EstadoIntegracao.Pendente));

        var claim = await repo.ClaimarPrimeiroAsync(new IdentidadeWorkerIntegracao("worker-A"), TimeSpan.FromMinutes(5), Instante(), CancellationToken.None);

        Assert.Null(claim);
        Assert.Empty(repo.Tentativas);
        Assert.Equal(EstadoIntegracao.Pendente, repo.Integracoes.Single().Estado);
    }

    [Fact]
    public async Task LeaseValidaBloqueiaSegundoWorkerELeaseExpiradaPermiteRecovery()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var claim = await repo.ClaimarPrimeiroAsync(new IdentidadeWorkerIntegracao("worker-A"), TimeSpan.FromMinutes(5), Instante(), CancellationToken.None);

        var bloqueado = await repo.ClaimarPrimeiroAsync(new IdentidadeWorkerIntegracao("worker-B"), TimeSpan.FromMinutes(5), Instante().AddMinutes(1), CancellationToken.None);
        var recuperadas = await repo.RecuperarLeasesExpiradasAsync(Instante().AddMinutes(6), CancellationToken.None);
        var novoClaim = await repo.ClaimarPrimeiroAsync(new IdentidadeWorkerIntegracao("worker-B"), TimeSpan.FromMinutes(5), Instante().AddMinutes(6), CancellationToken.None);

        Assert.NotNull(claim);
        Assert.Null(bloqueado);
        Assert.Equal(1, recuperadas);
        Assert.NotNull(novoClaim);
        Assert.True(novoClaim!.FencingToken > claim!.FencingToken);
    }

    [Fact]
    public async Task FencingStaleWriterToken10RejeitadoEToken11Persiste()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente, fencingToken: 9));
        var claimA = await repo.ClaimarPrimeiroAsync(new IdentidadeWorkerIntegracao("worker-A"), TimeSpan.FromMinutes(1), Instante(), CancellationToken.None);
        await repo.RecuperarLeasesExpiradasAsync(Instante().AddMinutes(2), CancellationToken.None);
        var claimB = await repo.ClaimarPrimeiroAsync(new IdentidadeWorkerIntegracao("worker-B"), TimeSpan.FromMinutes(1), Instante().AddMinutes(2), CancellationToken.None);

        var stale = await repo.PersistirResultadoAsync(claimA!.IntegracaoId, claimA.TentativaId, claimA.FencingToken, claimA.Worker, ResultadoProcessamentoExterno.Sucesso, Instante().AddMinutes(3), TimeSpan.FromMinutes(5), CancellationToken.None);
        await repo.MarcarEnvioIniciadoAsync(claimB!.IntegracaoId, claimB.TentativaId, claimB.FencingToken, claimB.Worker, Instante().AddMinutes(2).AddSeconds(30), CancellationToken.None);
        var valido = await repo.PersistirResultadoAsync(claimB.IntegracaoId, claimB.TentativaId, claimB.FencingToken, claimB.Worker, ResultadoProcessamentoExterno.Sucesso, Instante().AddMinutes(2).AddSeconds(30), TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.False(stale);
        Assert.True(valido);
        Assert.Equal(10, claimA.FencingToken);
        Assert.Equal(11, claimB.FencingToken);
        Assert.Equal(EstadoIntegracao.Concluida, repo.Integracoes.Single().Estado);
        Assert.Contains(repo.Auditorias, evento => evento.Evento == "stale_writer_rejeitado");
    }

    [Fact]
    public async Task SendMarkerPersistidoAntesDoProcessorESemTransacaoDuranteChamada()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var processador = new ProcessadorExternoIntegracaoFake(() => repo.TransacaoAtiva);
        var motor = Motor(repo, processador);

        var ciclo = await motor.ExecutarUmCicloAsync(CancellationToken.None);

        Assert.Equal(CicloMotorIntegracaoResultado.Processado, ciclo);
        Assert.Equal(1, processador.QuantidadeChamadas);
        Assert.False(processador.TransacaoAtivaObservada);
        Assert.Contains(repo.Auditorias, evento => evento.Evento == "envio_iniciado");
        Assert.Equal(FaseEnvioIntegracao.ResultadoRecebido, repo.Tentativas.Single().FaseEnvio);
    }

    [Fact]
    public async Task MarkerFalhaOuDbIndisponivelImpedeChamadaExterna()
    {
        var repo = new RepositorioMotorIntegracaoFake { Disponivel = false };
        var processador = new ProcessadorExternoIntegracaoFake();
        var motor = Motor(repo, processador);

        var ciclo = await motor.ExecutarUmCicloAsync(CancellationToken.None);

        Assert.Equal(CicloMotorIntegracaoResultado.DegradedDb, ciclo);
        Assert.Equal(0, processador.QuantidadeChamadas);
    }

    [Fact]
    public async Task ResultadoIndeterminadoNaoAgendaRetryNemDisparoPendente()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var processador = new ProcessadorExternoIntegracaoFake();
        processador.Configurar(new ResultadoProcessamentoExterno(ClassificacaoFalhaIntegracao.ResultadoIndeterminado));

        await Motor(repo, processador).ExecutarUmCicloAsync(CancellationToken.None);
        var integracao = repo.Integracoes.Single();

        Assert.Equal(EstadoIntegracao.ResultadoIndeterminado, integracao.Estado);
        Assert.Null(integracao.ProximaTentativaEm);
        Assert.Null(integracao.TipoDisparoPendente);
    }

    [Theory]
    [InlineData(ClassificacaoFalhaIntegracao.FalhaInternaPreEnvio, true)]
    [InlineData(ClassificacaoFalhaIntegracao.FalhaTransitoriaSegura, true)]
    [InlineData(ClassificacaoFalhaIntegracao.FalhaDefinitiva, false)]
    [InlineData(ClassificacaoFalhaIntegracao.ResultadoIndeterminado, false)]
    public async Task RetrySomenteParaFalhasSeguras(ClassificacaoFalhaIntegracao falha, bool agenda)
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var processador = new ProcessadorExternoIntegracaoFake();
        processador.Configurar(new ResultadoProcessamentoExterno(falha));

        await Motor(repo, processador).ExecutarUmCicloAsync(CancellationToken.None);
        var integracao = repo.Integracoes.Single();

        Assert.Equal(agenda, integracao.ProximaTentativaEm is not null);
        Assert.Equal(agenda ? TipoDisparoPendente.Automatico : null, integracao.TipoDisparoPendente);
    }

    [Fact]
    public async Task ReprocessarSomenteAguardandoAcaoManualPreservaHistoricoENaoChamaGateway()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var id = Guid.NewGuid();
        repo.Semear(Integracao(EstadoIntegracao.AguardandoAcaoManual, id: id));
        repo.Semear(Integracao(EstadoIntegracao.ResultadoIndeterminado));
        var casoDeUso = new ReprocessarIntegracaoCasoDeUso(repo, TimeProvider.System);

        var ok = await casoDeUso.ExecutarAsync(id, "corr-reproc", CancellationToken.None);
        var negado = await casoDeUso.ExecutarAsync(repo.Integracoes.Single(i => i.Estado == EstadoIntegracao.ResultadoIndeterminado).Id, "corr-negado", CancellationToken.None);

        Assert.True(ok);
        Assert.False(negado);
        var alvo = repo.Integracoes.Single(i => i.Id == id);
        Assert.Equal(EstadoIntegracao.AguardandoReprocessamento, alvo.Estado);
        Assert.Equal(TipoDisparoPendente.Reprocessamento, alvo.TipoDisparoPendente);
        Assert.Empty(repo.Tentativas);
    }

    [Fact]
    public async Task IntegrarAgoraSomentePendentePersisteSolicitacaoSemGatewayCall()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var id = Guid.NewGuid();
        repo.Semear(Integracao(EstadoIntegracao.Pendente, id: id));
        repo.Semear(Integracao(EstadoIntegracao.AguardandoReprocessamento));
        var casoDeUso = new IntegrarAgoraCasoDeUso(repo, TimeProvider.System);

        var ok = await casoDeUso.ExecutarAsync(id, "corr-agora", CancellationToken.None);
        var negado = await casoDeUso.ExecutarAsync(repo.Integracoes.Single(i => i.Estado == EstadoIntegracao.AguardandoReprocessamento).Id, "corr-negado", CancellationToken.None);

        Assert.True(ok);
        Assert.False(negado);
        var alvo = repo.Integracoes.Single(i => i.Id == id);
        Assert.Equal(EstadoIntegracao.Pendente, alvo.Estado);
        Assert.Equal(TipoDisparoPendente.IntegrarAgora, alvo.TipoDisparoPendente);
        Assert.Empty(repo.Tentativas);
    }

    [Fact]
    public async Task PausasGeralSistemaOperacaoBloqueiamClaimsERetomadaLibera()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente, origem: "SAP", destino: "TOLEDO", operacao: "PRECADASTRO"));
        var geral = new PausaProcessamentoMotor(Guid.NewGuid(), EscopoPausaProcessamento.Geral, null, null, true, Instante(), null);

        Assert.True(await repo.CriarPausaAsync(geral, "corr-pausa", CancellationToken.None));
        Assert.Null(await repo.ClaimarPrimeiroAsync(new IdentidadeWorkerIntegracao("worker-A"), TimeSpan.FromMinutes(1), Instante(), CancellationToken.None));
        Assert.True(await repo.RetomarPausaAsync(geral.Id, Instante().AddMinutes(1), "corr-retoma", CancellationToken.None));
        Assert.NotNull(await repo.ClaimarPrimeiroAsync(new IdentidadeWorkerIntegracao("worker-A"), TimeSpan.FromMinutes(1), Instante().AddMinutes(1), CancellationToken.None));
    }

    [Fact]
    public async Task PausaEspecificaECombinadaNaoAbortaProcessamentoJaIniciado()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente, origem: "TOLEDO", destino: "SAP", operacao: "PRECADASTRO"));
        var claim = await repo.ClaimarPrimeiroAsync(new IdentidadeWorkerIntegracao("worker-A"), TimeSpan.FromMinutes(5), Instante(), CancellationToken.None);
        await repo.CriarPausaAsync(new PausaProcessamentoMotor(Guid.NewGuid(), EscopoPausaProcessamento.SistemaExterno, "TOLEDO", null, true, Instante(), null), "corr", CancellationToken.None);
        await repo.CriarPausaAsync(new PausaProcessamentoMotor(Guid.NewGuid(), EscopoPausaProcessamento.Operacao, null, "PRECADASTRO", true, Instante(), null), "corr", CancellationToken.None);

        var envio = await repo.MarcarEnvioIniciadoAsync(claim!.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, Instante(), CancellationToken.None);

        Assert.True(envio);
        Assert.Equal(EstadoIntegracao.EmProcessamento, repo.Integracoes.Single().Estado);
    }

    [Fact]
    public async Task PausaEscopoIncompativelFailClosed()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        var invalida = new PausaProcessamentoMotor(Guid.NewGuid(), EscopoPausaProcessamento.Operacao, "SAP", null, true, Instante(), null);

        var criada = await repo.CriarPausaAsync(invalida, "corr", CancellationToken.None);

        Assert.False(criada);
        Assert.Empty(repo.Pausas);
    }

    [Fact]
    public async Task RecoverySemMemoriaDistinguePreSendPostMarkerEResultadoRecebido()
    {
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(Integracao(EstadoIntegracao.Pendente));
        var claimPreSend = await repo.ClaimarPrimeiroAsync(new IdentidadeWorkerIntegracao("worker-A"), TimeSpan.FromMinutes(1), Instante(), CancellationToken.None);
        await repo.RecuperarLeasesExpiradasAsync(Instante().AddMinutes(2), CancellationToken.None);
        Assert.Equal(EstadoIntegracao.AguardandoReprocessamento, repo.Integracoes.Single().Estado);

        var repoPostMarker = new RepositorioMotorIntegracaoFake();
        repoPostMarker.Semear(Integracao(EstadoIntegracao.Pendente));
        var claimPost = await repoPostMarker.ClaimarPrimeiroAsync(new IdentidadeWorkerIntegracao("worker-A"), TimeSpan.FromMinutes(1), Instante(), CancellationToken.None);
        await repoPostMarker.MarcarEnvioIniciadoAsync(claimPost!.IntegracaoId, claimPost.TentativaId, claimPost.FencingToken, claimPost.Worker, Instante(), CancellationToken.None);
        await repoPostMarker.RecuperarLeasesExpiradasAsync(Instante().AddMinutes(2), CancellationToken.None);
        Assert.Equal(EstadoIntegracao.ResultadoIndeterminado, repoPostMarker.Integracoes.Single().Estado);

        var repoResultado = new RepositorioMotorIntegracaoFake();
        repoResultado.Semear(Integracao(EstadoIntegracao.Pendente));
        var processador = new ProcessadorExternoIntegracaoFake();
        await Motor(repoResultado, processador).ExecutarUmCicloAsync(CancellationToken.None);
        await repoResultado.RecuperarLeasesExpiradasAsync(Instante().AddMinutes(2), CancellationToken.None);
        Assert.Equal(1, processador.QuantidadeChamadas);
        Assert.Equal(EstadoIntegracao.Concluida, repoResultado.Integracoes.Single().Estado);
        Assert.NotNull(claimPreSend);
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public async Task CulturaNaoAlteraDecisoesDoMotorEClockUsaUtc(string cultura)
    {
        var anterior = CultureInfo.CurrentCulture;
        var anteriorUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultura);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
            var repo = new RepositorioMotorIntegracaoFake();
            repo.Semear(Integracao(EstadoIntegracao.Pendente));

            await Motor(repo, new ProcessadorExternoIntegracaoFake()).ExecutarUmCicloAsync(CancellationToken.None);

            Assert.Equal(EstadoIntegracao.Concluida, repo.Integracoes.Single().Estado);
            Assert.Equal(TimeSpan.Zero, repo.Tentativas.Single().CriadaEm.Offset);
        }
        finally
        {
            CultureInfo.CurrentCulture = anterior;
            CultureInfo.CurrentUICulture = anteriorUi;
        }
    }

    [Fact]
    public void OptionsInvalidasFalhamStartupLogicoEModoRealNaoAutorizado()
    {
        Assert.True(Options().EhValida());
        Assert.False(new MotorIntegracaoOptions { LeaseDuration = TimeSpan.Zero, LeaseRenewInterval = TimeSpan.FromSeconds(1), RetryBackoff = TimeSpan.FromSeconds(1) }.EhValida());
        Assert.False(new MotorIntegracaoOptions { LeaseDuration = TimeSpan.FromSeconds(10), LeaseRenewInterval = TimeSpan.FromSeconds(10), RetryBackoff = TimeSpan.FromSeconds(1) }.EhValida());
        Assert.False(new MotorIntegracaoOptions { LeaseDuration = TimeSpan.FromSeconds(10), LeaseRenewInterval = TimeSpan.FromSeconds(1), RetryBackoff = TimeSpan.Zero }.EhValida());
    }

    [Fact]
    public void IdentidadeWorkerRuntimeENovaPorStartupSemUsername()
    {
        var a = new IdentidadeWorkerRuntimeProvider().Obter().Valor;
        var b = new IdentidadeWorkerRuntimeProvider().Obter().Valor;

        Assert.NotEqual(a, b);
        Assert.Contains(Environment.MachineName, a, StringComparison.OrdinalIgnoreCase);
        var fonte = File.ReadAllText(Path.Combine(LocalizarRaizRepositorio(), "src", "FugaTES.Infraestrutura", "MotorIntegracao", "IdentidadeWorkerRuntimeProvider.cs"));
        Assert.DoesNotContain("UserName", fonte, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArquiteturaHostGenericSemWinFormsENpgsqlSomenteDesenhoDocumental()
    {
        var raiz = LocalizarRaizRepositorio();
        var hostProgram = File.ReadAllText(Path.Combine(raiz, "src", "FugaTES.HostIntegracao", "Program.cs"));
        var worker = File.ReadAllText(Path.Combine(raiz, "src", "FugaTES.HostIntegracao", "HospedagemIntegracaoWorker.cs"));
        var sql = File.ReadAllText(Path.Combine(raiz, "docs", "Incrementos", "005", "MOTOR_INTEGRACAO_POSTGRESQL18_DOCUMENTAL.sql"));

        Assert.Contains("Host.CreateApplicationBuilder", hostProgram, StringComparison.Ordinal);
        var composition = File.ReadAllText(Path.Combine(raiz, "src", "FugaTES.HostIntegracao", "HostIntegracaoComposition.cs"));
        Assert.Contains("HostIntegracaoComposition.Configurar", hostProgram, StringComparison.Ordinal);
        Assert.Contains("AddHostedService", composition, StringComparison.Ordinal);
        Assert.Contains("BackgroundService", worker, StringComparison.Ordinal);
        Assert.Contains("IServiceScopeFactory", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("WinForms", hostProgram, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MessageBox", hostProgram, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Parallel.ForEachAsync", worker, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Task.WhenAll", worker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NpgsqlDataSource", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOR UPDATE SKIP LOCKED", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SegurancaInc005_NaoContemTransporteRealCredenciaisOuDdlExecutado()
    {
        var raiz = LocalizarRaizRepositorio();
        var arquivos = Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(arquivo => arquivo.Contains("MotorIntegracao", StringComparison.OrdinalIgnoreCase)
                || arquivo.Contains("HostIntegracao", StringComparison.OrdinalIgnoreCase))
            .Where(arquivo => !arquivo.Contains(Path.Combine("FugaTES.Persistencia", "MotorIntegracao"), StringComparison.OrdinalIgnoreCase))
            .Where(arquivo => !FontesCarrierR3Autorizadas.Contains(Path.GetRelativePath(raiz, arquivo)));
        // These operational entrypoints predate this reconciliation. Pin the approved source,
        // rather than allowing arbitrary future transport/credential code by filename.
        var operacionais = new Dictionary<string, string>
        {
            ["Program.cs"] = "3443070990249C77C02440A0C4B158142E7C8FF4BB7C433CBD5BF3B078818A81",
            ["GuardianProtocolCommandService.cs"] = "634601F929822B34EECA720D8B7DEB444FE08DE39BD3DB43851416E7080787B2",
            ["CarrierSystemdCredentials.cs"] = "1E2D7CB1FEEEAA3F15420E68132F7A098C6F793129CC7A27624EFAC5529BD7F9",
            ["CarrierLinuxRuntimeBootstrap.cs"] = "F52E404D326073DBACF4D13EA1B7FD1C1E35D6C861DAA61B77F9190EEBA1C794",
            ["AcmR104OneShotEntrypoint.cs"] = "612425BD3DB1BCE6F924FD7DC70AC6B12E963535C1AE99080F5DF20455EDA1A7",
            ["AcmProviderReadOnlyEntrypoint.cs"] = "8417019F29D1BC9F55CBC329281C5AF19352496F04B10F3F68C6BAEF2C88D56E"
        }.ToDictionary(pair => Path.Combine(raiz,"src","FugaTES.HostIntegracao",pair.Key), pair => pair.Value);
        foreach (var (arquivo, hash) in operacionais)
            Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(arquivo))));
        var proibidos = new[]
        {
            "HttpClient",
            "HttpRequestMessage",
            "PostAsync",
            "SoapClient",
            "NpgsqlDataSourceBuilder",
            "OpenConnection",
            "ExecuteNonQuery",
            "Authorization",
            "Bearer",
            "password",
            "senha",
            "Thread.Sleep",
            "Environment.Exit",
            "Thread.Abort"
        };

        foreach (var arquivo in arquivos)
        {
            if (operacionais.ContainsKey(arquivo)) continue;
            var conteudo = File.ReadAllText(arquivo);
            foreach (var proibido in proibidos)
            {
                Assert.DoesNotContain(proibido, conteudo, StringComparison.OrdinalIgnoreCase);
            }
        }
        var empty = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        Assert.Equal("R165R_CREDENTIAL_SECURITY_APPROVAL_REQUIRED", FugaTES.HostIntegracao.AcmProviderReadOnlyEntrypoint.ConfigurationBlock(empty));
        Assert.NotNull(FugaTES.HostIntegracao.AcmR104OneShotEntrypoint.ConfigurationBlock(empty, new()));
        ArchitectureBoundaries.NoHttpTransport(typeof(FugaTES.HostIntegracao.HospedagemIntegracaoWorker));
    }

    // Apenas os oito arquivos novos do worker exclusivo R3. Host/Motor legados continuam protegidos.
    private static readonly HashSet<string> FontesCarrierR3Autorizadas = new(StringComparer.OrdinalIgnoreCase)
    {
        Path.Combine("src", "FugaTES.HostIntegracao", "CarrierManualRequestHandler.cs"),
        Path.Combine("src", "FugaTES.HostIntegracao", "CarrierPollLoop.cs"),
        Path.Combine("src", "FugaTES.HostIntegracao", "CarrierProtocolBinding.cs"),
        Path.Combine("src", "FugaTES.HostIntegracao", "CarrierServiceRuntimeBootstrap.cs"),
        Path.Combine("src", "FugaTES.HostIntegracao", "CarrierServiceSecretProviders.cs"),
        Path.Combine("src", "FugaTES.HostIntegracao", "CarrierSyncComposition.cs"),
        Path.Combine("src", "FugaTES.HostIntegracao", "CarrierSyncManualCommandServer.cs"),
        Path.Combine("src", "FugaTES.HostIntegracao", "CarrierSyncWorker.cs")
    };

    [Fact]
    public void SegurancaInc005_ExcecaoCarrierNaoExcluiHostLegadoOuArquivoNovoArbitrario()
    {
        var raiz = LocalizarRaizRepositorio();
        Assert.Equal(8, FontesCarrierR3Autorizadas.Count);
        foreach (var arquivo in FontesCarrierR3Autorizadas) Assert.True(File.Exists(Path.Combine(raiz, arquivo)));
        Assert.DoesNotContain(Path.Combine("src", "FugaTES.HostIntegracao", "HostIntegracaoComposition.cs"), FontesCarrierR3Autorizadas);
        Assert.DoesNotContain(Path.Combine("src", "FugaTES.HostIntegracao", "HospedagemIntegracaoWorker.cs"), FontesCarrierR3Autorizadas);
        Assert.DoesNotContain(Path.Combine("src", "FugaTES.HostIntegracao", "CarrierNovoNaoRevisado.cs"), FontesCarrierR3Autorizadas);
        Assert.DoesNotContain(Path.Combine("src", "FugaTES.Aplicacao", "MotorIntegracao", "CarrierProtocolBinding.cs"), FontesCarrierR3Autorizadas);
    }

    private static MotorIntegracaoServico Motor(RepositorioMotorIntegracaoFake repo, ProcessadorExternoIntegracaoFake processador)
    {
        return new MotorIntegracaoServico(
            repo,
            processador,
            new DetectorCandidatosIntegracaoFake(),
            new ValidadorGenericoIntegracao(),
            new IdentidadeFixaProvider("worker-fixo"),
            new PoliticaTimeoutFixa(TimeSpan.FromSeconds(30)),
            new TempoFixo(Instante()),
            Options());
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

    private static MotorIntegracaoOptions Options()
    {
        return new MotorIntegracaoOptions
        {            LeaseDuration = TimeSpan.FromMinutes(1),
            LeaseRenewInterval = TimeSpan.FromSeconds(20),            RetryBackoff = TimeSpan.FromMinutes(5),        };
    }

    private static IntegracaoMotor Integracao(
        EstadoIntegracao estado,
        Guid? id = null,
        long fencingToken = 0,
        string origem = "SAP",
        string destino = "TOLEDO",
        string operacao = "OPERACAO-SINTETICA",
        string chaveNegocio = "CHAVE-SINTETICA")
    {
        var idFinal = id ?? Guid.NewGuid();
        return new IntegracaoMotor(
            idFinal,
            origem,
            destino,
            operacao,
            chaveNegocio,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(idFinal.ToString("N") + origem + destino + operacao + chaveNegocio))),
            "CORRELATION-SINTETICA",
            estado,
            estado is EstadoIntegracao.Pendente or EstadoIntegracao.AguardandoReprocessamento ? TipoDisparoPendente.Automatico : null,
            estado is EstadoIntegracao.Pendente or EstadoIntegracao.AguardandoReprocessamento ? Instante() : null,
            fencingToken,
            null,
            null,
            FaseEnvioIntegracao.NaoIniciado,
            null);
    }

    private static DateTimeOffset Instante()
    {
        return new DateTimeOffset(2099, 1, 15, 10, 0, 0, TimeSpan.Zero);
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

    private sealed class IdentidadeFixaProvider : FugaTES.Aplicacao.Contratos.MotorIntegracao.IIdentidadeWorkerProvider
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

    private static string LocalizarRaizRepositorio()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (diretorio is not null && !File.Exists(Path.Combine(diretorio.FullName, "FugaTES.slnx")))
        {
            diretorio = diretorio.Parent;
        }

        return diretorio?.FullName ?? AppContext.BaseDirectory;
    }
}
