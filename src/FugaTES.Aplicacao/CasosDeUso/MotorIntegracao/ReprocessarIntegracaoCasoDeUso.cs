using FugaTES.Aplicacao.Contratos.MotorIntegracao;

namespace FugaTES.Aplicacao.CasosDeUso.MotorIntegracao;

public sealed class ReprocessarIntegracaoCasoDeUso
{
    private readonly IMotorIntegracaoRepository repository;
    private readonly TimeProvider timeProvider;

    public ReprocessarIntegracaoCasoDeUso(IMotorIntegracaoRepository repository, TimeProvider timeProvider)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<bool> ExecutarAsync(Guid integracaoId, string correlationId, CancellationToken cancellationToken)
    {
        return repository.ReprocessarAsync(integracaoId, timeProvider.GetUtcNow(), correlationId, cancellationToken);
    }
}
