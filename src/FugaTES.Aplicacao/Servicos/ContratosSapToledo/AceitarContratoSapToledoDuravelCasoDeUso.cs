using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;

namespace FugaTES.Aplicacao.Servicos.ContratosSapToledo;

public sealed record ResultadoAceiteContratoSapToledo(
    string NumeroContrato, long Versao, bool Reentrega, string CorrelationId);

public sealed class AceitarContratoSapToledoDuravelCasoDeUso(
    ContratoSapToledoCanonicalizer canonicalizer,
    IContratoSapToledoVersaoStore store)
{
    public async Task<ResultadoAceiteContratoSapToledo> ExecutarAdmitidoAsync(
        ContratoSapToledoSnapshot snapshot, string correlationId,
        AcmInboundAdmission admission, CancellationToken cancellationToken)
    {
        if (store is not IContratoSapToledoAdmissionStore admissionStore ||
            !admission.EhValida() || string.IsNullOrWhiteSpace(correlationId) ||
            !correlationId.StartsWith(admission.CorrelationPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("R165AG_ATOMIC_ADMISSION_STORE_REQUIRED");
        var canonical = canonicalizer.Canonicalizar(snapshot);
        var version = await admissionStore.RegistrarPrimeiroOuObterReentregaAsync(canonical.Snapshot,
            canonical.FingerprintSha256, canonical.PayloadCanonicoJson, correlationId, admission, cancellationToken);
        if (version.IntegracaoId == Guid.Empty || version.Versao != 1 ||
            version.NumeroContrato != canonical.Snapshot.NumeroContrato ||
            version.FingerprintSha256 != canonical.FingerprintSha256 ||
            version.AdmissionCorrelationId is not { } durableCorrelation ||
            !durableCorrelation.StartsWith(admission.CorrelationPrefix, StringComparison.Ordinal) ||
            durableCorrelation.Length != admission.CorrelationPrefix.Length + 32 ||
            !Guid.TryParseExact(durableCorrelation[admission.CorrelationPrefix.Length..], "N", out var durableId) || durableId == Guid.Empty)
            throw new InvalidOperationException("R165AG_DURABLE_ADMISSION_IDENTITY_UNPROVEN");
        return new(version.NumeroContrato, version.Versao,
            version.Resultado == ResultadoRegistroVersaoContrato.JaExistente, durableCorrelation);
    }

    public async Task<ResultadoAceiteContratoSapToledo> ExecutarAsync(
        ContratoSapToledoSnapshot snapshot, string correlationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Correlation ID is required.", nameof(correlationId));

        var canonical = canonicalizer.Canonicalizar(snapshot);
        var version = await store.RegistrarOuObterAsync(canonical.Snapshot,
            canonical.FingerprintSha256, canonical.PayloadCanonicoJson, correlationId, cancellationToken);
        if (version.IntegracaoId == Guid.Empty || version.Versao <= 0 ||
            version.NumeroContrato != canonical.Snapshot.NumeroContrato ||
            version.FingerprintSha256 != canonical.FingerprintSha256)
            throw new InvalidOperationException("Durable contract registration returned an inconsistent version.");

        return new(version.NumeroContrato, version.Versao,
            version.Resultado == ResultadoRegistroVersaoContrato.JaExistente, correlationId);
    }
}
