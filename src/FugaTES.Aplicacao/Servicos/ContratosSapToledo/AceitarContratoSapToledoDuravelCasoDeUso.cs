using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;

namespace FugaTES.Aplicacao.Servicos.ContratosSapToledo;

public sealed record ResultadoAceiteContratoSapToledo(
    string NumeroContrato, long Versao, bool Reentrega, string CorrelationId);

public sealed class AceitarContratoSapToledoDuravelCasoDeUso(
    ContratoSapToledoCanonicalizer canonicalizer,
    IContratoSapToledoVersaoStore store)
{
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
