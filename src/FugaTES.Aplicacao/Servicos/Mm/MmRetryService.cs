using System.Text;
using FugaTES.Aplicacao.Contratos.Mm;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.ManualRetry;
namespace FugaTES.Aplicacao.Servicos.Mm;
/// <summary>Authenticated admission only. This service never dispatches Guardian POSTs.</summary>
public sealed class MmRetryService(IMmRetryStore store,Func<CancellationToken,Task<MmPurchaseOrderSnapshot>> source,IMmMasterdataGate gate,MmRetryOptions options,TimeProvider clock)
{
 bool Actor(AcmRetryActor actor)=>options.Enabled&&actor.Authenticated&&actor.Sid==options.OperatorSid;
 public async Task<MmRetryPreview> PreviewAsync(string pedido,string item,AcmRetryActor actor,CancellationToken ct)
 {
  MmRetryPreview Block(string c)=>new(pedido,item,Guid.Empty,"",actor.Sid,new(false,c,MmRetryPolicy.Describe(c)));
  if(!Actor(actor)||!MmRetryPolicy.Key(pedido,10)||!MmRetryPolicy.Key(item,5))return Block("MM_RETRY_NOT_AUTHORIZED");
  options.Validate();var snapshot=await source(ct);var row=snapshot.Items.SingleOrDefault(x=>MapeadorProtocoloMm.Codigo(x.Pedido)==pedido&&MapeadorProtocoloMm.Codigo(x.Item)==item);
  if(row is null)return Block("MM_RETRY_SAP_SCOPE_REJECTED");
  var previous=await store.ReadRegistryAsync(pedido,item,ct);if(previous is null)return Block("MM_RETRY_RESERVATION_REQUIRED");
  var decision=MmRetryPolicy.Evaluate(new(row,previous,snapshot.IsFresh(clock.GetUtcNow()),await store.HasPendingAsync(previous.Chave,ct)),options.LegacyEvidence);
  if(decision.Allowed)
  {
   using var bounded=CancellationTokenSource.CreateLinkedTokenSource(ct);bounded.CancelAfter(TimeSpan.FromSeconds(6));
   var check=await gate.CheckAsync(Encoding.UTF8.GetBytes(previous.JsonEnviado),bounded.Token);
   if(!check.Confirmed)decision=new(false,check.Code,MmRetryPolicy.Describe(check.Code));
  }
  return new(pedido,item,previous.ReservaId,MmRetryPolicy.Digest(row,previous),actor.Sid,decision);
 }
 public async Task<MmRetryReceipt?> StatusAsync(Guid id,AcmRetryActor actor,CancellationToken ct)
 {
  if(!Actor(actor)||id==Guid.Empty)return null;var value=await store.FindCommandAsync(id,ct);return value?.OperatorSid==actor.Sid?value.Receipt():null;
 }
 public async Task<MmRetryReceipt> SubmitAsync(MmRetryRequest request,AcmRetryActor actor,CancellationToken ct)
 {
  MmRetryReceipt Block(string c)=>new(request.CommandId,request.Pedido,request.Item,"BLOCKED",c);
  if(!Actor(actor)||request.CommandId==Guid.Empty||!MmRetryOptions.Hash(request.SnapshotDigest)||request.Justification is null||request.Justification.Trim().Length is <8 or >500||request.Justification.Any(char.IsControl)||!MmRetryPolicy.Key(request.Pedido,10)||!MmRetryPolicy.Key(request.Item,5))return Block("MM_RETRY_REQUEST_INVALID");
  var existing=await store.FindCommandAsync(request.CommandId,ct);
  if(existing is not null)return existing.OperatorSid==actor.Sid&&existing.Previous.Pedido==request.Pedido&&existing.Previous.Item==request.Item&&existing.SnapshotDigest==request.SnapshotDigest&&existing.Justification==request.Justification.Trim()?existing.Receipt():Block("MM_RETRY_COMMAND_CONFLICT");
  var preview=await PreviewAsync(request.Pedido,request.Item,actor,ct);
  if(!preview.Decision.Allowed)return Block(preview.Decision.Code);
  if(preview.SnapshotDigest!=request.SnapshotDigest)return Block("MM_RETRY_PREVIEW_CHANGED");
  var snapshot=await source(ct);var row=snapshot.Items.SingleOrDefault(x=>MapeadorProtocoloMm.Codigo(x.Pedido)==request.Pedido&&MapeadorProtocoloMm.Codigo(x.Item)==request.Item);
  var previous=await store.ReadRegistryAsync(request.Pedido,request.Item,ct);
  if(row is null||previous is null||!snapshot.IsFresh(clock.GetUtcNow())||MmRetryPolicy.Digest(row,previous)!=request.SnapshotDigest)return Block("MM_RETRY_PREVIEW_CHANGED");
  return await store.EnqueueAsync(new(){Id=request.CommandId,Previous=previous,SnapshotDigest=request.SnapshotDigest,OperatorSid=actor.Sid,Justification=request.Justification.Trim(),CreatedUtc=clock.GetUtcNow()},ct);
 }
}
