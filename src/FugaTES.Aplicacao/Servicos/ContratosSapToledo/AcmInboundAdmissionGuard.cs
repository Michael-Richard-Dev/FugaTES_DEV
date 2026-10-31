using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;

namespace FugaTES.Aplicacao.Servicos.ContratosSapToledo;

public sealed record AcmInboundAdmittedRow(VersaoContratoSapToledo? Version, string CorrelationId,
    DateTimeOffset CreatedAtUtc, string Origem, string Destino, string Operacao, string ChaveNegocio, string Fingerprint);

public static class AcmInboundAdmissionGuard
{
    public static VersaoContratoSapToledo? Resolver(AcmInboundAdmission admission,
        ContratoSapToledoSnapshot snapshot, string fingerprint, string correlationId,
        DateTimeOffset databaseNowUtc, IReadOnlyList<AcmInboundAdmittedRow> admittedRows)
    {
        static bool Marker(AcmInboundAdmission window, string value) =>
            value.StartsWith(window.CorrelationPrefix, StringComparison.Ordinal) &&
            value.Length == window.CorrelationPrefix.Length + 32 &&
            Guid.TryParseExact(value[window.CorrelationPrefix.Length..], "N", out var id) && id != Guid.Empty;
        static AcmInboundScopeConflictException Conflict() => new();
        if (!admission.EstaAberta(databaseNowUtc) || !Marker(admission, correlationId) || admittedRows.Count > 1)
            throw Conflict();
        var canonicalizer = new ContratoSapToledoCanonicalizer();
        var requested = canonicalizer.Canonicalizar(snapshot);
        if (requested.FingerprintSha256 != fingerprint) throw Conflict();
        if (admittedRows.Count == 0) return null;
        var row = admittedRows[0];
        var v = row.Version;
        if (v is null || v.IntegracaoId == Guid.Empty || v.Versao != 1 || !Marker(admission, row.CorrelationId) ||
            row.CreatedAtUtc < admission.NotBeforeUtc || row.CreatedAtUtc >= admission.UntilUtc ||
            row.Origem != "SAP" || row.Destino != "TOLEDO" || row.Operacao != "SAP_CONTRATO_TOLEDO" ||
            row.ChaveNegocio != v.NumeroContrato || row.Fingerprint != v.FingerprintSha256 ||
            v.NumeroContrato != requested.Snapshot.NumeroContrato || v.FingerprintSha256 != fingerprint)
            throw Conflict();
        var previous = canonicalizer.Canonicalizar(v.Snapshot);
        if (previous.Snapshot.NumeroContrato != v.NumeroContrato || previous.FingerprintSha256 != fingerprint ||
            previous.PayloadCanonicoJson != requested.PayloadCanonicoJson)
            throw Conflict();
        return v with { Resultado = ResultadoRegistroVersaoContrato.JaExistente, AdmissionCorrelationId = row.CorrelationId };
    }
}
