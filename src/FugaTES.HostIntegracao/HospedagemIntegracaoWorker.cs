using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using Microsoft.Extensions.Options;

namespace FugaTES.HostIntegracao;

public sealed class HospedagemIntegracaoWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly TimeProvider timeProvider;
    private readonly HostIntegracaoOptions options;
    private readonly IIdentidadeWorkerProvider identidadeWorkerProvider;
    private readonly ILogger<HospedagemIntegracaoWorker> logger;
    private readonly object gate = new();
    private CancellationTokenSource? activeWorkCts;
    private Task? activeWorkTask;
    private bool stopRequested;
    private bool? dbDisponivelAnterior;

    public HospedagemIntegracaoWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<HostIntegracaoOptions> options,
        IIdentidadeWorkerProvider identidadeWorkerProvider,
        ILogger<HospedagemIntegracaoWorker> logger)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        this.identidadeWorkerProvider = identidadeWorkerProvider ?? throw new ArgumentNullException(nameof(identidadeWorkerProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (!this.options.EhValida())
        {
            throw new ArgumentException("Configuracao invalida para o worker do motor de integracao.", nameof(options));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = identidadeWorkerProvider.Obter().Valor;
        logger.LogInformation("HOST_START WorkerId={WorkerId}", workerId);
        logger.LogInformation("WORKER_ID WorkerId={WorkerId}", workerId);

        while (!stoppingToken.IsCancellationRequested && !stopRequested)
        {
            try
            {
                logger.LogInformation("CYCLE_START WorkerId={WorkerId}", workerId);
                var resultado = await ExecutarCicloComTokenAtivoAsync(stoppingToken);
                LogarResultadoCiclo(workerId, resultado);
                logger.LogInformation("CYCLE_END WorkerId={WorkerId} Resultado={Resultado}", workerId, resultado);
            }
            catch (OperationCanceledException) when (stopRequested || stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // F27: nao entregar a exception inteira ao provider (poderia carregar Message/payload/segredo).
                // Estrutura sanitizada: apenas ExceptionType + WorkerId. ex.Message nunca e logada.
                logger.LogError("UNEXPECTED_ERROR WorkerId={WorkerId} ExceptionType={ExceptionType}", workerId, ex.GetType().FullName);
                throw;
            }

            if (stopRequested || stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await Task.Delay(options.PollInterval, timeProvider, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // F7 (JANUS-005B-SEC-004): stopRequested e a captura do trabalho ativo compartilham a MESMA
        // fronteira sincronizada usada pela decisao de iniciar/reservar trabalho novo. Sem janela invisivel.
        Task? trabalhoAtivo;
        CancellationTokenSource? ctsAtivo;
        lock (gate)
        {
            stopRequested = true;
            trabalhoAtivo = activeWorkTask;
            ctsAtivo = activeWorkCts;
        }

        logger.LogInformation("HOST_STOP GracePeriod={ShutdownGracePeriod}", options.ShutdownGracePeriod);

        if (trabalhoAtivo is not null && !trabalhoAtivo.IsCompleted)
        {
            var grace = Task.Delay(options.ShutdownGracePeriod, timeProvider, cancellationToken);
            var concluida = await Task.WhenAny(trabalhoAtivo, grace);
            if (concluida == grace)
            {
                ctsAtivo?.Cancel();
                // F26: grace expirado NAO significa resultado indeterminado persistido; o recovery decidira.
                logger.LogWarning("SHUTDOWN_GRACE_EXPIRADO ACTIVE_WORK_CANCELADO RECOVERY_PENDENTE");
            }
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task<CicloMotorIntegracaoResultado> ExecutarCicloComTokenAtivoAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var motor = scope.ServiceProvider.GetRequiredService<MotorIntegracaoServico>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task<CicloMotorIntegracaoResultado> tarefa;

        // F7/§22: verificar stopRequested, reservar o slot, associar o CTS ativo e iniciar/rastrear a Task
        // sao ATOMICOS sob a MESMA fronteira que StopAsync usa para fixar stopRequested. Sem janela invisivel.
        // O CTS e encadeado ao stoppingToken para que um shutdown forcado propague o cancelamento.
        lock (gate)
        {
            if (stopRequested || stoppingToken.IsCancellationRequested)
            {
                return CicloMotorIntegracaoResultado.NenhumTrabalho;
            }

            tarefa = motor.ExecutarUmCicloAsync(cts.Token);
            activeWorkCts = cts;
            activeWorkTask = tarefa;
        }

        try
        {
            return await tarefa;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(activeWorkTask, tarefa))
                {
                    activeWorkTask = null;
                    activeWorkCts = null;
                }
            }
        }
    }

    private void LogarResultadoCiclo(string workerId, CicloMotorIntegracaoResultado resultado)
    {
        switch (resultado)
        {
            case CicloMotorIntegracaoResultado.DegradedDb:
                LogarTransicaoDb(workerId, disponivel: false);
                break;
            case CicloMotorIntegracaoResultado.NenhumTrabalho:
                LogarTransicaoDb(workerId, disponivel: true);
                // F25: fila vazia nao e pausa. O motor nao distingue pausa aqui, entao nunca afirma PAUSED.
                logger.LogInformation("NO_WORK WorkerId={WorkerId} Resultado={Resultado}", workerId, resultado);
                break;
            case CicloMotorIntegracaoResultado.ValidacaoReprovada:
                LogarTransicaoDb(workerId, disponivel: true);
                logger.LogWarning("VALIDACAO_REPROVADA WorkerId={WorkerId}", workerId);
                break;
            case CicloMotorIntegracaoResultado.StaleWriter:
                LogarTransicaoDb(workerId, disponivel: true);
                logger.LogWarning("LEASE_RENEWAL_LOST WorkerId={WorkerId}", workerId);
                break;
            case CicloMotorIntegracaoResultado.ResultadoIndeterminado:
                LogarTransicaoDb(workerId, disponivel: true);
                logger.LogWarning("RESULTADO_INDETERMINADO WorkerId={WorkerId}", workerId);
                break;
            default:
                LogarTransicaoDb(workerId, disponivel: true);
                logger.LogInformation("ACTIVE WorkerId={WorkerId} Resultado={Resultado}", workerId, resultado);
                break;
        }
    }

    private void LogarTransicaoDb(string workerId, bool disponivel)
    {
        if (dbDisponivelAnterior == disponivel)
        {
            return;
        }

        dbDisponivelAnterior = disponivel;
        logger.LogInformation(disponivel ? "DB_AVAILABLE WorkerId={WorkerId}" : "DB_UNAVAILABLE WorkerId={WorkerId}", workerId);
    }
}
