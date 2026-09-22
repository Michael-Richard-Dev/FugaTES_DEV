using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;

namespace FugaTES.Infraestrutura.MotorIntegracao;

public sealed class RepositorioMotorIntegracaoFake : IMotorIntegracaoRepository
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, IntegracaoMotor> integracoes = [];
    private readonly List<TentativaIntegracaoMotor> tentativas = [];
    private readonly List<AuditoriaIntegracaoMotor> auditorias = [];
    private readonly List<PausaProcessamentoMotor> pausas = [];

    public bool Disponivel { get; set; } = true;

    public bool FalharAntesCommitClaim { get; set; }

    public bool FalharAposMutacaoClaimAntesTentativa { get; set; }

    // Seam de teste: forca o write fenced do indeterminado a ser rejeitado (fail-closed).
    public bool RejeitarRegistroIndeterminado { get; set; }

    public bool TransacaoAtiva { get; private set; }

    public IReadOnlyCollection<IntegracaoMotor> Integracoes => integracoes.Values.ToArray();

    public IReadOnlyCollection<TentativaIntegracaoMotor> Tentativas => tentativas.ToArray();

    public IReadOnlyCollection<AuditoriaIntegracaoMotor> Auditorias => auditorias.ToArray();

    public IReadOnlyCollection<PausaProcessamentoMotor> Pausas => pausas.ToArray();

    public void Semear(IntegracaoMotor integracao)
    {
        lock (gate)
        {
            if (integracoes.Values.Any(item => item.FingerprintSha256 == integracao.FingerprintSha256))
            {
                throw new InvalidOperationException("Fingerprint ja registrado para integracao logica.");
            }

            integracoes.Add(integracao.Id, integracao);
        }
    }

    public Task<bool> EstaDisponivelAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(Disponivel);
    }

    public Task<bool> ExistePausaAplicavelAsync(string origem, string destino, string operacao, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(ExistePausaAplicavelBloqueada(origem, destino, operacao));
        }
    }

    public Task<ResultadoRegistroIntegracao> RegistrarCandidatoAsync(CandidatoIntegracao candidato, DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidato);
        lock (gate)
        {
            var existente = integracoes.Values.FirstOrDefault(item => item.FingerprintSha256 == candidato.FingerprintSha256);
            if (existente is not null)
            {
                return Task.FromResult(ResultadoRegistroIntegracao.JaExistente);
            }

            var novaId = Guid.NewGuid();
            var integracao = new IntegracaoMotor(novaId, candidato.Origem, candidato.Destino, candidato.Operacao, candidato.ChaveNegocio, candidato.FingerprintSha256, candidato.CorrelationId, EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, agoraUtc, 0, null, null, FaseEnvioIntegracao.NaoIniciado, null);
            integracoes.Add(novaId, integracao);
            RegistrarAuditoria(novaId, "registrada", agoraUtc, null, null, null, candidato.CorrelationId);
            return Task.FromResult(ResultadoRegistroIntegracao.Criada);
        }
    }

    public Task<IReadOnlyList<IntegracaoMotor>> SelecionarCandidatosPreClaimAsync(DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            IReadOnlyList<IntegracaoMotor> elegiveis = integracoes.Values
                .Where(integracao => Elegivel(integracao, agoraUtc)
                    && !ExistePausaAplicavelBloqueada(integracao.Origem, integracao.Destino, integracao.Operacao))
                .OrderBy(integracao => integracao.Id)
                .ToArray();
            return Task.FromResult(elegiveis);
        }
    }

    public Task<IntegracaoMotor?> ObterPorIdAsync(Guid integracaoId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(integracoes.GetValueOrDefault(integracaoId));
    }

    public Task<ClaimIntegracaoMotor?> ClaimarEspecificamenteAsync(Guid integracaoId, EstadoIntegracao estadoEsperado, TipoDisparoPendente? tipoDisparoEsperado, string fingerprintEsperado, IdentidadeWorkerIntegracao worker, TimeSpan leaseDuration, DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            TransacaoAtiva = true;
            try
            {
                if (FalharAntesCommitClaim)
                {
                    return Task.FromResult<ClaimIntegracaoMotor?>(null);
                }

                if (!integracoes.TryGetValue(integracaoId, out var atual)
                    || atual.FingerprintSha256 != fingerprintEsperado
                    || atual.Estado != estadoEsperado
                    || atual.TipoDisparoPendente != tipoDisparoEsperado
                    || !Elegivel(atual, agoraUtc)
                    || ExistePausaAplicavelBloqueada(atual.Origem, atual.Destino, atual.Operacao))
                {
                    return Task.FromResult<ClaimIntegracaoMotor?>(null);
                }

                return Task.FromResult<ClaimIntegracaoMotor?>(ExecutarClaimAtomico(atual, worker, leaseDuration, agoraUtc));
            }
            finally
            {
                TransacaoAtiva = false;
            }
        }
    }

    public Task<bool> RegistrarValidacaoReprovadaAsync(Guid integracaoId, string correlationId, string diagnosticoSanitizado, DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!integracoes.ContainsKey(integracaoId))
            {
                return Task.FromResult(false);
            }

            RegistrarAuditoria(integracaoId, "VALIDACAO_REPROVADA", agoraUtc, null, null, null, correlationId, diagnosticoSanitizado);
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarcarEnvioIniciadoAsync(Guid integracaoId, Guid tentativaId, long fencingToken, string worker, DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            TransacaoAtiva = true;
            try
            {
                if (!ValidarOwnership(integracaoId, tentativaId, fencingToken, worker, EstadoIntegracao.EmProcessamento, agoraUtc)
                    || integracoes[integracaoId].FaseEnvio != FaseEnvioIntegracao.NaoIniciado)
                {
                    RegistrarAuditoria(integracaoId, "marker_rejeitado", agoraUtc, worker, fencingToken, tentativaId, null);
                    return Task.FromResult(false);
                }

                var atual = integracoes[integracaoId];
                integracoes[integracaoId] = atual with { FaseEnvio = FaseEnvioIntegracao.EnvioIniciado };
                AtualizarTentativa(tentativaId, item => item with { FaseEnvio = FaseEnvioIntegracao.EnvioIniciado, EnvioIniciadoEm = agoraUtc });
                RegistrarAuditoria(integracaoId, "envio_iniciado", agoraUtc, worker, fencingToken, tentativaId, atual.CorrelationId);
                return Task.FromResult(true);
            }
            finally
            {
                TransacaoAtiva = false;
            }
        }
    }

    public Task<bool> RenovarLeaseAsync(Guid integracaoId, Guid tentativaId, long fencingToken, string worker, TimeSpan leaseDuration, DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            TransacaoAtiva = true;
            try
            {
                if (!ValidarOwnership(integracaoId, tentativaId, fencingToken, worker, EstadoIntegracao.EmProcessamento, agoraUtc))
                {
                    RegistrarAuditoria(integracaoId, "lease_renew_rejeitado", agoraUtc, worker, fencingToken, tentativaId, null);
                    return Task.FromResult(false);
                }

                var atual = integracoes[integracaoId];
                integracoes[integracaoId] = atual with { LeaseExpiraEm = agoraUtc.Add(leaseDuration) };
                RegistrarAuditoria(integracaoId, "lease_renew", agoraUtc, worker, fencingToken, tentativaId, atual.CorrelationId);
                return Task.FromResult(true);
            }
            finally
            {
                TransacaoAtiva = false;
            }
        }
    }

    public Task<bool> PersistirResultadoAsync(Guid integracaoId, Guid tentativaId, long fencingToken, string worker, ResultadoProcessamentoExterno resultado, DateTimeOffset agoraUtc, TimeSpan retryBackoff, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            TransacaoAtiva = true;
            try
            {
                // F7: o caminho generico de resultado conhecido nunca aceita ResultadoIndeterminado.
                // Ha uma unica fronteira dedicada (RegistrarResultadoIndeterminadoAsync).
                if (resultado.Falha == ClassificacaoFalhaIntegracao.ResultadoIndeterminado)
                {
                    RegistrarAuditoria(integracaoId, "indeterminado_no_caminho_generico_rejeitado", agoraUtc, worker, fencingToken, tentativaId, null);
                    return Task.FromResult(false);
                }

                if (!ValidarOwnership(integracaoId, tentativaId, fencingToken, worker, EstadoIntegracao.EmProcessamento, agoraUtc)
                    || integracoes[integracaoId].FaseEnvio != FaseEnvioIntegracao.EnvioIniciado)
                {
                    RegistrarAuditoria(integracaoId, "stale_writer_rejeitado", agoraUtc, worker, fencingToken, tentativaId, null);
                    return Task.FromResult(false);
                }

                var atual = integracoes[integracaoId];
                var novoEstado = MaquinaEstadoIntegracao.AplicarResultado(resultado.Falha);
                var agendaRetry = novoEstado == EstadoIntegracao.AguardandoReprocessamento;
                var desfecho = resultado.Falha is null ? DesfechoTentativaIntegracao.Sucesso : DesfechoTentativaIntegracao.Falha;
                integracoes[integracaoId] = atual with
                {
                    Estado = novoEstado,
                    TipoDisparoPendente = agendaRetry ? TipoDisparoPendente.Automatico : null,
                    ProximaTentativaEm = agendaRetry ? agoraUtc.Add(retryBackoff) : null,
                    WorkerProprietario = null,
                    LeaseExpiraEm = null,
                    FaseEnvio = FaseEnvioIntegracao.ResultadoRecebido
                };
                AtualizarTentativa(tentativaId, item => item with { FaseEnvio = FaseEnvioIntegracao.ResultadoRecebido, Desfecho = desfecho, ResultadoRecebidoEm = agoraUtc });
                RegistrarAuditoria(integracaoId, "resultado", agoraUtc, worker, fencingToken, tentativaId, atual.CorrelationId);
                RegistrarAuditoria(integracaoId, agendaRetry ? "retry_agendado" : "terminal", agoraUtc, worker, fencingToken, tentativaId, atual.CorrelationId);
                return Task.FromResult(true);
            }
            finally
            {
                TransacaoAtiva = false;
            }
        }
    }

    public Task<bool> RegistrarResultadoIndeterminadoAsync(Guid integracaoId, Guid tentativaId, long fencingToken, string worker, DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            TransacaoAtiva = true;
            try
            {
                if (RejeitarRegistroIndeterminado
                    || !ValidarOwnership(integracaoId, tentativaId, fencingToken, worker, EstadoIntegracao.EmProcessamento, agoraUtc)
                    || integracoes[integracaoId].FaseEnvio != FaseEnvioIntegracao.EnvioIniciado)
                {
                    RegistrarAuditoria(integracaoId, "indeterminado_rejeitado", agoraUtc, worker, fencingToken, tentativaId, null);
                    return Task.FromResult(false);
                }

                var atual = integracoes[integracaoId];
                integracoes[integracaoId] = atual with
                {
                    Estado = EstadoIntegracao.ResultadoIndeterminado,
                    TipoDisparoPendente = null,
                    ProximaTentativaEm = null,
                    WorkerProprietario = null,
                    LeaseExpiraEm = null
                };
                AtualizarTentativa(tentativaId, item => item with { Desfecho = DesfechoTentativaIntegracao.Indeterminada });
                RegistrarAuditoria(integracaoId, "resultado_indeterminado", agoraUtc, worker, fencingToken, tentativaId, atual.CorrelationId);
                return Task.FromResult(true);
            }
            finally
            {
                TransacaoAtiva = false;
            }
        }
    }

    public Task<bool> ReprocessarAsync(Guid integracaoId, DateTimeOffset agoraUtc, string correlationId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!integracoes.TryGetValue(integracaoId, out var atual) || !MaquinaEstadoIntegracao.PodeReprocessar(atual.Estado))
            {
                return Task.FromResult(false);
            }

            integracoes[integracaoId] = atual with { Estado = EstadoIntegracao.AguardandoReprocessamento, TipoDisparoPendente = TipoDisparoPendente.Reprocessamento, ProximaTentativaEm = agoraUtc, CorrelationId = correlationId };
            RegistrarAuditoria(integracaoId, "reprocessar", agoraUtc, null, null, null, correlationId);
            return Task.FromResult(true);
        }
    }

    public Task<bool> IntegrarAgoraAsync(Guid integracaoId, DateTimeOffset agoraUtc, string correlationId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!integracoes.TryGetValue(integracaoId, out var atual) || atual.Estado != EstadoIntegracao.Pendente)
            {
                return Task.FromResult(false);
            }

            integracoes[integracaoId] = atual with { TipoDisparoPendente = TipoDisparoPendente.IntegrarAgora, ProximaTentativaEm = agoraUtc, CorrelationId = correlationId };
            RegistrarAuditoria(integracaoId, "integrar_agora", agoraUtc, null, null, null, correlationId);
            return Task.FromResult(true);
        }
    }

    public Task<bool> CriarPausaAsync(PausaProcessamentoMotor pausa, string correlationId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!EscopoPausaCoerente(pausa) || ExistePausaVigenteEquivalente(pausa))
            {
                return Task.FromResult(false);
            }

            pausas.Add(pausa);
            RegistrarAuditoria(null, "pausa", pausa.CriadaEm, null, null, null, correlationId);
            return Task.FromResult(true);
        }
    }

    public Task<bool> RetomarPausaAsync(Guid pausaId, DateTimeOffset agoraUtc, string correlationId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var index = pausas.FindIndex(pausa => pausa.Id == pausaId && pausa.Ativa);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            pausas[index] = pausas[index] with { Ativa = false, RetomadaEm = agoraUtc };
            RegistrarAuditoria(null, "retomada", agoraUtc, null, null, null, correlationId);
            return Task.FromResult(true);
        }
    }

    public Task<int> RecuperarLeasesExpiradasAsync(DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var alteradas = 0;
            foreach (var atual in integracoes.Values.ToArray())
            {
                if (atual.Estado != EstadoIntegracao.EmProcessamento || atual.LeaseExpiraEm is null || atual.LeaseExpiraEm > agoraUtc)
                {
                    continue;
                }

                if (atual.FaseEnvio == FaseEnvioIntegracao.NaoIniciado)
                {
                    integracoes[atual.Id] = atual with { Estado = EstadoIntegracao.AguardandoReprocessamento, TipoDisparoPendente = TipoDisparoPendente.Automatico, ProximaTentativaEm = agoraUtc, WorkerProprietario = null, LeaseExpiraEm = null, TentativaAtualId = null };
                    AtualizarTentativa(atual.TentativaAtualId, item => item with { Desfecho = DesfechoTentativaIntegracao.Interrompida });
                }
                else if (atual.FaseEnvio == FaseEnvioIntegracao.EnvioIniciado)
                {
                    integracoes[atual.Id] = atual with { Estado = EstadoIntegracao.ResultadoIndeterminado, TipoDisparoPendente = null, ProximaTentativaEm = null, WorkerProprietario = null, LeaseExpiraEm = null };
                    AtualizarTentativa(atual.TentativaAtualId, item => item with { Desfecho = DesfechoTentativaIntegracao.Indeterminada });
                }
                else
                {
                    continue;
                }

                RegistrarAuditoria(atual.Id, "recovery", agoraUtc, atual.WorkerProprietario, atual.FencingToken, atual.TentativaAtualId, atual.CorrelationId);
                alteradas++;
            }

            return Task.FromResult(alteradas);
        }
    }

    private bool ExistePausaAplicavelBloqueada(string origem, string destino, string operacao)
    {
        return pausas.Any(pausa => pausa.Ativa && (pausa.Escopo == EscopoPausaProcessamento.Geral
            || (pausa.Escopo == EscopoPausaProcessamento.SistemaExterno && (pausa.SistemaExterno == origem || pausa.SistemaExterno == destino))
            || (pausa.Escopo == EscopoPausaProcessamento.Operacao && pausa.Operacao == operacao)));
    }

    private static bool EscopoPausaCoerente(PausaProcessamentoMotor pausa)
    {
        return pausa.Escopo switch
        {
            EscopoPausaProcessamento.Geral => pausa.SistemaExterno is null && pausa.Operacao is null,
            EscopoPausaProcessamento.SistemaExterno => !string.IsNullOrWhiteSpace(pausa.SistemaExterno) && pausa.Operacao is null,
            EscopoPausaProcessamento.Operacao => pausa.SistemaExterno is null && !string.IsNullOrWhiteSpace(pausa.Operacao),
            _ => false
        };
    }

    private bool ExistePausaVigenteEquivalente(PausaProcessamentoMotor pausa)
    {
        return pausa.Escopo switch
        {
            EscopoPausaProcessamento.Geral => pausas.Any(existente => existente.Ativa && existente.Escopo == EscopoPausaProcessamento.Geral),
            EscopoPausaProcessamento.SistemaExterno => pausas.Any(existente => existente.Ativa && existente.Escopo == EscopoPausaProcessamento.SistemaExterno && existente.SistemaExterno == pausa.SistemaExterno),
            EscopoPausaProcessamento.Operacao => pausas.Any(existente => existente.Ativa && existente.Escopo == EscopoPausaProcessamento.Operacao && existente.Operacao == pausa.Operacao),
            _ => false
        };
    }

    private bool ValidarOwnership(Guid integracaoId, Guid tentativaId, long fencingToken, string worker, EstadoIntegracao estadoEsperado, DateTimeOffset agoraUtc)
    {
        return integracoes.TryGetValue(integracaoId, out var atual)
            && atual.Estado == estadoEsperado
            && atual.FencingToken == fencingToken
            && atual.TentativaAtualId == tentativaId
            && atual.WorkerProprietario == worker
            && atual.LeaseExpiraEm is not null
            && atual.LeaseExpiraEm > agoraUtc;
    }

    private void AtualizarTentativa(Guid? tentativaId, Func<TentativaIntegracaoMotor, TentativaIntegracaoMotor> atualizar)
    {
        if (tentativaId is null)
        {
            return;
        }

        var index = tentativas.FindIndex(tentativa => tentativa.Id == tentativaId.Value);
        if (index >= 0)
        {
            tentativas[index] = atualizar(tentativas[index]);
        }
    }

    private void RegistrarAuditoria(Guid? integracaoId, string evento, DateTimeOffset agoraUtc, string? worker, long? fencingToken, Guid? tentativaId, string? correlationId, string? diagnostico = null)
    {
        auditorias.Add(new AuditoriaIntegracaoMotor(Guid.NewGuid(), integracaoId, evento, agoraUtc, worker, fencingToken, tentativaId, correlationId, diagnostico));
    }

    private static bool Elegivel(IntegracaoMotor integracao, DateTimeOffset agoraUtc)
    {
        return MaquinaEstadoIntegracao.PodeClaimar(integracao.Estado)
            && MaquinaEstadoIntegracao.PodeAplicarTipoDisparo(integracao.Estado, integracao.TipoDisparoPendente)
            && (integracao.LeaseExpiraEm is null || integracao.LeaseExpiraEm <= agoraUtc)
            && integracao.ProximaTentativaEm is not null
            && integracao.ProximaTentativaEm <= agoraUtc;
    }

    private ClaimIntegracaoMotor? ExecutarClaimAtomico(IntegracaoMotor candidata, IdentidadeWorkerIntegracao worker, TimeSpan leaseDuration, DateTimeOffset agoraUtc)
    {
        var original = candidata;
        var tentativaId = Guid.NewGuid();
        var novoToken = candidata.FencingToken + 1;
        var tipoDisparo = candidata.TipoDisparoPendente ?? TipoDisparoPendente.Automatico;
        var leaseExpira = agoraUtc.Add(leaseDuration);
        var atualizada = candidata with
        {
            Estado = EstadoIntegracao.EmProcessamento,
            TipoDisparoPendente = null,
            ProximaTentativaEm = null,
            FencingToken = novoToken,
            WorkerProprietario = worker.Valor,
            LeaseExpiraEm = leaseExpira,
            FaseEnvio = FaseEnvioIntegracao.NaoIniciado,
            TentativaAtualId = tentativaId
        };

        integracoes[candidata.Id] = atualizada;
        if (FalharAposMutacaoClaimAntesTentativa)
        {
            integracoes[candidata.Id] = original;
            return null;
        }

        var tentativa = new TentativaIntegracaoMotor(tentativaId, candidata.Id, novoToken, tipoDisparo, FaseEnvioIntegracao.NaoIniciado, DesfechoTentativaIntegracao.EmExecucao, agoraUtc, null, null);
        tentativas.Add(tentativa);
        RegistrarAuditoria(candidata.Id, "claim", agoraUtc, worker.Valor, novoToken, tentativaId, candidata.CorrelationId);
        RegistrarAuditoria(candidata.Id, "tentativa_criada", agoraUtc, worker.Valor, novoToken, tentativaId, candidata.CorrelationId);

        return new ClaimIntegracaoMotor(candidata.Id, tentativaId, candidata.CorrelationId, candidata.Origem, candidata.Destino, candidata.Operacao, candidata.ChaveNegocio, candidata.FingerprintSha256, novoToken, worker.Valor, leaseExpira);
    }
}
