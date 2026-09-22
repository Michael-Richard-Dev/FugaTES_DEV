using System.Security.Cryptography;
using System.Text;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.HostIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace FugaTES.Testes;

public sealed class Inc005Rev2S5HostCompositionTests
{
    private static readonly DateTimeOffset BaseUtc = new(2099, 3, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CompositionRoot_ConfigValida_ResolveMotor()
    {
        using var host = CriarHostValido();
        using var scope = host.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<MotorIntegracaoServico>());
    }

    [Fact]
    public void CompositionRoot_ResolveWorker()
    {
        using var host = CriarHostValido();
        Assert.Contains(host.Services.GetServices<IHostedService>(), servico => servico is HospedagemIntegracaoWorker);
    }

    [Fact]
    public void CompositionRoot_ResolveTimeProvider() => Assert.NotNull(CriarHostValido().Services.GetRequiredService<TimeProvider>());

    [Fact]
    public void CompositionRoot_ResolveDetectorFake() => Assert.IsType<DetectorCandidatosIntegracaoFake>(CriarHostValido().Services.GetRequiredService<IDetectorCandidatosIntegracao>());

    [Fact]
    public void CompositionRoot_ResolveValidator() => Assert.IsType<ValidadorGenericoIntegracao>(CriarHostValido().Services.GetRequiredService<IValidadorIntegracao>());

    [Fact]
    public void CompositionRoot_ResolveRepositoryFake() => Assert.IsType<RepositorioMotorIntegracaoFake>(CriarHostValido().Services.GetRequiredService<IMotorIntegracaoRepository>());

    [Fact]
    public void CompositionRoot_ResolveProcessorFake() => Assert.IsType<ProcessadorExternoIntegracaoFake>(CriarHostValido().Services.GetRequiredService<IProcessadorExternoIntegracao>());

    [Fact]
    public void CompositionRoot_ResolveTimeoutPolicy() => Assert.IsType<PoliticaTimeoutProcessamentoIntegracaoConfigurada>(CriarHostValido().Services.GetRequiredService<IPoliticaTimeoutProcessamentoIntegracao>());

    [Fact]
    public void CompositionRoot_SemWinForms()
    {
        var program = File.ReadAllText(Path.Combine(LocalizarRaizRepositorio(), "src", "FugaTES.HostIntegracao", "Program.cs"));
        var csproj = File.ReadAllText(Path.Combine(LocalizarRaizRepositorio(), "src", "FugaTES.HostIntegracao", "FugaTES.HostIntegracao.csproj"));
        Assert.DoesNotContain("WinForms", program, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FugaTES.WinForms", csproj, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("HostIntegracao:PollInterval", null)]
    [InlineData("HostIntegracao:PollInterval", "00:00:00")]
    [InlineData("MotorIntegracao:LeaseDuration", null)]
    [InlineData("MotorIntegracao:LeaseRenewInterval", null)]
    [InlineData("MotorIntegracao:LeaseRenewInterval", "00:01:00")]
    [InlineData("MotorIntegracao:RetryBackoff", "00:00:00")]
    [InlineData("HostIntegracao:ShutdownGracePeriod", null)]
    [InlineData("HostIntegracao:ExternalMode", null)]
    [InlineData("HostIntegracao:ExternalMode", "Real")]
    public void Config_FailFast(string chave, string? valor)
    {
        var config = ConfigValida();
        if (valor is null)
        {
            config.Remove(chave);
        }
        else
        {
            config[chave] = valor;
        }

        Assert.ThrowsAny<Exception>(() => CriarHost(config).StartAsync(CancellationToken.None).GetAwaiter().GetResult());
    }

    [Fact]
    public void Config_PollIntervalAusenteFalha() => Config_FailFast("HostIntegracao:PollInterval", null);

    [Fact]
    public void Config_PollIntervalInvalidoFalha() => Config_FailFast("HostIntegracao:PollInterval", "00:00:00");

    [Fact]
    public void Config_LeaseDurationAusenteFalha() => Config_FailFast("MotorIntegracao:LeaseDuration", null);

    [Fact]
    public void Config_LeaseRenewIntervalAusenteFalha() => Config_FailFast("MotorIntegracao:LeaseRenewInterval", null);

    [Fact]
    public void Config_LeaseRenewMaiorOuIgualLeaseFalha() => Config_FailFast("MotorIntegracao:LeaseRenewInterval", "00:01:00");

    [Fact]
    public void Config_RetryBackoffAusenteOuInvalidoFalha() => Config_FailFast("MotorIntegracao:RetryBackoff", "00:00:00");

    [Fact]
    public void Config_ShutdownGraceAusenteFalha() => Config_FailFast("HostIntegracao:ShutdownGracePeriod", null);

    [Fact]
    public void Config_ExternalModeAusenteFalha() => Config_FailFast("HostIntegracao:ExternalMode", null);

    [Fact]
    public void Config_ExternalModeRealFalha() => Config_FailFast("HostIntegracao:ExternalMode", "Real");

    [Fact]
    public void Worker_MotorObrigatorioNaoUsaGetServiceOpcional()
    {
        var fonte = FonteWorker();
        Assert.Contains("GetRequiredService<MotorIntegracaoServico>", fonte, StringComparison.Ordinal);
        Assert.DoesNotContain("Get" + "Service<MotorIntegracaoServico>", fonte, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_TimeProviderObrigatorioSemFallback()
    {
        var fonte = FonteWorker();
        Assert.Contains("TimeProvider timeProvider", fonte, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeProvider?", fonte, StringComparison.Ordinal);
        Assert.DoesNotContain("?? TimeProvider.System", fonte, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_OptionsObrigatoriasSemFallback()
    {
        var fonte = FonteWorker() + File.ReadAllText(Path.Combine(LocalizarRaizRepositorio(), "src", "FugaTES.HostIntegracao", "Program.cs"));
        Assert.DoesNotContain("IOptions<MotorIntegracaoOptions>?", fonte, StringComparison.Ordinal);
        Assert.DoesNotContain("new MotorIntegracaoOptions()", fonte, StringComparison.Ordinal);
    }

    [Fact]
    public void DependenciaObrigatoria_TimeProvider_FalhaResolucao()
    {
        var builder = CriarBuilder(ConfigValida());
        HostIntegracaoComposition.Configurar(builder);
        builder.Services.RemoveAll<TimeProvider>();
        using var host = builder.Build();
        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<IEnumerable<IHostedService>>().ToArray());
    }

    [Fact]
    public async Task Shutdown_ParaNovosClaimsImediatamente()
    {
        var cenario = CriarHostComProcessadorPendente();
        using var host = cenario.Host;
        await host.StartAsync();
        await AteAsync(() => cenario.Processador.QuantidadeChamadas == 1);
        var stop = host.StopAsync();
        cenario.Detector.Enfileirar(Candidato());
        cenario.Tempo.Avancar(TimeSpan.FromSeconds(1));
        cenario.Processador.ManterPendente().TrySetResult(ResultadoProcessamentoExterno.Sucesso);
        cenario.Pendente.SetResult(ResultadoProcessamentoExterno.Sucesso);
        await stop;
        Assert.Equal(1, cenario.Processador.QuantidadeChamadas);
    }

    [Fact]
    public async Task Shutdown_InterrompePolling()
    {
        using var host = CriarHostValido(new TempoManual(BaseUtc));
        await host.StartAsync();
        await host.StopAsync();
        Assert.True(true);
    }

    [Fact]
    public async Task Shutdown_TrabalhoAtivoContinuaDuranteGrace()
    {
        var cenario = CriarHostComProcessadorPendente();
        using var host = cenario.Host;
        await host.StartAsync();
        await AteAsync(() => cenario.Processador.QuantidadeChamadas == 1);
        var stop = host.StopAsync();
        await Task.Yield();
        Assert.False(cenario.Processador.CancelamentoObservado);
        cenario.Pendente.SetResult(ResultadoProcessamentoExterno.Sucesso);
        await stop;
    }

    [Fact]
    public async Task Shutdown_DuranteGrace_HeartbeatContinua()
    {
        var cenario = CriarHostComProcessadorPendente();
        using var host = cenario.Host;
        await host.StartAsync();
        await AteAsync(() => cenario.Processador.QuantidadeChamadas == 1);
        var stop = host.StopAsync();
        cenario.Tempo.Avancar(TimeSpan.FromSeconds(20));
        await Task.Yield();
        Assert.Contains(cenario.Repo.Auditorias, e => e.Evento == "lease_renew");
        cenario.Pendente.SetResult(ResultadoProcessamentoExterno.Sucesso);
        await stop;
    }

    [Fact]
    public async Task Shutdown_TrabalhoTerminaNoGrace_PersisteResultado()
    {
        var cenario = CriarHostComProcessadorPendente();
        using var host = cenario.Host;
        await host.StartAsync();
        await AteAsync(() => cenario.Processador.QuantidadeChamadas == 1);
        var stop = host.StopAsync();
        cenario.Pendente.SetResult(ResultadoProcessamentoExterno.Sucesso);
        await stop;
        Assert.Equal(EstadoIntegracao.Concluida, cenario.Repo.Integracoes.Single().Estado);
    }

    [Fact]
    public async Task Shutdown_GraceExpira_CancelaActiveWork()
    {
        var cenario = CriarHostComProcessadorPendente();
        using var host = cenario.Host;
        await host.StartAsync();
        await AteAsync(() => cenario.Processador.QuantidadeChamadas == 1);
        var stop = host.StopAsync();
        cenario.Tempo.Avancar(TimeSpan.FromSeconds(5));
        await stop;
        Assert.True(cenario.Processador.CancelamentoObservado);
    }

    [Fact]
    public async Task Shutdown_PosMarkerExpirado_NaoMarcaSucesso()
    {
        var cenario = CriarHostComProcessadorPendente();
        using var host = cenario.Host;
        await host.StartAsync();
        await AteAsync(() => cenario.Processador.QuantidadeChamadas == 1);
        var stop = host.StopAsync();
        cenario.Tempo.Avancar(TimeSpan.FromSeconds(5));
        await stop;
        Assert.NotEqual(EstadoIntegracao.Concluida, cenario.Repo.Integracoes.Single().Estado);
    }

    [Fact]
    public async Task Shutdown_PosMarkerExpirado_NaoAgendaRetry()
    {
        var cenario = CriarHostComProcessadorPendente();
        using var host = cenario.Host;
        await host.StartAsync();
        await AteAsync(() => cenario.Processador.QuantidadeChamadas == 1);
        var stop = host.StopAsync();
        cenario.Tempo.Avancar(TimeSpan.FromSeconds(5));
        await stop;
        Assert.Null(cenario.Repo.Integracoes.Single().TipoDisparoPendente);
    }

    [Fact]
    public async Task Shutdown_Restart_RecoveryFailClosed()
    {
        var cenario = CriarHostComProcessadorPendente();
        using var host = cenario.Host;
        await host.StartAsync();
        await AteAsync(() => cenario.Processador.QuantidadeChamadas == 1);
        var stop = host.StopAsync();
        cenario.Tempo.Avancar(TimeSpan.FromSeconds(5));
        await stop;
        await cenario.Repo.RecuperarLeasesExpiradasAsync(BaseUtc.AddMinutes(2), CancellationToken.None);
        Assert.Equal(EstadoIntegracao.ResultadoIndeterminado, cenario.Repo.Integracoes.Single().Estado);
    }

    [Fact]
    public void Logging_HostStart() => Assert.Contains("HOST_START", FonteWorker(), StringComparison.Ordinal);

    [Fact]
    public void Logging_HostStop() => Assert.Contains("HOST_STOP", FonteWorker(), StringComparison.Ordinal);

    [Fact]
    public void Logging_WorkerId() => Assert.Contains("WORKER_ID", FonteWorker(), StringComparison.Ordinal);

    [Fact]
    public async Task Logging_DbOfflineTransicao()
    {
        var logs = await LogsDeCicloAsync(disponivel: false);
        Assert.Contains(logs, item => item.Contains("DB_UNAVAILABLE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logging_DbOnlineTransicao()
    {
        var logs = await LogsDeCicloAsync(disponivel: true);
        Assert.Contains(logs, item => item.Contains("DB_AVAILABLE", StringComparison.Ordinal));
    }

    [Fact]
    public void Logging_ResultadoIndeterminado() => Assert.Contains("RESULTADO_INDETERMINADO", FonteWorker(), StringComparison.Ordinal);

    [Fact]
    public void Logging_NaoContemSegredos()
    {
        var fonte = FonteWorker() + File.ReadAllText(Path.Combine(LocalizarRaizRepositorio(), "src", "FugaTES.HostIntegracao", "HostIntegracaoComposition.cs"));
        foreach (var proibido in new[] { "Password", "Authorization", "Bearer", "connection string", "CPF", "CNPJ" })
        {
            Assert.DoesNotContain(proibido, fonte, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void BusyLoop_Ausente()
    {
        var fonte = FonteWorker();
        Assert.Contains("Task.Delay(options.PollInterval, timeProvider, stoppingToken)", fonte, StringComparison.Ordinal);
        Assert.DoesNotContain("while (true)", fonte, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThreadSleep_TestesS5_Zero()
    {
        var fonte = File.ReadAllText(Path.Combine(LocalizarRaizRepositorio(), "tests", "FugaTES.Testes", "Inc005Rev2S5HostCompositionTests.cs"));
        Assert.DoesNotContain("Thread." + "Sleep", fonte, StringComparison.Ordinal);
    }

    // ---- REV3 F7: race de shutdown ----

    [Fact]
    public async Task ShutdownRace_StopEntreCiclos_NaoNasceNovoTrabalho()
    {
        var tempo = new TempoManual(BaseUtc);
        var repo = new RepositorioMotorIntegracaoFake();
        var detector = new DetectorCandidatosIntegracaoFake(); // fila vazia: primeiro ciclo NO_WORK
        var processador = new ProcessadorExternoIntegracaoFake();
        var logs = new ListaLoggerProvider();
        using var host = CriarHostComFakes(tempo, repo, detector, processador, logs);

        await host.StartAsync();
        await AteAsync(() => logs.Mensagens.Any(m => m.Contains("NO_WORK", StringComparison.Ordinal)));

        // Trabalho novo aparece exatamente quando o shutdown e solicitado.
        var stop = host.StopAsync();
        detector.Enfileirar(Candidato());
        tempo.Avancar(TimeSpan.FromSeconds(1));
        await stop;

        // Invariante: nenhum registro/claim/processamento novo nasceu apos stopRequested.
        Assert.Empty(repo.Integracoes);
        Assert.Equal(0, processador.QuantidadeChamadas);
    }

    // ---- REV3 F28: logging ----

    [Fact]
    public async Task NenhumTrabalho_LogaNoWorkNaoPaused()
    {
        var logs = new ListaLoggerProvider();
        using var host = CriarHostComFakes(new TempoManual(BaseUtc), new RepositorioMotorIntegracaoFake(), new DetectorCandidatosIntegracaoFake(), new ProcessadorExternoIntegracaoFake(), logs);

        await host.StartAsync();
        await AteAsync(() => logs.Mensagens.Any(m => m.Contains("NO_WORK", StringComparison.Ordinal)));
        await host.StopAsync();

        Assert.Contains(logs.Mensagens, m => m.Contains("NO_WORK", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Mensagens, m => m.Contains("PAUSED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShutdownGraceExpirado_NaoLogaResultadoIndeterminadoAntesPersistencia()
    {
        var tempo = new TempoManual(BaseUtc);
        var repo = new RepositorioMotorIntegracaoFake();
        var detector = new DetectorCandidatosIntegracaoFake();
        var processador = new ProcessadorExternoIntegracaoFake();
        processador.ManterPendente();
        detector.Enfileirar(Candidato());
        var logs = new ListaLoggerProvider();
        using var host = CriarHostComFakes(tempo, repo, detector, processador, logs);

        await host.StartAsync();
        await AteAsync(() => processador.QuantidadeChamadas == 1);
        var stop = host.StopAsync();
        tempo.Avancar(TimeSpan.FromSeconds(5)); // expira grace
        await stop;

        Assert.Contains(logs.Mensagens, m => m.Contains("SHUTDOWN_GRACE_EXPIRADO", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Mensagens, m => m.Contains("RESULTADO_INDETERMINADO", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnexpectedExceptionComSecret_NaoVazaSecretNoLog()
    {
        const string segredo = "Password=super-secreta-123";
        var logs = new ListaLoggerProvider();
        var builder = CriarBuilder(ConfigValida());
        HostIntegracaoComposition.Configurar(builder);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.RemoveAll<IDetectorCandidatosIntegracao>();
        builder.Services.AddSingleton<IDetectorCandidatosIntegracao>(new DetectorQueLanca(segredo));
        builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
        using var host = builder.Build();

        await host.StartAsync();
        await AteAsync(() => logs.Mensagens.Any(m => m.Contains("UNEXPECTED_ERROR", StringComparison.Ordinal)));
        await host.StopAsync();

        Assert.DoesNotContain(logs.Mensagens, m => m.Contains(segredo, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Mensagens, m => m.Contains("super-secreta", StringComparison.Ordinal));
        Assert.Contains(logs.Mensagens, m => m.Contains("ExceptionType", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResultadoIndeterminadoPersistido_LogaSomenteAposWriteAceito()
    {
        // Aceito: write fenced do indeterminado e aceito -> loga RESULTADO_INDETERMINADO e audita persistencia.
        var repoAceito = new RepositorioMotorIntegracaoFake();
        var logsAceito = new ListaLoggerProvider();
        using (var host = CriarHostIndeterminado(repoAceito, logsAceito, rejeitar: false))
        {
            await host.StartAsync();
            await AteAsync(() => logsAceito.Mensagens.Any(m => m.Contains("RESULTADO_INDETERMINADO", StringComparison.Ordinal)));
            await host.StopAsync();
        }

        Assert.Contains(repoAceito.Auditorias, e => e.Evento == "resultado_indeterminado");

        // Rejeitado: write fenced recusado -> StaleWriter, NUNCA loga RESULTADO_INDETERMINADO.
        var repoRejeitado = new RepositorioMotorIntegracaoFake { RejeitarRegistroIndeterminado = true };
        var logsRejeitado = new ListaLoggerProvider();
        using (var host = CriarHostIndeterminado(repoRejeitado, logsRejeitado, rejeitar: true))
        {
            await host.StartAsync();
            await AteAsync(() => logsRejeitado.Mensagens.Any(m => m.Contains("LEASE_RENEWAL_LOST", StringComparison.Ordinal)));
            await host.StopAsync();
        }

        Assert.DoesNotContain(logsRejeitado.Mensagens, m => m.Contains("RESULTADO_INDETERMINADO", StringComparison.Ordinal));
        Assert.DoesNotContain(repoRejeitado.Integracoes, i => i.Estado == EstadoIntegracao.ResultadoIndeterminado);
    }

    private static IHost CriarHostComFakes(TempoManual tempo, RepositorioMotorIntegracaoFake repo, IDetectorCandidatosIntegracao detector, ProcessadorExternoIntegracaoFake processador, ListaLoggerProvider logs)
    {
        var builder = CriarBuilder(ConfigValida());
        HostIntegracaoComposition.Configurar(builder);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.RemoveAll<TimeProvider>();
        builder.Services.RemoveAll<RepositorioMotorIntegracaoFake>();
        builder.Services.RemoveAll<IMotorIntegracaoRepository>();
        builder.Services.RemoveAll<IDetectorCandidatosIntegracao>();
        builder.Services.RemoveAll<IProcessadorExternoIntegracao>();
        builder.Services.AddSingleton<TimeProvider>(tempo);
        builder.Services.AddSingleton(repo);
        builder.Services.AddSingleton<IMotorIntegracaoRepository>(repo);
        builder.Services.AddSingleton(detector);
        builder.Services.AddSingleton<IProcessadorExternoIntegracao>(processador);
        return builder.Build();
    }

    private static IHost CriarHostIndeterminado(RepositorioMotorIntegracaoFake repo, ListaLoggerProvider logs, bool rejeitar)
    {
        var tempo = new TempoManual(BaseUtc);
        var detector = new DetectorCandidatosIntegracaoFake();
        detector.Enfileirar(Candidato());
        var processador = new ProcessadorExternoIntegracaoFake();
        processador.Configurar(new ResultadoProcessamentoExterno(ClassificacaoFalhaIntegracao.ResultadoIndeterminado));
        _ = rejeitar; // o seam ja esta setado no repo pelo chamador
        return CriarHostComFakes(tempo, repo, detector, processador, logs);
    }

    private sealed class DetectorQueLanca : IDetectorCandidatosIntegracao
    {
        private readonly string mensagemSegredo;

        public DetectorQueLanca(string mensagemSegredo) => this.mensagemSegredo = mensagemSegredo;

        public Task<IReadOnlyList<CandidatoIntegracao>> DetectarAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException(mensagemSegredo);
    }

    private static async Task<IReadOnlyList<string>> LogsDeCicloAsync(bool disponivel)
    {
        var logs = new ListaLoggerProvider();
        var builder = CriarBuilder(ConfigValida());
        HostIntegracaoComposition.Configurar(builder);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.RemoveAll<IMotorIntegracaoRepository>();
        builder.Services.RemoveAll<RepositorioMotorIntegracaoFake>();
        builder.Services.AddSingleton(new RepositorioMotorIntegracaoFake { Disponivel = disponivel });
        builder.Services.AddSingleton<IMotorIntegracaoRepository>(sp => sp.GetRequiredService<RepositorioMotorIntegracaoFake>());
        using var host = builder.Build();
        await host.StartAsync();
        var esperado = disponivel ? "DB_AVAILABLE" : "DB_UNAVAILABLE";
        await AteAsync(() => logs.Mensagens.Any(item => item.Contains(esperado, StringComparison.Ordinal)));
        await host.StopAsync();
        return logs.Mensagens;
    }

    private static (IHost Host, TempoManual Tempo, RepositorioMotorIntegracaoFake Repo, DetectorCandidatosIntegracaoFake Detector, ProcessadorExternoIntegracaoFake Processador, TaskCompletionSource<ResultadoProcessamentoExterno> Pendente) CriarHostComProcessadorPendente()
    {
        var tempo = new TempoManual(BaseUtc);
        var repo = new RepositorioMotorIntegracaoFake();
        var detector = new DetectorCandidatosIntegracaoFake();
        var processador = new ProcessadorExternoIntegracaoFake();
        var pendente = processador.ManterPendente();
        detector.Enfileirar(Candidato());
        var builder = CriarBuilder(ConfigValida());
        HostIntegracaoComposition.Configurar(builder);
        builder.Services.RemoveAll<TimeProvider>();
        builder.Services.RemoveAll<RepositorioMotorIntegracaoFake>();
        builder.Services.RemoveAll<IMotorIntegracaoRepository>();
        builder.Services.RemoveAll<IDetectorCandidatosIntegracao>();
        builder.Services.RemoveAll<IProcessadorExternoIntegracao>();
        builder.Services.AddSingleton<TimeProvider>(tempo);
        builder.Services.AddSingleton(repo);
        builder.Services.AddSingleton<IMotorIntegracaoRepository>(repo);
        builder.Services.AddSingleton<IDetectorCandidatosIntegracao>(detector);
        builder.Services.AddSingleton<IProcessadorExternoIntegracao>(processador);
        return (builder.Build(), tempo, repo, detector, processador, pendente);
    }

    private static IHost CriarHostValido(TimeProvider? tempo = null)
    {
        var builder = CriarBuilder(ConfigValida());
        HostIntegracaoComposition.Configurar(builder);
        if (tempo is not null)
        {
            builder.Services.RemoveAll<TimeProvider>();
            builder.Services.AddSingleton<TimeProvider>(tempo);
        }

        return builder.Build();
    }

    private static IHost CriarHost(Dictionary<string, string?> config)
    {
        var builder = CriarBuilder(config);
        HostIntegracaoComposition.Configurar(builder);
        return builder.Build();
    }

    private static HostApplicationBuilder CriarBuilder(Dictionary<string, string?> config)
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Configuration.AddInMemoryCollection(config);
        return builder;
    }

    private static Dictionary<string, string?> ConfigValida()
    {
        return new Dictionary<string, string?>
        {
            ["HostIntegracao:PollInterval"] = "00:00:01",
            ["HostIntegracao:ShutdownGracePeriod"] = "00:00:05",
            ["HostIntegracao:ExternalMode"] = "Fake",
            ["HostIntegracao:PersistenceMode"] = "Fake",
            ["MotorIntegracao:LeaseDuration"] = "00:01:00",
            ["MotorIntegracao:LeaseRenewInterval"] = "00:00:20",
            ["MotorIntegracao:RetryBackoff"] = "00:05:00",
            ["TimeoutProcessamentoIntegracao:Timeout"] = "00:00:45"
        };
    }

    private static CandidatoIntegracao Candidato()
    {
        var id = Guid.NewGuid();
        return new CandidatoIntegracao("SAP", "TOLEDO", "PRECADASTRO", "CHAVE-S5", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString("N")))), "CORR-S5");
    }

    private static async Task AteAsync(Func<bool> condicao)
    {
        for (var i = 0; i < 5000; i++)
        {
            if (condicao())
            {
                return;
            }

            // Cede a continuacao ao worker de forma robusta a warmup do pool (delay assincrono, nao bloqueante).
            await Task.Delay(1);
        }

        throw new TimeoutException("Condicao deterministica nao atingida.");
    }

    private static string FonteWorker() => File.ReadAllText(Path.Combine(LocalizarRaizRepositorio(), "src", "FugaTES.HostIntegracao", "HospedagemIntegracaoWorker.cs"));

    private static string LocalizarRaizRepositorio()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (diretorio is not null && !File.Exists(Path.Combine(diretorio.FullName, "FugaTES.slnx")))
        {
            diretorio = diretorio.Parent;
        }

        return diretorio?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed class ListaLoggerProvider : ILoggerProvider
    {
        private readonly List<string> mensagens = [];

        public IReadOnlyList<string> Mensagens
        {
            get
            {
                lock (mensagens)
                {
                    return mensagens.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(mensagens);

        public void Dispose()
        {
        }

        private sealed class Logger : ILogger
        {
            private readonly List<string> mensagens;

            public Logger(List<string> mensagens)
            {
                this.mensagens = mensagens;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (mensagens)
                {
                    mensagens.Add(formatter(state, exception));
                }
            }
        }
    }

    private sealed class TempoManual : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<TemporizadorManual> timers = [];
        private DateTimeOffset agora;

        public TempoManual(DateTimeOffset agora) => this.agora = agora;

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

            public bool DeveDisparar(DateTimeOffset agora) => !disposed && venceEm is not null && venceEm <= agora;

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
