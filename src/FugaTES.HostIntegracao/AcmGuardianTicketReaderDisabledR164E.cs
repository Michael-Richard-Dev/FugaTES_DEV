using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Servicos.Recebimento;

namespace FugaTES.HostIntegracao;

internal sealed class AcmGuardianTicketReaderDisabledR164E : IAcmGuardianVerifiedWeighingReaderR164A
{
    public Task<AcmGuardianVerifiedWeighingR164A?> ObterAsync(CorrelacaoRecebimentoMvp correlation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<AcmGuardianVerifiedWeighingR164A?>(null);
    }
}
