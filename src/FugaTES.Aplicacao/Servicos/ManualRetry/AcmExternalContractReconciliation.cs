using System.Security.Cryptography;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Servicos.Mvp;
namespace FugaTES.Aplicacao.Servicos.ManualRetry;
// Server-owned maintenance proof, never accepted by a public/read-only endpoint.
public sealed record AcmExternalContractReconciliationProof
{
 public Guid OperationId{get;init;}public Guid IntegrationId{get;init;}
 public string SapCode{get;init;}="";public string ExternalCode{get;init;}="";public long Version{get;init;}
 public string Fingerprint{get;init;}="";public string SnapshotDigest{get;init;}="";
 public EstadoConsultaContratoGuardian Lookup{get;init;}public long Sequence{get;init;}public DateTimeOffset ObservedUtc{get;init;}
 public int SoapHttp{get;init;}public string SoapResponseHash{get;init;}="";
 public AcmR57NormalContract? Desired{get;init;}public ContratoGuardianSet26? Observed{get;init;}
 public string OperatorSid{get;init;}="";public string AuthorizationReference{get;init;}="";public string IdentifierMappingEvidence{get;init;}="";public string BackupReference{get;init;}="";
 public string MappingHash()=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Desired)));
 public string Metadata()=>"U:"+MappingHash()+"::"+Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
public sealed record AcmExternalContractReconciliationResult(string Result,Guid IntegrationId,string SapCode,string GuardianCode,long Sequence,Guid AuditId,int PreservedAttemptCount);
public static class AcmExternalContractReconciliationPolicy
{
 public static bool CanReconcile(AcmRetrySnapshot s,AcmExternalContractReconciliationProof p,AcmR57NormalContract sourcePrepared,DateTimeOffset now)
 {
  var age=now-p.ObservedUtc;
  return AcmManualRetryPolicy.IsKnownPreSendFailure(s)&&!s.HasPendingCommand&&p.OperationId!=Guid.Empty&&p.IntegrationId==s.IntegrationId&&p.SapCode==s.Contract&&p.Version==s.Version&&
   p.Fingerprint==s.Fingerprint&&p.SnapshotDigest==s.Digest()&&p.Lookup==EstadoConsultaContratoGuardian.Existente&&p.Sequence>0&&p.SoapHttp==200&&AcmManualRetryPolicy.Hash(p.SoapResponseHash)&&age>=TimeSpan.Zero&&age<=TimeSpan.FromMinutes(2)&&
   p.SapCode.Length is >0 and <=20&&p.ExternalCode.Length is >0 and <=20&&p.SapCode.All(char.IsAsciiDigit)&&p.ExternalCode.All(char.IsAsciiDigit)&&p.SapCode.TrimStart('0').Length>0&&p.SapCode.TrimStart('0')==p.ExternalCode.TrimStart('0')&&
   p.Desired is not null&&p.Observed is not null&&p.Desired.Contrato.Codigo==p.ExternalCode&&p.Observed.Codigo==p.ExternalCode&&
   sourcePrepared.Contrato.Codigo==s.Contract&&sourcePrepared.NumeroReferencia==p.Desired.NumeroReferencia&&
   SincronizarContratoGuardian.Identicos(p.Desired.Contrato,sourcePrepared.Contrato with{Codigo=p.ExternalCode})&&
   AcmManualRetryPolicy.Evidence(p.Desired.NumeroReferencia)&&SincronizarContratoGuardian.Identicos(p.Observed,p.Desired.Contrato)&&
   System.Text.RegularExpressions.Regex.IsMatch(p.OperatorSid,@"\AS-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+\z")&&new[]{p.AuthorizationReference,p.IdentifierMappingEvidence,p.BackupReference}.All(AcmManualRetryPolicy.Evidence);
 }
}
