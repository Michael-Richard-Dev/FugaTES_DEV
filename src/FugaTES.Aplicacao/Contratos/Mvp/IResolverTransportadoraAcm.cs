using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Mvp;

namespace FugaTES.Aplicacao.Contratos.Mvp;

public interface IResolverTransportadoraAcm
{
    // Return only an identity proven by a transactional source for this immutable version.
    // Null means unresolved; the issuing partner never supplies an implicit carrier identity.
    Task<TransportadoraAcmComprovada?> ResolverAsync(VersaoContratoSapToledo versao, CancellationToken ct);
}