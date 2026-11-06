using System.Text;
using FugaTES.Aplicacao.Contratos.Mm;
using FugaTES.Aplicacao.Modelos.Mm;
namespace FugaTES.Aplicacao.Servicos.Mm;
/// <summary>Called only sequentially by the existing owner of the MM worker lease.</summary>
public sealed class MmRetryExecutor(IMmRetryStore store,IMmMasterdataGate gate,IEnviarProtocoloMmGateway guardian,MmRetryOptions options)
{
 int busy;
 public async Task<bool> ProcessOneAsync(IReadOnlyList<PedidoCompraMmLeitura> rows,Func<bool> admission,Func<CancellationToken,Task<bool>> ownership,CancellationToken ct)
 {
  if(!options.Enabled||Interlocked.CompareExchange(ref busy,1,0)!=0)return false;
  try
  {
   if(!admission()||!await ownership(ct))return false;
   var command=await store.ClaimAsync(ct);if(command is null)return false;
   bool dispatched=false;ResultadoProtocoloMm outcome=new(EstadoProtocoloMm.Recusado,null,null,"MM_RETRY_REVALIDATION_REQUIRED");
   try
   {
    var row=rows.SingleOrDefault(x=>MapeadorProtocoloMm.ChaveNatural(x)==command.Previous.Chave);
    var current=await store.ReadRegistryAsync(command.Previous.Pedido,command.Previous.Item,ct);
    var decision=row is null||current is null?new MmRetryDecision(false,"MM_RETRY_SOURCE_CHANGED",""):MmRetryPolicy.Evaluate(new(row,current,admission(),false),options.LegacyEvidence);
    if(!decision.Allowed||row is null||current is null||command.OperatorSid!=options.OperatorSid||MmRetryPolicy.Digest(row,current)!=command.SnapshotDigest)
     outcome=new(EstadoProtocoloMm.Recusado,null,null,decision.Allowed?"MM_RETRY_PREVIEW_CHANGED":decision.Code);
    else
    {
     var bytes=Encoding.UTF8.GetBytes(current.JsonEnviado);var check=await gate.CheckAsync(bytes,ct);
     if(!check.Confirmed)outcome=new(EstadoProtocoloMm.Recusado,null,null,check.Code);
     else if(!admission()||!await ownership(ct))outcome=new(EstadoProtocoloMm.Recusado,null,null,"MM_RETRY_LEASE_LOST_BEFORE_POST");
     else if(!await store.MarkDispatchAsync(command,ct))throw new InvalidOperationException("MM_RETRY_START_NOT_COMMITTED");
     else if(!admission()||!await ownership(ct))outcome=new(EstadoProtocoloMm.Indeterminado,null,null,"MM_RETRY_LEASE_LOST_NO_REPLAY");
     else
     {
      // An independent drain deadline keeps a UI/host cancellation from erasing external evidence.
      using var delivery=new CancellationTokenSource(TimeSpan.FromSeconds(45));dispatched=true;
      outcome=await guardian.EnviarAsync(bytes,delivery.Token);
      if(outcome.Estado==EstadoProtocoloMm.Criado&&(outcome.Http is not(>=200 and <300)||outcome.ProtocoloId is not >0))outcome=new(EstadoProtocoloMm.Indeterminado,outcome.Http,null,"MM_RETRY_RESULT_UNCONFIRMED");
     }
    }
   }
   catch(Exception) {outcome=new(dispatched?EstadoProtocoloMm.Indeterminado:EstadoProtocoloMm.Recusado,null,null,dispatched?"MM_RETRY_EXTERNAL_UNCONFIRMED":"MM_RETRY_PRE_POST_BLOCKED");}
   using var closing=new CancellationTokenSource(TimeSpan.FromSeconds(15));await store.FinishAsync(command,outcome,dispatched,closing.Token);return true;
  }
  finally{Volatile.Write(ref busy,0);}
 }
}
