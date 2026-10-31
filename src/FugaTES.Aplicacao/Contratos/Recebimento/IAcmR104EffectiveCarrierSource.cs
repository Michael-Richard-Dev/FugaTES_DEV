using FugaTES.Aplicacao.Modelos.Recebimento;
namespace FugaTES.Aplicacao.Contratos.Recebimento;

// The minimal callback does not contain the carrier selected later in Guardian.
// No operational implementation is registered until a ticket-scoped source is homologated.
public sealed record AcmR104EffectiveCarrier(string Protocolo,string Ticket,string CallbackFingerprint,
    string SapBusinessPartner,string EvidenceReference,bool IdentityConfirmedForTicket);
public interface IAcmR104EffectiveCarrierSource
{
    Task<AcmR104EffectiveCarrier?> ObterAsync(CorrelacaoRecebimentoMvp correlation,CancellationToken ct);
}
