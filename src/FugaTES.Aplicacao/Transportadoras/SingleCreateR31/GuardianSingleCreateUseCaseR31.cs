using System.Security.Cryptography;
using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras.SnapshotsR24;
namespace FugaTES.Aplicacao.Transportadoras.SingleCreateR31;

// Manual only; no scheduler, profile loading, credential lookup or external composition.
public sealed class GuardianSingleCreateUseCaseR31(IGuardianCarrierManualReadOnly reader,IGuardianCarrierGateway writer,
    ISingleCreateJournalR31? journal=null,Func<bool>? writeGate=null,Func<CarrierPayload,bool>? completePayloadValidator=null,
    Func<CarrierPayload,CarrierManualGuardian,bool>? readbackMatcher=null,TimeProvider? clock=null,
    Func<CarrierPayload,byte[]>? wirePayload=null)
{
    // Retained constructor argument for existing callers; post-GET is no longer a confirmation prerequisite.
    bool Gate(){_ = readbackMatcher;try{return writeGate?.Invoke()==true;}catch{return false;}}
    bool SpecificallyAuthorized(SingleCreateRequestR31 r)=>r.Approval is {SpecificCreateAuthorized:true,Environment:"HML"} a&&
        !string.IsNullOrWhiteSpace(a.EvidenceReference)&&a.UntilUtc>(clock??TimeProvider.System).GetUtcNow()&&
        a.BusinessPartner==r.BusinessPartner&&a.GuardianCode==r.Identity?.Codigo&&a.GetSignatureId==r.Identity?.IdAssinatura;
    public async Task<SingleCreateResultR31> ExecuteAsync(SingleCreateRequestR31 r,CancellationToken ct=default)
    {
        SingleCreateResultR31 Block(string code)=>new(r.AttemptId,SingleCreateStateR31.BLOCKED,code);
        SingleCreateResultR31 Unknown(string code)=>new(r.AttemptId,SingleCreateStateR31.INDETERMINATE,code);
        if(!Gate())return Block("SINGLE_CREATE_WRITE_GATE_OFF");
        if(ct.IsCancellationRequested)return Block("APP_CLOSED_OR_CANCELLED");
        if(r.AttemptId==Guid.Empty)return Block("SINGLE_CREATE_ATTEMPT_INVALID");
        if(r.Identity is not {Comprovada:true} identity||string.IsNullOrWhiteSpace(identity.Codigo)||
            r.Approval is not {CodeApproved:true} approval||approval.GuardianCode!=identity.Codigo||string.IsNullOrWhiteSpace(identity.Evidencia))
            return Block("GUARDIAN_CODE_NOT_APPROVED");
        if(string.IsNullOrWhiteSpace(identity.IdAssinatura)||!approval.HmlSignatureBound||approval.GetSignatureId!=identity.IdAssinatura)
            return Block("GUARDIAN_SIGNATURE_NOT_BOUND");
        if(!approval.BpConfirmed||approval.BusinessPartner!=r.BusinessPartner)return Block("SAP_BP_NOT_CONFIRMED");
        try{CarrierPersistencePolicy.ValidateBp(r.BusinessPartner);}catch{return Block("SAP_BP_IDENTITY_INVALID");}
        var eligibility=new Crm010BusinessDatePolicyR25T().Evaluate(r.ValidFrom,r.ValidTo,r.ReferenceAt).Status;
        if(eligibility==AvaliacaoCrm010R24.INDETERMINATE)return Block("CRM010_INDETERMINATE");
        if(eligibility!=AvaliacaoCrm010R24.EVALUATED_VALID)return Block("CRM010_NOT_VALID");
        if(!approval.PayloadContractComplete||r.Payload is not {} payload||payload.BusinessPartner!=r.BusinessPartner||
            string.IsNullOrWhiteSpace(payload.Name)||string.IsNullOrWhiteSpace(payload.LegalName)||
            completePayloadValidator is null)return Block("CREATE_PAYLOAD_CONTRACT_PARTIAL");
        try{if(!completePayloadValidator(payload))return Block("CREATE_PAYLOAD_CONTRACT_PARTIAL");}
        catch{return Block("CREATE_PAYLOAD_CONTRACT_PARTIAL");}
        if(!approval.ScopedAbsenceApproved)return Block("GUARDIAN_ABSENCE_CONTRACT_NOT_APPROVED");
        if(!approval.PostgreSqlBootstrapAttested||journal is null)return Block("POSTGRESQL_RUNTIME_NOT_ATTESTED");
        if(!SpecificallyAuthorized(r))return Block("SINGLE_CREATE_NOT_AUTHORIZED");
        CarrierManualGuardian existence;
        try{existence=await reader.ConsultarCodigoAsync(identity,ct).ConfigureAwait(false);}
        catch{return Block("GUARDIAN_EXISTENCE_NOT_PROVEN");}
        if(existence.State!=CarrierLookupState.Absent||!existence.Ausencia404Comprovada)
            return Block("GUARDIAN_CREATE_ABSENCE_NOT_PROVEN");
        if(!Gate()||!SpecificallyAuthorized(r)||ct.IsCancellationRequested)return Block("SINGLE_CREATE_NOT_AUTHORIZED");
        // Journal receives only identifiers and fingerprint, never names/signature/raw payload.
        string digest;
        try
        {
            var encoded=wirePayload is null?JsonSerializer.SerializeToUtf8Bytes(payload):wirePayload(payload);
            if(encoded is null||encoded.Length==0)return Block("CREATE_PAYLOAD_ENCODING_INVALID");
            digest=Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant();
        }
        catch{return Block("CREATE_PAYLOAD_ENCODING_INVALID");}
        try{if(!await journal.TryPrepareAsync(r.AttemptId,r.BusinessPartner,identity.Codigo,digest,ct).ConfigureAwait(false))
            return Block("PENDING_OR_REPLAY_RECONCILIATION_REQUIRED");}
        catch{return Unknown("CREATE_PREPARE_OUTCOME_UNKNOWN");}
        if(!Gate()||!SpecificallyAuthorized(r)||ct.IsCancellationRequested)return Unknown("CREATE_NOT_SENT_PENDING_RECONCILIATION");
        CarrierWriteResult write;
        try{write=await writer.GravarAsync(new(r.AttemptId,r.BusinessPartner,identity.Codigo,"CREATE",payload),ct).ConfigureAwait(false);}
        catch{return Unknown("CREATE_DISPATCH_OUTCOME_UNKNOWN");}
        if(!write.Accepted||write.Indeterminate||write.HttpStatusCode!=200||write.GuardianCode!=identity.Codigo)
            return Unknown("CREATE_WRITE_UNCONFIRMED");
        // R38A/R41: HTTP 200 and the exact response code confirm CREATE; post-GET is optional.
        // Durable confirmation still must succeed; an uncertain commit never permits a blind retry.
        try
        {
            await journal.ConfirmAsync(r.AttemptId,ct).ConfigureAwait(false);
            return new(r.AttemptId,SingleCreateStateR31.CONFIRMED,"CREATE_HTTP200_CODE_CONFIRMED");
        }
        catch{return Unknown("CREATE_CONFIRM_OUTCOME_UNKNOWN");}
    }
}
