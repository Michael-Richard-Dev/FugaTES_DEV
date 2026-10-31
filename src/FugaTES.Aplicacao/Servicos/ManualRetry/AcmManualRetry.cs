using System.Security.Cryptography;
using System.Text.Json;

namespace FugaTES.Aplicacao.Servicos.ManualRetry;

public enum AcmRetryStage { None, SoapThenRest, RestOnly }
public sealed record AcmRetrySnapshot
{
    public Guid IntegrationId{get;init;}
    public string Contract{get;init;}="";
    public string? Item{get;init;}
    public long Version{get;init;}
    public string Fingerprint{get;init;}="";
    public string CanonicalFingerprint{get;init;}="";
    public string CorrelationId{get;init;}="";
    public string State{get;init;}="";
    public long Fence{get;init;}
    public Guid? AttemptId{get;init;}
    public bool IdentityVerified{get;init;}
    public bool Synthetic{get;init;}
    public bool UnsafeAttempt{get;init;}
    public bool HasPendingCommand{get;init;}
    public string? Worker{get;init;}
    public DateTimeOffset? LeaseUntil{get;init;}
    public string? AttemptOutcome{get;init;}
    public string? JournalStatus{get;init;}
    public string? Stage{get;init;}
    public string? Metadata{get;init;}
    public string? ErrorCode{get;init;}
    public string? ErrorCategory{get;init;}
    public bool Indeterminate{get;init;}
    public bool RetryableFailure{get;init;}
    public string? GuardianRequestedCode{get;init;}
    public string? GuardianConfirmedCode{get;init;}
    public string? Reference{get;init;}
    public bool HasProtocolOrCallback{get;init;}
    public DateTimeOffset JournalUpdatedUtc{get;init;}
    public string Digest()=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
}

// Supplied by the future homologated SERVER authorization/evidence provider, never the request body.
public sealed record AcmRetryApproval
{
    public bool Homologated{get;init;}
    public bool ExecutionAuthorized{get;init;}
    public bool ExclusiveWorker{get;init;}
    public Guid IntegrationId{get;init;}
    public string Contract{get;init;}="";
    public string? Item{get;init;}
    public string Fingerprint{get;init;}="";
    public string ContractMappingHash{get;init;}="";
    public string ObservedSnapshotDigest{get;init;}="";
    public string OperatorSid{get;init;}="";
    public string AuthorizationReference{get;init;}="";
    public string BusinessOriginEvidence{get;init;}="";
    public DateTimeOffset NotBeforeUtc{get;init;}
    public DateTimeOffset UntilUtc{get;init;}
    public AcmRetryStage AllowedStage{get;init;}
    public string? CorrectedFailureDigest{get;init;}
    public string? ExternalCorrectionEvidence{get;init;}
}
public sealed record AcmRetryActor(bool Authenticated,string Sid);
public sealed record AcmRetryDecision(bool Eligible,string Code,string Message,AcmRetryStage Stage=AcmRetryStage.None,string? ResumeMetadata=null);
public sealed record AcmRetryPreview(Guid IntegrationId,string Contract,string? Item,string State,string? PendingStage,string SnapshotDigest,AcmRetryDecision Decision,string OperatorSid);
public sealed record AcmRetryRequest(Guid CommandId,Guid IntegrationId,string SnapshotDigest,string Justification,bool Confirm);
public sealed record AcmRetryAdmission(string State,string Code,Guid CommandId,Guid IntegrationId)
{public bool Accepted=>State=="QUEUED";}
public sealed record AcmRetryWork(Guid CommandId,Guid IntegrationId,long PreviousFence,string Worker);
public interface IAcmRetryApprovals
{Task<AcmRetryApproval?> ResolveAsync(AcmRetryActor actor,Guid integrationId,CancellationToken ct);}
public interface IAcmManualRetryStore
{
    Task<AcmRetrySnapshot?> ReadAsync(Guid id,CancellationToken ct);
    Task<AcmRetryAdmission> AdmitAsync(AcmRetryRequest request,AcmRetryActor actor,AcmRetryApproval approval,DateTimeOffset now,CancellationToken ct);
    Task<AcmRetryWork?> StartAsync(Guid commandId,AcmRetryApproval approval,string worker,DateTimeOffset now,CancellationToken ct);
    Task CompleteAsync(AcmRetryWork work,string result,DateTimeOffset now,CancellationToken ct);
    Task<Guid?> PendingCommandAsync(AcmRetryApproval approval,CancellationToken ct);
}
public interface IAcmManualRetryCommandClient
{
    bool Configured{get;}
    Task<AcmRetryPreview> PreviewAsync(FugaTES.Aplicacao.Consultas.ContratosAcm.ContratoAcmLeitura row,CancellationToken ct);
    Task<AcmRetryAdmission> SubmitAsync(AcmRetryRequest command,CancellationToken ct);
}
public static class AcmManualRetryPolicy
{
    public static bool AuthorizationOpen(AcmRetryApproval? a,Guid id,AcmRetryActor actor,DateTimeOffset now)
        =>actor.Authenticated&&a is{Homologated:true,ExecutionAuthorized:true,ExclusiveWorker:true}&&a.IntegrationId==id&&
            a.OperatorSid==actor.Sid&&actor.Sid.StartsWith("S-1-5-21-",StringComparison.Ordinal)&&Evidence(a.AuthorizationReference)&&
            Hash(a.Fingerprint)&&Hash(a.ObservedSnapshotDigest)&&now>=a.NotBeforeUtc&&now<a.UntilUtc;
    public static AcmRetryDecision Evaluate(AcmRetrySnapshot s,AcmRetryApproval? approval,AcmRetryActor actor,DateTimeOffset now)
    {
        AcmRetryDecision Block(string code,string message)=>new(false,code,message);
        if(s.Synthetic||s.Contract.TrimStart('0')=="986801"||s.IntegrationId==Guid.Parse("e502b0a0-57c5-49f3-976c-9e1fe7e355e3"))
            return Block("SIMULATION","Contrato de simulação não pode acionar o Guardian real.");
        if(s.State=="CONCLUIDA"||s.JournalStatus is "CONCLUIDO" or "AGUARDANDO_CALLBACK"||s.HasProtocolOrCallback)
            return Block("ALREADY_COMPLETED","Contrato/protocolo já concluído ou vinculado. Não reenviar.");
        var unknownMarker=new[]{s.ErrorCode,s.ErrorCategory}.Any(v=>v is not null&&
            (v.Contains("TIMEOUT",StringComparison.OrdinalIgnoreCase)||v.Contains("INDETERMIN",StringComparison.OrdinalIgnoreCase)||
             v.Contains("OUTCOME_UNKNOWN",StringComparison.OrdinalIgnoreCase)||v is "GUARDIAN_REST_HTTP_408" or "GUARDIAN_REST_HTTP_504"));
        if(s.Indeterminate||s.UnsafeAttempt||unknownMarker||s.State=="RESULTADO_INDETERMINADO"||s.Stage is "R165U_SOAP_ARMED" or "R165U_REST_ARMED" or "R165U_RECONCILIATION_REQUIRED")
            return Block("RECONCILIATION_REQUIRED","Resultado externo indeterminado ou histórico inseguro; exige reconciliação, sem reenvio.");
        if(s.HasPendingCommand||s.State is "PENDENTE" or "EM_PROCESSAMENTO" or "AGUARDANDO_REPROCESSAMENTO"||s.Worker is not null||s.LeaseUntil is not null)
            return Block("BUSY","Integração ativa, enfileirada ou com lease. Nenhuma nova tentativa será criada.");
        if(!s.IdentityVerified||s.IntegrationId==Guid.Empty||s.Version<=0||!Evidence(s.Contract)||!Evidence(s.Item)||!Evidence(s.CorrelationId)||
            !Hash(s.Fingerprint)||s.Fingerprint!=s.CanonicalFingerprint||s.Fence<=0||s.AttemptId is null||s.AttemptId==Guid.Empty||s.AttemptOutcome!="FALHA")
            return Block("IDENTITY_OR_HISTORY_UNPROVEN","Identidade H4, item, tentativa ou payload original não comprovado.");
        if(s.State is not("AGUARDANDO_ACAO_MANUAL" or "FALHA_DEFINITIVA"))return Block("NOT_RECOVERABLE","Estado persistido não permite recuperação manual.");
        if(!actor.Authenticated||approval is null||!approval.Homologated||!approval.ExecutionAuthorized||!approval.ExclusiveWorker||
            !Evidence(approval.AuthorizationReference)||approval.OperatorSid!=actor.Sid||!actor.Sid.StartsWith("S-1-5-21-",StringComparison.Ordinal)||
            approval.IntegrationId!=s.IntegrationId||approval.Contract!=s.Contract||approval.Item!=s.Item||approval.Fingerprint!=s.Fingerprint||approval.ObservedSnapshotDigest!=s.Digest())
            return Block("COMMAND_NOT_AUTHORIZED","Serviço de comando homologado e autorização empresarial unitária ainda são necessários.");
        if(now<approval.NotBeforeUtc||now>=approval.UntilUtc||approval.UntilUtc<=approval.NotBeforeUtc)
            return Block("WINDOW_CLOSED","Janela de autorização ausente ou encerrada.");
        if(!Evidence(approval.BusinessOriginEvidence))return Block("ORIGIN_UNPROVEN","Origem empresarial SAP não comprovada; projeção persistida não é recibo autenticado.");
        var corrected=approval.CorrectedFailureDigest==s.Digest()&&Evidence(approval.ExternalCorrectionEvidence);
        if(s.ErrorCategory=="GUARDIAN_REST_HTTP_400"&&!corrected)
            return Block("VALIDATION_400_NOT_CORRECTED","HTTP 400 exige correção comprovada. Dados SAP inválidos devem ser corrigidos na origem; este fluxo não altera o payload.");
        if(!s.RetryableFailure&&!corrected)return Block("CAUSE_NOT_CORRECTED","Falha não classificada como recuperável e sem evidência aprovada de correção da dependência externa.");
        if(s.Stage=="R165U_PRE_SEND_BLOCKED"&&s.JournalStatus is "AGUARDANDO_DEPENDENCIA_EXTERNA" or "FALHA_REPROCESSAVEL")
        {
            if(s.GuardianConfirmedCode is not null)return Block("SOAP_HISTORY_CONFLICT","Há identidade Guardian sem checkpoint SOAP correspondente; reconciliar.");
            if(approval.AllowedStage!=AcmRetryStage.SoapThenRest)return Block("STAGE_SCOPE_MISMATCH","A autorização não abrange a etapa pendente.");
            return new(true,"SAFE_PRE_DISPATCH","Falha conhecida antes do envio; o motor repetirá o preflight de ausência antes de qualquer SOAP.",AcmRetryStage.SoapThenRest);
        }
        if(s.Stage is "R165U_SOAP_CONFIRMED" or "R165U_PROTOCOL_BLOCKED" or "R165Y_REST_REJECTED")
        {
            var parts=s.Metadata?.Split(':');
            if(parts is not{Length:4}||parts[0]!="U"||!Hash(parts[1])||parts[1]!=approval.ContractMappingHash||
                (parts[2].Length>0&&!Hash(parts[2]))||(parts[3].Length>0&&(!long.TryParse(parts[3],System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var seq)||seq<=0))||
                !Evidence(s.GuardianConfirmedCode)||s.GuardianConfirmedCode!=s.GuardianRequestedCode||!Evidence(s.Reference))
                return Block("SOAP_CORRELATION_UNPROVEN","Código real Guardian e checkpoint SOAP não comprovados; não repetir SOAP nem REST.");
            if(s.Stage=="R165Y_REST_REJECTED"&&!corrected)return Block("REST_REJECTION_NOT_CORRECTED","Rejeição REST exige evidência aprovada de correção antes da recuperação.");
            if(approval.AllowedStage!=AcmRetryStage.RestOnly)return Block("STAGE_SCOPE_MISMATCH","A autorização deve ser exclusiva da recuperação REST.");
            return new(true,"REST_ONLY","SOAP confirmado. Recuperação exclusivamente REST, mantendo código, referência e H4 originais.",AcmRetryStage.RestOnly,"U:"+parts[1]+"::"+parts[3]);
        }
        return Block("STAGE_UNPROVEN","Etapa persistida não possui recuperação segura implementada.");
    }
    public static bool Hash(string? value)=>value is {Length:64}&&value.All(c=>char.IsAsciiHexDigit(c)&&!char.IsLower(c));
    public static bool Evidence(string? text)=>!string.IsNullOrWhiteSpace(text)&&text.Length<=200&&!text.Any(char.IsControl);
}

public sealed class AcmManualRetryService(IAcmManualRetryStore store,IAcmRetryApprovals approvals,TimeProvider time)
{
    public async Task<AcmRetryPreview> PreviewAsync(Guid id,AcmRetryActor actor,CancellationToken ct)
    {
        if(!actor.Authenticated)return new(id,"",null,"BLOCKED",null,"",new(false,"AUTHENTICATION_REQUIRED","Autenticação de comando obrigatória."),"");
        var approval=await approvals.ResolveAsync(actor,id,ct);
        if(!AcmManualRetryPolicy.AuthorizationOpen(approval,id,actor,time.GetUtcNow()))
            return new(id,"",null,"BLOCKED",null,"",new(false,"COMMAND_NOT_AUTHORIZED","Autorização unitária de comando ausente ou encerrada."),actor.Sid);
        var snapshot=await store.ReadAsync(id,ct);
        if(snapshot is null)return new(id,"",null,"NOT_FOUND",null,"",new(false,"NOT_FOUND","Integração não localizada."),actor.Sid);
        return new(id,snapshot.Contract,snapshot.Item,snapshot.State,snapshot.Stage,snapshot.Digest(),AcmManualRetryPolicy.Evaluate(snapshot,approval,actor,time.GetUtcNow()),actor.Sid);
    }
    public async Task<AcmRetryAdmission> RequestAsync(AcmRetryRequest request,AcmRetryActor actor,CancellationToken ct)
    {
        if(!actor.Authenticated||request.CommandId==Guid.Empty||!request.Confirm||!AcmManualRetryPolicy.Hash(request.SnapshotDigest)||
            !SafeJustification(request.Justification))return new("BLOCKED","CONFIRMATION_OR_REASON_REQUIRED",request.CommandId,request.IntegrationId);
        var approval=await approvals.ResolveAsync(actor,request.IntegrationId,ct);
        if(!AcmManualRetryPolicy.AuthorizationOpen(approval,request.IntegrationId,actor,time.GetUtcNow()))return new("BLOCKED","COMMAND_NOT_AUTHORIZED",request.CommandId,request.IntegrationId);
        try
        {
            var receipt=await store.AdmitAsync(request,actor,approval!,time.GetUtcNow(),ct);
            return receipt.CommandId==request.CommandId&&receipt.IntegrationId==request.IntegrationId?receipt:
                new("UNKNOWN","ADMISSION_RECEIPT_UNVERIFIED_NO_RETRY",request.CommandId,request.IntegrationId);
        }
        catch{return new("UNKNOWN","ADMISSION_COMMIT_UNVERIFIED_NO_RETRY",request.CommandId,request.IntegrationId);}
    }
    public static bool SafeJustification(string? text)=>text is {Length:>=10 and <=400}&&text.Trim().Length>=10&&!text.Any(char.IsControl)&&
        !System.Text.RegularExpressions.Regex.IsMatch(text,@"(?i)password|senha|authorization|bearer|secret|token\s*[:=]|cnpj\s*[:=]");
}
