namespace FugaTES.Aplicacao.Contratos.ContratosSapToledo;

// As identidades pertencem a dominios distintos. NULL significa identidade nao comprovada.
public sealed record AcmGuardianVinculo(
    string NumeroContratoSap, long VersaoH4, Guid IntegracaoIdOrigem,
    string NumeroReferenciaSolicitado, string CodigoContratoSolicitado,
    string? CodigoContratoConfirmado, long? ProtocoloIdGuardian,
    string? NumeroTicketGuardian, long? CallbackIdGuardian,
    long? CallbackSequencialGuardian, string? CallbackFingerprint);

public enum StatusEventoAcmGuardian { Novo, Idempotente }

public interface IAcmGuardianVinculoStore
{
    Task<AcmGuardianVinculo> ReservarAsync(
        VersaoContratoSapToledo versao, string numeroReferencia, string codigoContratoSolicitado,
        CancellationToken cancellationToken);
    Task<AcmGuardianVinculo> ReservarSolicitacaoUnicaAsync(VersaoContratoSapToledo versao,string referencia,string codigo,CancellationToken ct)
        => throw new InvalidOperationException("ACM_UNIQUE_REQUEST_RESERVATION_UNAVAILABLE");
    Task<AcmGuardianVinculo?> ObterPorIntegracaoAsync(Guid integracaoId, CancellationToken cancellationToken);
    // Somente chamar com codigo EXTRAIDO e conferido de resposta Guardian real.
    Task<AcmGuardianVinculo> RegistrarCodigoConfirmadoAsync(
        Guid integracaoId, string codigoContratoConfirmado, CancellationToken cancellationToken);
    // Id do pre-cadastro nao e o callback.ID; estes campos sao armazenados separadamente.
    Task<AcmGuardianVinculo> RegistrarProtocoloConfirmadoAsync(
        Guid integracaoId, long protocoloId, string? numeroTicket, CancellationToken cancellationToken);
    // Somente chamar apos prova documental/wire da associacao exata do callback a versao H4.
    Task<AcmGuardianVinculo> VincularCallbackComprovadoAsync(
        Guid integracaoId, long callbackId, long callbackSequencial, CancellationToken cancellationToken);
    Task<AcmGuardianVinculo?> ResolverCallbackAsync(
        long callbackId, long callbackSequencial, CancellationToken cancellationToken);
    // Apenas ledger de deduplicacao; este metodo NUNCA dispara SAP.
    Task<StatusEventoAcmGuardian> RegistrarFingerprintCallbackAsync(
        long callbackId, long callbackSequencial, string fingerprintSha256, CancellationToken cancellationToken);
}
