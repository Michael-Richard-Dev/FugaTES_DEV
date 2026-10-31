using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Mvp;

namespace FugaTES.Aplicacao.Servicos.Mvp;

public sealed class ResolverTransportadoraAcmPendente : IResolverTransportadoraAcm
{
    public Task<TransportadoraAcmComprovada?> ResolverAsync(VersaoContratoSapToledo versao, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(versao);
        ct.ThrowIfCancellationRequested();
        // Functional source remains pending. No fallback partner lookup or inferred code.
        return Task.FromResult<TransportadoraAcmComprovada?>(null);
    }
}