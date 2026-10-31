using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
namespace FugaTES.Aplicacao.Contratos.ContratosSapToledo;
public enum ResultadoRegistroVersaoContrato { Criada, JaExistente }
public sealed record VersaoContratoSapToledo(ResultadoRegistroVersaoContrato Resultado, string NumeroContrato, long Versao, string FingerprintSha256, Guid IntegracaoId, ContratoSapToledoSnapshot Snapshot)
{
    // Existing H4 timestamp; not part of the SAP snapshot/fingerprint.
    public DateTimeOffset? RecebidoEmUtc { get; init; }
    // Present only for the typed new-contract admission result; never changes the SAP fingerprint.
    public string? AdmissionCorrelationId { get; init; }
}
public interface IContratoSapToledoVersaoStore
{
    Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot snapshot, string fingerprintSha256, string payloadCanonicoJson, string correlationId, CancellationToken cancellationToken);
    Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid integracaoId, CancellationToken cancellationToken);
    Task<bool> TemPredecessorNaoTerminalAsync(string numeroContrato, long versao, CancellationToken cancellationToken);
}
