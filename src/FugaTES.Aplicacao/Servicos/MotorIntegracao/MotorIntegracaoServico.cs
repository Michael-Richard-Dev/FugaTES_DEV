using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;

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
    private readonly IContratoSapToledoVersaoStore? contratos;

    public MotorIntegracaoServico(
        IMotorIntegracaoRepository repository,
        IProcessadorExternoIntegracao processador,
        IDetectorCandidatosIntegracao detector,
        IValidadorIntegracao validador,
        IIdentidadeWorkerProvider identidadeWorkerProvider,
        IPoliticaTimeoutProcessamentoIntegracao politicaTimeout,
        TimeProvider timeProvider,
        MotorIntegracaoOptions options,
        IContratoSapToledoVersaoStore? contratos = null)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.processador = processador ?? throw new ArgumentNullException(nameof(processador));
        this.detector = detector ?? throw new ArgumentNullException(nameof(detector));
        this.validador = validador ?? throw new ArgumentNullException(nameof(validador));
        this.identidadeWorkerProvider = identidadeWorkerProvider ?? throw new ArgumentNullException(nameof(identidadeWorkerProvider));
        this.politicaTimeout = politicaTimeout ?? throw new ArgumentNullException(nameof(politicaTimeout));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.contratos = contratos;

        if (!options.EhValida())
        {
            throw new ArgumentException("Configuracao invalida para o motor de integracao offline.", nameof(options));
        }
        if (options.UsaEscopoAcm && contratos is null)
            throw new InvalidOperationException("ACM_PILOT_DURABLE_H4_REQUIRED");
    }

    public async Task<CicloMotorIntegracaoResultado> ExecutarUmCicloAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!await repository.EstaDisponivelAsync(cancellationToken))
        {
            return CicloMotorIntegracaoResultado.DegradedDb;
        }

        var agora = timeProvider.GetUtcNow();
        if (options.UsaJanelaAcm && !options.AcmInboundWindow.EstaAberta(agora))
            return CicloMotorIntegracaoResultado.NenhumTrabalho;
        // A scoped pilot must not recover or ingest unrelated backlog as a side effect.
        if (!options.UsaEscopoAcm)
        {
            await repository.RecuperarLeasesExpiradasAsync(agora, cancellationToken);
            foreach (var candidato in await detector.DetectarAsync(cancellationToken))
                await repository.RegistrarCandidatoAsync(candidato, agora, cancellationToken);
        }

        var elegiveis = await repository.SelecionarCandidatosPreClaimAsync(agora, cancellationToken);
        var worker = identidadeWorkerProvider.Obter();
        var houveReprovacao = false;

        foreach (var integracao in elegiveis)
        {
            if (!await DentroDoEscopoAsync(integracao, cancellationToken)) continue;
            var resultado = await TentarProcessarAsync(integracao, worker, agora, cancellationToken);
            if (resultado == CicloMotorIntegracaoResultado.ValidacaoReprovada) { houveReprovacao = true; continue; }
            if (resultado == CicloMotorIntegracaoResultado.ClaimNaoAdquirido) continue;
            return resultado;
        }

        return houveReprovacao ? CicloMotorIntegracaoResultado.ValidacaoReprovada : CicloMotorIntegracaoResultado.NenhumTrabalho;
    }

    public async Task<CicloMotorIntegracaoResultado> ExecutarPorIdAsync(Guid integracaoId, CancellationToken cancellationToken)
    {
        var integracao = await repository.ObterPorIdAsync(integracaoId, cancellationToken);
        if (integracao is null || !await DentroDoEscopoAsync(integracao, cancellationToken))
            return CicloMotorIntegracaoResultado.NenhumTrabalho;
        return await TentarProcessarAsync(integracao, identidadeWorkerProvider.Obter(), timeProvider.GetUtcNow(), cancellationToken);
    }

    public bool ManualRetryScopeMatches(string contract,string? item,string worker)
        =>options.UsaEscopoAcm&&!options.UsaJanelaAcm&&options.AcmPilotNumeroContrato==contract&&
            options.AcmPilotItem==item&&options.AcmPilotCentro=="3011"&&identidadeWorkerProvider.Obter().Valor==worker;

    private async Task<bool> DentroDoEscopoAsync(IntegracaoMotor integracao, CancellationToken ct)
    {
        if (!options.UsaEscopoAcm) return true;
        if (integracao.Origem != "SAP" || integracao.Destino != "TOLEDO" ||
            integracao.Operacao != "SAP_CONTRATO_TOLEDO")
            return false;
        if (options.UsaJanelaAcm)
        {
            if (!options.AcmInboundWindow.EstaAberta(timeProvider.GetUtcNow()) ||
                !options.AcmInboundWindow.CorrelationConfere(integracao.CorrelationId)) return false;
        }
        // R170: no recorte continuo nao ha caixa de tempo nem prefixo de correlacao - o contrato ja
        // chegou pelo receptor comum. O que delimita e a regra de negocio sobre o snapshot, abaixo.
        else if (options.UsaAutomaticoAcm) { }
        else if (integracao.ChaveNegocio != options.AcmPilotNumeroContrato) return false;
        if (contratos is not { } store) return false;
        var version = await store.ObterPorIntegracaoIdAsync(integracao.Id, ct);
        var identidadeConfere = version is not null && version.IntegracaoId == integracao.Id &&
            version.NumeroContrato == integracao.ChaveNegocio &&
            version.FingerprintSha256 == integracao.FingerprintSha256 &&
            version.Snapshot.NumeroContrato == version.NumeroContrato;
        if (!identidadeConfere) return false;
        if (!options.UsaJanelaAcm && !options.UsaAutomaticoAcm)
            return version!.Snapshot.Centro == options.AcmPilotCentro && version.Snapshot.Item == options.AcmPilotItem;
        if (version!.Versao != 1) return false;
        // Corte do cadastro automatico. Fica aqui, no recorte, e nao num bloqueio: contrato de
        // antes do corte nao e trabalho deste motor, e nao-trabalho nao gera recusa - a fila antiga
        // deixa de ser varrida em vez de acumular tentativa e backoff para sempre.
        if (options.UsaAutomaticoAcm && !options.AcmAutomatico.RecebimentoNoCorte(version.RecebidoEmUtc)) return false;
        if (options.UsaAutomaticoAcm && !options.AcmAutomatico.SnapshotPermitido(version.Snapshot)) return false;
        if (options.UsaJanelaAcm && !options.AcmInboundWindow.SnapshotPermitido(version.Snapshot)) return false;
        try { return new ContratoSapToledoCanonicalizer().Canonicalizar(version.Snapshot).FingerprintSha256 == version.FingerprintSha256; }
        catch { return false; }
    }

    private async Task<CicloMotorIntegracaoResultado> TentarProcessarAsync(IntegracaoMotor integracao, IdentidadeWorkerIntegracao worker, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var validacao = validador.Validar(integracao);
        if (!validacao.Aprovada) { await repository.RegistrarValidacaoReprovadaAsync(integracao.Id, integracao.CorrelationId, validacao.DiagnosticoSanitizado ?? "validacao-reprovada", agora, cancellationToken); return CicloMotorIntegracaoResultado.ValidacaoReprovada; }
        if (!await DentroDoEscopoAsync(integracao, cancellationToken)) return CicloMotorIntegracaoResultado.NenhumTrabalho;
        var claim = await repository.ClaimarEspecificamenteAsync(integracao.Id, integracao.Estado, integracao.TipoDisparoPendente, integracao.FingerprintSha256, worker, options.LeaseDuration, agora, cancellationToken);
        if (claim is null) return CicloMotorIntegracaoResultado.ClaimNaoAdquirido;
        if (!await repository.MarcarEnvioIniciadoAsync(claim.IntegracaoId, claim.TentativaId, claim.FencingToken, claim.Worker, timeProvider.GetUtcNow(), cancellationToken)) return CicloMotorIntegracaoResultado.EnvioNaoMarcado;
        return await ProcessarComHeartbeatAsync(claim, cancellationToken);
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
    ,ClaimNaoAdquirido
}
