using FugaTES.Aplicacao.Transportadoras.SnapshotsR24;
namespace FugaTES.Aplicacao.Transportadoras.SingleCreateR31;
public enum SingleCreateStateR31 { BLOCKED, CONFIRMED, INDETERMINATE }
public sealed record SingleCreateResultR31(Guid AttemptId,SingleCreateStateR31 State,string Code);
public sealed record SingleCreateApprovalR31(string BusinessPartner,string GuardianCode,string GetSignatureId,
    string EvidenceReference,DateTimeOffset UntilUtc,bool BpConfirmed=false,bool CodeApproved=false,
    bool HmlSignatureBound=false,bool PayloadContractComplete=false,bool ScopedAbsenceApproved=false,
    bool PostgreSqlBootstrapAttested=false,bool SpecificCreateAuthorized=false,string Environment="HML");
public sealed record SingleCreateRequestR31(Guid AttemptId,string BusinessPartner,CarrierManualIdentity? Identity,
    CarrierPayload? Payload,DateTimeOffset? ValidFrom,DateTimeOffset? ValidTo,DateTimeOffset ReferenceAt,
    SingleCreateApprovalR31? Approval);
// Journal must atomically reject pending/replayed writes for this BP/code. No real adapter in this gate.
public interface ISingleCreateJournalR31
{
    Task<bool> TryPrepareAsync(Guid attemptId,string bp,string code,string payloadHash,CancellationToken ct);
    Task ConfirmAsync(Guid attemptId,CancellationToken ct);
}
