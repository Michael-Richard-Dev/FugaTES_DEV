using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Modelos.Mm;
namespace FugaTES.Aplicacao.Servicos.Mm;
public sealed class MmMasterdataGate(IGuardianEmitterRead emitters,IGuardianMaterialRead materials,int signatureId,TimeProvider? clock=null):IMmMasterdataGate
{
 public async Task<MmMasterdataCheck> CheckAsync(byte[] payload,CancellationToken ct)
 {
  var now=(clock??TimeProvider.System).GetUtcNow();string emitter="",material="";
  MmMasterdataCheck Block(string code)=>new(false,code,emitter,material,now);
  try
  {
   if(signatureId<=0||payload.Length>65536)return Block("MM_PRECHECK_SCOPE_INVALID");
   var dto=JsonSerializer.Deserialize<ProtocoloPreCadastroMmRequest>(payload,new JsonSerializerOptions(JsonSerializerDefaults.Web){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow});
   if(dto?.DadosProtocoloPreCadastro is not{} data)return Block("MM_PRECHECK_DOCUMENT_INVALID");
   emitter=data.CodigoEmissor;material=data.CodigoItem;
   var e=await emitters.ConsultarAsync(emitter,ct);var m=await materials.ConsultarAsync(material,ct);
   if(e.IdAssinatura!=signatureId||m.IdAssinatura!=signatureId)return Block("MM_PRECHECK_SCOPE_INVALID");
   bool missingE=e.Motivo=="R171_EMITTER_NOT_FOUND",missingM=m.Motivo=="ACM_MATERIAL_NOT_FOUND";
   if(missingE&&missingM)return Block("MM_PRECHECK_MASTERS_MISSING");
   if(missingE)return Block("MM_PRECHECK_EMITTER_MISSING");if(missingM)return Block("MM_PRECHECK_MATERIAL_MISSING");
   if(e.Motivo=="R171_EMITTER_INACTIVE"||m.Motivo=="ACM_MATERIAL_INACTIVE")return Block("MM_PRECHECK_MASTER_INACTIVE");
   if(!e.Confirmado||!m.Confirmado||e.Http!=200||m.Http!=200||e.Sequencial<=0||m.Sequencial<=0||e.Codigo!=emitter||m.Codigo!=material)return Block("MM_PRECHECK_MASTER_UNVERIFIED");
   return new(true,"MM_PRECHECK_CONFIRMED",emitter,material,now);
  }
  catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
  catch{return Block("MM_PRECHECK_MASTER_UNVERIFIED");}
 }
}
public sealed class MmValidatedGuardianGateway(IMmMasterdataGate gate,IEnviarProtocoloMmGateway inner):IMmAdmittedGuardianGateway
{
 public Task<ResultadoProtocoloMm> EnviarAsync(byte[] payload,CancellationToken ct)
  =>Task.FromResult(new ResultadoProtocoloMm(EstadoProtocoloMm.Recusado,null,null,"MM_PRECHECK_ADMISSION_REQUIRED"));
 public async Task<ResultadoProtocoloMm> EnviarAdmitidoAsync(byte[] payload,Func<CancellationToken,Task<bool>> admission,CancellationToken ct)
 {
  try
  {
   var proof=await gate.CheckAsync(payload,ct);
   if(!proof.Confirmed)return new(EstadoProtocoloMm.Recusado,null,null,proof.Code);
   if(ct.IsCancellationRequested||!await admission(ct))return new(EstadoProtocoloMm.Recusado,null,null,"MM_PRECHECK_ADMISSION_REVOKED");
  }
  catch{return new(EstadoProtocoloMm.Recusado,null,null,"MM_PRECHECK_MASTER_UNVERIFIED");}
  // Keep the POST outside the pre-check catch: transport failures here are externally uncertain.
  return await inner.EnviarAsync(payload,ct);
 }
}
