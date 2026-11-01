using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Configuracao;
namespace FugaTES.Aplicacao.Servicos.Mvp;

public interface IAcmR57DynamicMaterialPreparation
{Task ValidarMaterialAsync(VersaoContratoSapToledo version,CancellationToken ct);}

// A material mapping candidate is accepted only after an exact scoped Guardian GET.
// Proof belongs to one DI scope/integration/source fingerprint and is never persisted as a new whitelist entry.
public sealed class AcmGuardianMaterialResolver(AcmR57NormalOptions options,IGuardianMaterialRead lookup,TimeProvider clock)
{
 private Proof? proof;
 sealed record Proof(Guid IntegrationId,string Fingerprint,string SapMaterial,string? Item,string Centro,string Codigo,long Sequencial,int Signature,DateTimeOffset At);
 public static bool UsesDynamic(AcmR57NormalOptions o,VersaoContratoSapToledo v)
 {
  if(!o.DynamicMaterialValidationEnabled)return false;
  if(!o.DynamicEmitterValidationEnabled||o.DynamicEmitterSignatureId<=0)throw new AcmR57BlockedException("ACM_MATERIAL_SCOPE_REQUIRED");
  if(o.ManualRecoveryScope is {} manual)
  {if(o.AcmInboundWindow.Enabled||o.AcmAutomatico.Enabled||!manual.Matches(v))throw new AcmR57BlockedException("ACM_MATERIAL_SCOPE_REQUIRED");return true;}
  if(o.AcmInboundWindow.Enabled)return false;
  if(o.DynamicMaterialValidationFromUtc is null||o.DynamicMaterialValidationFromUtc.Value.Offset!=TimeSpan.Zero||v.RecebidoEmUtc is null)
   throw new AcmR57BlockedException("ACM_MATERIAL_ADMISSION_IDENTITY_REQUIRED");
  return v.RecebidoEmUtc>=o.DynamicMaterialValidationFromUtc&&AcmGuardianEmitterResolver.UsesDynamic(o,v);
 }
 public static string Candidate(AcmR57NormalOptions o,VersaoContratoSapToledo v)
 {
  var s=v.Snapshot;
  if((v.Versao!=1&&o.ManualRecoveryScope?.Matches(v)!=true)||s.Centro!="3011"||s.StatusContrato!="T"||!AcmInboundWindowOptions.EscopoContratoAprovado(s.TipoContrato,s.TipoPesagem))throw new AcmR57BlockedException("ACM_MATERIAL_SCOPE_REQUIRED");
  var raw=s.Material;
  if(string.IsNullOrEmpty(raw)||raw.Length>40||!char.IsAsciiLetterOrDigit(raw[0])||!raw.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.')||raw.All(c=>c=='0'))throw new AcmR57BlockedException("ACM_MATERIAL_CODE_INVALID");
  var mappings=o.ApprovedMaterialMappings.Where(m=>m.Centro==s.Centro&&m.Item==s.Item&&m.SapMaterial==raw).ToArray();
  if(mappings.Length>1)throw new AcmR57BlockedException("ACM_MATERIAL_MAPPING_AMBIGUOUS");
  string? approvedCode=null;
  if(mappings.Length==1)
  {if(!AcmR57NormalPreparation.Proven(mappings[0].GuardianCode)||!AcmR57NormalPreparation.Proven(mappings[0].EvidenceReference))throw new AcmR57BlockedException("ACM_MATERIAL_MAPPING_UNPROVEN");approvedCode=mappings[0].GuardianCode;}
  // A contract-specific alias cannot be discarded in favour of a numeric fallback.
  // An alias for another contract remains unrelated to this admission.
  var exact=o.ApprovedMaterialMapping;
  if(exact is not null&&exact.NumeroContrato==v.NumeroContrato&&exact.Item==s.Item&&exact.Centro==s.Centro)
  {
   if(exact.NumeroContrato!=s.NumeroContrato||exact.SapMaterial!=raw||!AcmR57NormalPreparation.Proven(exact.GuardianCode)||!AcmR57NormalPreparation.Proven(exact.EvidenceReference))throw new AcmR57BlockedException("ACM_MATERIAL_MAPPING_UNPROVEN");
   if(approvedCode is not null&&approvedCode!=exact.GuardianCode)throw new AcmR57BlockedException("ACM_MATERIAL_MAPPING_AMBIGUOUS");
   approvedCode=exact.GuardianCode;
  }
  var candidate=approvedCode??(raw.All(char.IsAsciiDigit)?raw.TrimStart('0'):raw);
  if(!GuardianMaterialCode.IsValid(candidate))throw new AcmR57BlockedException("ACM_MATERIAL_CODE_INVALID");
  return candidate;
 }
 public async Task ValidarAsync(VersaoContratoSapToledo version,CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested();proof=null;
  if(!UsesDynamic(options,version)||options.ManualRecoveryScope is {} manual&&!manual.IsOpen(clock.GetUtcNow()))throw new AcmR57BlockedException("ACM_MATERIAL_SCOPE_REQUIRED");
  var candidate=Candidate(options,version);GuardianMaterialReadResult result;
  try{result=await lookup.ConsultarAsync(candidate,ct);}catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch{throw new AcmR57BlockedException("ACM_MATERIAL_LOOKUP_UNVERIFIED");}
  if(!result.Confirmado)
  {var allowed=new[]{"ACM_MATERIAL_NOT_FOUND","ACM_MATERIAL_INACTIVE","ACM_MATERIAL_LOOKUP_UNVERIFIED","ACM_MATERIAL_AUTHENTICATION_FAILED","ACM_MATERIAL_AUTHORIZATION_FAILED","ACM_MATERIAL_IDENTITY_MISMATCH","ACM_MATERIAL_SCOPE_MISMATCH","ACM_MATERIAL_RESPONSE_INVALID","ACM_MATERIAL_PROFILE_INVALID","ACM_MATERIAL_CODE_INVALID"};throw new AcmR57BlockedException(allowed.Contains(result.Motivo,StringComparer.Ordinal)?result.Motivo:"ACM_MATERIAL_LOOKUP_UNVERIFIED");}
  if(result.IdAssinatura!=options.DynamicEmitterSignatureId)throw new AcmR57BlockedException("ACM_MATERIAL_SCOPE_MISMATCH");
  if(result.Http!=200||result.Codigo!=candidate||result.Sequencial<=0)throw new AcmR57BlockedException("ACM_MATERIAL_IDENTITY_MISMATCH");
  proof=new(version.IntegracaoId,version.FingerprintSha256,version.Snapshot.Material!,version.Snapshot.Item,version.Snapshot.Centro!,candidate,result.Sequencial,result.IdAssinatura,clock.GetUtcNow());
 }
 public string Exigir(VersaoContratoSapToledo v)
 {
  if(!UsesDynamic(options,v)||options.ManualRecoveryScope is {} manual&&!manual.IsOpen(clock.GetUtcNow()))throw new AcmR57BlockedException("ACM_MATERIAL_SCOPE_REQUIRED");
  if(proof is null||proof.IntegrationId!=v.IntegracaoId||proof.Fingerprint!=v.FingerprintSha256||proof.SapMaterial!=v.Snapshot.Material||proof.Item!=v.Snapshot.Item||proof.Centro!=v.Snapshot.Centro||proof.Signature!=options.DynamicEmitterSignatureId||proof.Codigo!=Candidate(options,v))throw new AcmR57BlockedException("ACM_MATERIAL_VALIDATION_REQUIRED");
  var age=clock.GetUtcNow()-proof.At;if(age<TimeSpan.Zero||age>TimeSpan.FromMinutes(2))throw new AcmR57BlockedException("ACM_MATERIAL_PROOF_EXPIRED");
  return proof.Codigo;
 }
}
