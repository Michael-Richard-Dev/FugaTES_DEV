using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;

namespace FugaTES.Aplicacao.Servicos.MotorIntegracao;

public sealed class MotorIntegracaoServico
{
    private readonly IMotorIntegracaoRepository repository;
    private readonly IProcessadorExternoIntegracao processador;
    private readonly IDetectorCandidatosIntegracao detector;
    private readonly IValidadorIntegracao validador;
    private readonly IIdentidadeWorkerProvider identidadeWorkerProvider;
    private readonly IPoliticaTimeoutProcessamentoIntegracao politicaTimeout;
    private readonly TimeProvider timeProvider;
    private readonly MotorIntegracaoOptions options;

    public MotorIntegracaoServico(
        IMotorIntegracaoRepository repository,
        IProcessadorExternoIntegracao processador,
        IDetectorCandidatosIntegracao detector,
        IValidadorIntegracao validador,
        IIdentidadeWorkerProvider identidadeWorkerProvider,
        IPoliticaTimeoutProcessamentoIntegracao politicaTimeout,
        TimeProvider timeProvider,
        MotorIntegracaoOptions options)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.processador = processador ?? throw new ArgumentNullException(nameof(processador));
        this.detector = detector ?? throw new ArgumentNullException(nameof(detector));
        this.validador = validador ?? throw new ArgumentNullException(nameof(validador));
        this.identidadeWorkerProvider = identidadeWorkerProvider ?? throw new ArgumentNullException(nameof(identidadeWorkerProvider));
        this.politicaTimeout = politicaTimeout ?? throw new ArgumentNullException(nameof(politicaTimeout));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.options = options ?? throw new ArgumentNullException(nameof(options));

        if (!options.EhValida())
        {
            throw new ArgumentException("Configuracao invalida para o motor de integracao offline.", nameof(options));
        }
    }

    public async Task<CicloMotorIntegracaoResultado> ExecutarUmCicloAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!await repository.EstaDisponivelAsync(cancellationToken))
        {
            return CicloMotorIntegracaoResultado.DegradedDb;
        }

        var agora = timeProvider.GetUtcNow();
        await repository.RecuperarLeasesExpiradasAsync(agora, cancellationToken);

        foreach (var candidato in await detector.DetectarAsync(cancellationToken))
        {
            await repository.RegistrarCandidatoAsync(candidato, agora, cancellationToken);
        }

        var elegiveis = await repository.SelecionarCandidatosPreClaimAsync(agora, cancellationToken);
        var worker = identidadeWorkerProvider.Obter();
        var houveReprovacao = false;

        foreach (var integracao in elegiveis)
        {
            var validacao = validador.Validar(integracao);
            if (!validacao.Aprovada)
            {
                await repository.RegistrarValidacaoReprovadaAsync(
                    integracao.Id,
                    integracao.CorrelationId,
                    validacao.DiagnosticoSanitizado ?? "validacao-reprovada",
                    agora,
                    cancellationToken);
                houveReprovacao = true;
                continue;
            }

            var claim = await repository.ClaimarEspecificamenteAsync(
                integracao.Id,
                integracao.Estado,
                integracao.TipoDisparoPendente,
                integracao.FingerprintSha256,
                worker,
                options.LeaseDuration,
                agora,
                cancellationToken);

            if (claim is null)
            {
                continue;
            }

            var envioMarcado = await repository.MarcarEnvioIniciadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, timeProvider.GetUtcNow(), cancellationToken);
            if (!envioMarcado)
            {
                return CicloMotorIntegracaoResultado.EnvioNaoMarcado;
            }

            return await ProcessarComHeartbeatAsync(claim, cancellationToken);
        }

        return houveReprovacao ? CicloMotorIntegracaoResultado.ValidacaoReprovada : CicloMotorIntegracaoResultado.NenhumTrabalho;
    }

    private async Task<CicloMotorIntegracaoResultado> ProcessarComHeartbeatAsync(ClaimIntegracaoMotor claim, CancellationToken cancellationToken)
    {
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeoutCts = new CancellationTokenSource();
        using var processorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, heartbeatCts.Token);
        var heartbeat = ManterHeartbeatAsync(claim, heartbeatCts, processorCts, heartbeatCts.Token);
        var timeoutTask = Task.Delay(politicaTimeout.ObterTimeout(claim), timeProvider, timeoutCts.Token);

        try
        {
            var processamento = processador.ProcessarAsync(claim, processorCts.Token);
            var concluida = await Task.WhenAny(processamento, timeoutTask);
            if (concluida == timeoutTask)
            {
                timeoutCts.Cancel();
                return await FinalizarIndeterminadoAsync(claim);
            }

            var resultado = await processamento;

            // F1: resultado indeterminado do processador NAO segue o caminho de resultado conhecido.
            if (resultado.Falha == ClassificacaoFalhaIntegracao.ResultadoIndeterminado)
            {
                return await FinalizarIndeterminadoAsync(claim);
            }

            var persistido = await repository.PersistirResultadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, resultado, timeProvider.GetUtcNow(), options.RetryBackoff, cancellationToken);
            return persistido ? CicloMotorIntegracaoResultado.Processado : CicloMotorIntegracaoResultado.StaleWriter;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return await FinalizarIndeterminadoAsync(claim);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            processorCts.Cancel();
            return CicloMotorIntegracaoResultado.CanceladoExterno;
        }
        catch (OperationCanceledException)
        {
            return CicloMotorIntegracaoResultado.StaleWriter;
        }
        catch
        {
            return await FinalizarIndeterminadoAsync(claim);
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task ManterHeartbeatAsync(ClaimIntegracaoMotor claim, CancellationTokenSource heartbeatCts, CancellationTokenSource processorCts, CancellationToken cancellationToken)
    {
        while (!heartbeatCts.IsCancellationRequested)
        {
            await Task.Delay(options.LeaseRenewInterval, timeProvider, heartbeatCts.Token);
            var renovado = await repository.RenovarLeaseAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, options.LeaseDuration, timeProvider.GetUtcNow(), cancellationToken);
            if (!renovado)
            {
                processorCts.Cancel();
                heartbeatCts.Cancel();
                return;
            }
        }
    }

    // F8: write fenced do indeterminado; se rejeitado (false), o ciclo devolve StaleWriter (fail-closed),
    // nunca declara ResultadoIndeterminado sem persistencia aceita.
    private async Task<CicloMotorIntegracaoResultado> FinalizarIndeterminadoAsync(ClaimIntegracaoMotor claim)
    {
        var registrado = await repository.RegistrarResultadoIndeterminadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, timeProvider.GetUtcNow(), CancellationToken.None);
        return registrado ? CicloMotorIntegracaoResultado.ResultadoIndeterminado : CicloMotorIntegracaoResultado.StaleWriter;
    }

}

public enum CicloMotorIntegracaoResultado
{
    NenhumTrabalho,
    DegradedDb,
    EnvioNaoMarcado,
    Processado,
    StaleWriter,
    ValidacaoReprovada,
    ResultadoIndeterminado,
    CanceladoExterno
}
