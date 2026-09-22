using FugaTES.Aplicacao.Modelos.MotorIntegracao;

namespace FugaTES.Aplicacao.Contratos.MotorIntegracao;

public interface IMotorIntegracaoRepository
{
    Task<bool> EstaDisponivelAsync(CancellationToken cancellationToken);

    Task<bool> ExistePausaAplicavelAsync(string origem, string destino, string operacao, CancellationToken cancellationToken);

    Task<ResultadoRegistroIntegracao> RegistrarCandidatoAsync(CandidatoIntegracao candidato, DateTimeOffset agoraUtc, CancellationToken cancellationToken);

    Task<IntegracaoMotor?> ObterPorIdAsync(Guid integracaoId, CancellationToken cancellationToken);

    Task<IReadOnlyList<IntegracaoMotor>> SelecionarCandidatosPreClaimAsync(DateTimeOffset agoraUtc, CancellationToken cancellationToken);

    Task<ClaimIntegracaoMotor?> ClaimarEspecificamenteAsync(Guid integracaoId, EstadoIntegracao estadoEsperado, TipoDisparoPendente? tipoDisparoEsperado, string fingerprintEsperado, IdentidadeWorkerIntegracao worker, TimeSpan leaseDuration, DateTimeOffset agoraUtc, CancellationToken cancellationToken);

    Task<bool> RegistrarValidacaoReprovadaAsync(Guid integracaoId, string correlationId, string diagnosticoSanitizado, DateTimeOffset agoraUtc, CancellationToken cancellationToken);

    Task<bool> MarcarEnvioIniciadoAsync(Guid integracaoId, Guid tentativaId, long fencingToken, string worker, DateTimeOffset agoraUtc, CancellationToken cancellationToken);

    Task<bool> RenovarLeaseAsync(Guid integracaoId, Guid tentativaId, long fencingToken, string worker, TimeSpan leaseDuration, DateTimeOffset agoraUtc, CancellationToken cancellationToken);

    Task<bool> PersistirResultadoAsync(Guid integracaoId, Guid tentativaId, long fencingToken, string worker, ResultadoProcessamentoExterno resultado, DateTimeOffset agoraUtc, TimeSpan retryBackoff, CancellationToken cancellationToken);

    Task<bool> RegistrarResultadoIndeterminadoAsync(Guid integracaoId, Guid tentativaId, long fencingToken, string worker, DateTimeOffset agoraUtc, CancellationToken cancellationToken);

    Task<bool> ReprocessarAsync(Guid integracaoId, DateTimeOffset agoraUtc, string correlationId, CancellationToken cancellationToken);

    Task<bool> IntegrarAgoraAsync(Guid integracaoId, DateTimeOffset agoraUtc, string correlationId, CancellationToken cancellationToken);

    Task<bool> CriarPausaAsync(PausaProcessamentoMotor pausa, string correlationId, CancellationToken cancellationToken);

    Task<bool> RetomarPausaAsync(Guid pausaId, DateTimeOffset agoraUtc, string correlationId, CancellationToken cancellationToken);

    Task<int> RecuperarLeasesExpiradasAsync(DateTimeOffset agoraUtc, CancellationToken cancellationToken);
}
