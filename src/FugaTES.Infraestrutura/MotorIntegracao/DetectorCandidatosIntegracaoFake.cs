using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;

namespace FugaTES.Infraestrutura.MotorIntegracao;

// Detector terminal de teste (S1 aditivo). Fila sintetica controlada, sem qualquer efeito externo.
// Nao calcula fingerprint: apenas devolve os candidatos previamente enfileirados, preservando a ordem FIFO.
public sealed class DetectorCandidatosIntegracaoFake : IDetectorCandidatosIntegracao
{
    private readonly object gate = new();
    private readonly Queue<CandidatoIntegracao> fila = new();

    public void Enfileirar(CandidatoIntegracao candidato)
    {
        ArgumentNullException.ThrowIfNull(candidato);
        lock (gate)
        {
            fila.Enqueue(candidato);
        }
    }

    public void EnfileirarVarios(params CandidatoIntegracao[] candidatos)
    {
        ArgumentNullException.ThrowIfNull(candidatos);
        lock (gate)
        {
            foreach (var candidato in candidatos)
            {
                ArgumentNullException.ThrowIfNull(candidato);
                fila.Enqueue(candidato);
            }
        }
    }

    public Task<IReadOnlyList<CandidatoIntegracao>> DetectarAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            IReadOnlyList<CandidatoIntegracao> lote = fila.ToArray();
            fila.Clear();
            return Task.FromResult(lote);
        }
    }
}
