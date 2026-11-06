using System.Text.Json;
using System.Text.RegularExpressions;
namespace FugaTES.Aplicacao.Modelos.Mm;
public sealed record MmMasterdataCheck(bool Confirmed,string Code,string Emitter,string Material,DateTimeOffset CheckedUtc);
public interface IMmMasterdataGate { Task<MmMasterdataCheck> CheckAsync(byte[] payload,CancellationToken ct); }
public sealed record MmRetryLegacyEvidence(Guid ReservaId,string SourceSha256,string PayloadSha256,string ProofSha256)
{public DateTimeOffset? RejectedUtc{get;init;}}
public sealed record MmRetryOptions
{
 public bool Enabled{get;init;}
 public string OperatorSid{get;init;}="";
 public string AuthorizationReference{get;init;}="";
 public int GuardianSignatureId{get;init;}
 public MmRetryLegacyEvidence[] LegacyEvidence{get;init;}=[];
 public void Validate()
 {
  if(!Enabled)return;
  if(!Regex.IsMatch(OperatorSid,@"\AS-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+\z")||AuthorizationReference.Length is <8 or >160||AuthorizationReference.Any(char.IsControl)||GuardianSignatureId<=0||LegacyEvidence is null||LegacyEvidence.Length>1000||LegacyEvidence.Select(x=>x.ReservaId).Distinct().Count()!=LegacyEvidence.Length||LegacyEvidence.Any(x=>x.ReservaId==Guid.Empty||!Hash(x.SourceSha256)||!Hash(x.PayloadSha256)||!Hash(x.ProofSha256)||x.RejectedUtc is null||x.RejectedUtc==default(DateTimeOffset)||x.RejectedUtc.Value.Offset!=TimeSpan.Zero))throw new InvalidOperationException("MM_RETRY_POLICY_INVALID");
 }
 public static bool Hash(string? value)=>value is{Length:64}&&value.All(char.IsAsciiHexDigit)&&value==value.ToUpperInvariant();
}
public sealed record MmRetryFacts(PedidoCompraMmLeitura Source,RegistroProtocoloMm Previous,bool Fresh,bool PendingCommand);
public sealed record MmRetryDecision(bool Allowed,string Code,string Message);
public sealed record MmRetryPreview(string Pedido,string Item,Guid ReservaId,string SnapshotDigest,string OperatorSid,MmRetryDecision Decision);
public sealed record MmRetryRequest(Guid CommandId,string Pedido,string Item,string SnapshotDigest,string Justification);
public sealed record MmRetryReceipt(Guid CommandId,string Pedido,string Item,string State,string Code)
{public bool Accepted=>State=="QUEUED";}
public sealed record MmRetryCommand
{
 public Guid Id{get;init;}
 public RegistroProtocoloMm Previous{get;init;}=null!;
 public string SnapshotDigest{get;init;}="";
 public string OperatorSid{get;init;}="";
 public string Justification{get;init;}="";
 public string State{get;init;}="PENDENTE";
 public DateTimeOffset CreatedUtc{get;init;}
 public Guid? Fence{get;init;}
 public DateTimeOffset? DispatchStartedUtc{get;init;}
 public string? OutcomeCode{get;init;}
 public MmRetryReceipt Receipt()=>new(Id,Previous.Pedido,Previous.Item,State is "PENDENTE" or "EM_PROCESSAMENTO"?"QUEUED":State=="CONCLUIDO"?"COMPLETED":State=="INDETERMINADO"?"UNKNOWN":"BLOCKED",MmRetryReason.IsPublic(OutcomeCode)?OutcomeCode!:"MM_COMMAND_"+State);
}
public interface IMmRetryCommandClient
{
 bool Configured{get;}
 Task<MmRetryPreview> PreviewMmAsync(string pedido,string item,CancellationToken ct);
 Task<MmRetryReceipt> SubmitMmAsync(MmRetryRequest command,CancellationToken ct);
 Task<MmRetryReceipt?> StatusMmAsync(Guid commandId,CancellationToken ct);
}

public interface IMmAdmittedGuardianGateway:IEnviarProtocoloMmGateway
{Task<ResultadoProtocoloMm> EnviarAdmitidoAsync(byte[] payload,Func<CancellationToken,Task<bool>> admission,CancellationToken ct);}
