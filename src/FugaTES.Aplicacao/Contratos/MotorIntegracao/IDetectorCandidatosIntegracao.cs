using FugaTES.Aplicacao.Modelos.MotorIntegracao;

namespace FugaTES.Aplicacao.Contratos.MotorIntegracao;

// Porta de deteccao de candidatos (S1 aditivo). Retorna 0..N candidatos sinteticos/offline.
// Sem SAP real, sem Toledo real, sem HTTP, sem DB real.
public interface IDetectorCandidatosIntegracao
{
    Task<IReadOnlyList<CandidatoIntegracao>> DetectarAsync(CancellationToken cancellationToken);
}
