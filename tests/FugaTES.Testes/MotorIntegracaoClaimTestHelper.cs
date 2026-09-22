using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;

namespace FugaTES.Testes;

// Helper EXCLUSIVO de teste (fora do port da Aplicacao). Reproduz o contrato oficial do pipeline
// SELECIONAR PRE-CLAIM -> ClaimarEspecificamenteAsync, substituindo o antigo claim generico inseguro.
internal static class MotorIntegracaoClaimTestHelper
{
    public static async Task<ClaimIntegracaoMotor?> ClaimarPrimeiroAsync(
        this IMotorIntegracaoRepository repository,
        IdentidadeWorkerIntegracao worker,
        TimeSpan leaseDuration,
        DateTimeOffset agoraUtc,
        CancellationToken cancellationToken)
    {
        var elegiveis = await repository.SelecionarCandidatosPreClaimAsync(agoraUtc, cancellationToken);
        if (elegiveis.Count == 0)
        {
            return null;
        }

        var alvo = elegiveis[0];
        return await repository.ClaimarEspecificamenteAsync(
            alvo.Id,
            alvo.Estado,
            alvo.TipoDisparoPendente,
            alvo.FingerprintSha256,
            worker,
            leaseDuration,
            agoraUtc,
            cancellationToken);
    }
}
