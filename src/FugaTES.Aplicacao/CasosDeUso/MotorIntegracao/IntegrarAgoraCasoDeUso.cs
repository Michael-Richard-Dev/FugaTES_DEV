using FugaTES.Aplicacao.Contratos.MotorIntegracao;

namespace FugaTES.Aplicacao.CasosDeUso.MotorIntegracao;

public sealed class IntegrarAgoraCasoDeUso
{
    private readonly IMotorIntegracaoRepository repository;
    private readonly TimeProvider timeProvider;

    public IntegrarAgoraCasoDeUso(IMotorIntegracaoRepository repository, TimeProvider timeProvider)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<bool> ExecutarAsync(Guid integracaoId, string correlationId, CancellationToken cancellationToken)
    {
        return repository.IntegrarAgoraAsync(integracaoId, timeProvider.GetUtcNow(), correlationId, cancellationToken);
    }
}
