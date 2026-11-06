using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Mm;
namespace FugaTES.Aplicacao.Servicos.Mm;
public static class MmRetryPolicy
{
 public static bool Key(string? value,int max)=>value is{Length:>0}&&value.Length<=max&&value[0]!='0'&&value.All(char.IsAsciiDigit);
 public static bool IsPrecheck(string? code)=>code is "MM_PRECHECK_MASTERS_MISSING" or "MM_PRECHECK_EMITTER_MISSING" or "MM_PRECHECK_MATERIAL_MISSING" or "MM_PRECHECK_MASTER_INACTIVE" or "MM_PRECHECK_MASTER_UNVERIFIED" or "MM_PRECHECK_ADMISSION_REVOKED";
 public static string Digest(PedidoCompraMmLeitura source,RegistroProtocoloMm previous)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{source=MapeadorProtocoloMm.SourceHash(source),previous}))));
 public static MmRetryDecision Evaluate(MmRetryFacts f,IReadOnlyList<MmRetryLegacyEvidence> legacy)
 {
  MmRetryDecision Deny(string c)=>new(false,c,Describe(c));
  var s=f.Source;var r=f.Previous;
  if(!f.Fresh)return Deny("MM_RETRY_STALE_SAP");if(f.PendingCommand)return Deny("MM_RETRY_ALREADY_PENDING");
  if(s is null||r is null||s.Empresa!="3000"||s.Centro is not("3011" or "3008")||s.EstadoSap!="05"||!MmCollectionPolicy.Eligible(s)||s.Quantidade<0)return Deny("MM_RETRY_SAP_SCOPE_REJECTED");
  if(r.ProtocoloId is not null||r.Estado==EstadoProtocoloMm.Criado)return Deny("MM_RETRY_ALREADY_CREATED");
  if(r.Estado!=EstadoProtocoloMm.Recusado||r.ReservaId==Guid.Empty)return Deny("MM_RETRY_OUTCOME_UNCERTAIN");
  try
  {
   if(r.Chave!=MapeadorProtocoloMm.ChaveNatural(s)||r.Pedido!=MapeadorProtocoloMm.Codigo(s.Pedido)||r.Item!=MapeadorProtocoloMm.Codigo(s.Item)||r.Centro!=s.Centro||r.SourceSha256!=MapeadorProtocoloMm.SourceHash(s)||!MmRetryOptions.Hash(r.Sha256)||Encoding.UTF8.GetByteCount(r.JsonEnviado)>65536||Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r.JsonEnviado)))!=r.Sha256)return Deny("MM_RETRY_SOURCE_CHANGED");
   var original=JsonSerializer.Deserialize<ProtocoloPreCadastroMmRequest>(r.JsonEnviado,JsonSerializerOptions.Web);
   if(original?.Configuracao?.UrlParaRetorno is not{} callback||MapeadorProtocoloMm.Serializar(MapeadorProtocoloMm.Montar(s,callback))!=r.JsonEnviado)return Deny("MM_RETRY_SOURCE_CHANGED");
  }
  catch{return Deny("MM_RETRY_SOURCE_CHANGED");}
  if(r.Http is null&&IsPrecheck(r.Mensagem))return new(true,"MM_RETRY_MASTERDATA_RECHECK_REQUIRED","Revalidar emissor e material antes da recuperação.");
  if(r.Http==400&&legacy.Any(x=>x.ReservaId==r.ReservaId&&x.PayloadSha256==r.Sha256&&x.SourceSha256==r.SourceSha256&&MmRetryOptions.Hash(x.ProofSha256)&&x.RejectedUtc==r.AtualizadoUtc))return new(true,"MM_RETRY_LEGACY_MASTERDATA_RECHECK_REQUIRED","Recusa legada com evidência nominal; revalidar os cadastros antes de enviar.");
  return Deny("MM_RETRY_CAUSE_NOT_PROVEN_CORRECTED");
 }
 public static string Describe(string code)=>MmRetryReason.Describe(code);
}
