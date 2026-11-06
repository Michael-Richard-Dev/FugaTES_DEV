using System.Collections.Concurrent;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
namespace FugaTES.WinForms.Mm;
public sealed record MmRetryUiResult(string Code,string Message,MmRetryReceipt? Receipt=null);
/// <summary>Shared Desktop latch; never contains a Guardian/SAP gateway or a writable DB connection.</summary>
public sealed class MmRetryController(IMmRetryCommandClient? client)
{
 readonly ConcurrentDictionary<string,byte> busy=new();
 readonly ConcurrentDictionary<string,MmRetryReceipt> held=new();
 readonly ConcurrentDictionary<string,string> lastReason=new();
 public bool Configured=>client?.Configured==true;
 public bool CanClick(MmGuardianMonitorRow row)=>Configured&&row.RetryCandidate&&!busy.ContainsKey(row.Registro!.Chave)&&!held.ContainsKey(row.Registro.Chave);
 public string Status(MmGuardianMonitorRow row)
 {
  if(row.Registro is null)return "Sem falha registrada para recuperar.";
  if(row.Registro.Estado==EstadoProtocoloMm.Criado)return "Criado no Guardian; reenvio desativado.";
  if(busy.ContainsKey(row.Registro.Chave))return "Validando cadastros / confirmação do operador.";
  if(held.TryGetValue(row.Registro.Chave,out var command))return command.State switch{
   "COMPLETED"=>"Criação confirmada; aguarde atualização da leitura.",
   "QUEUED"=>"Comando gravado; aguardando/processando no motor VM.",
   _=>"Comando/resultado não confirmado; não repetir."};
  if(lastReason.TryGetValue(row.Registro.Chave,out var reason))return "Última verificação: "+MmRetryReason.Describe(reason);
  return !Configured?"Comando MM não configurado.":row.RetryCandidate?"Disponível para verificar elegibilidade e cadastros.":"Bloqueado pelo estado ou identidade da integração.";
 }
 public async Task<MmRetryUiResult> ExecuteAsync(MmGuardianMonitorRow row,Func<MmRetryPreview,string?> confirm,CancellationToken ct)
 {
  if(!CanClick(row)||!busy.TryAdd(row.Registro!.Chave,0))return new("MM_RETRY_UI_BLOCKED","Item já criado, indeterminado, em processamento ou sem comando habilitado.");
  var key=row.Registro.Chave;MmRetryRequest? submitted=null;lastReason.TryRemove(key,out _);
  try
  {
   var preview=await client!.PreviewMmAsync(row.Registro.Pedido,row.Registro.Item,ct);
   if(!preview.Decision.Allowed){lastReason[key]=preview.Decision.Code;return new(preview.Decision.Code,preview.Decision.Message);}
   if(preview.ReservaId!=row.Registro.ReservaId)return new("MM_RETRY_UI_SNAPSHOT_CHANGED","A integração mudou; atualize a tela antes de reenviar.");
   var reason=confirm(preview);if(string.IsNullOrWhiteSpace(reason))return new("MM_RETRY_CANCELLED","Reenvio cancelado pelo operador.");
   submitted=new(Guid.NewGuid(),preview.Pedido,preview.Item,preview.SnapshotDigest,reason.Trim());
   var result=await client.SubmitMmAsync(submitted,ct);
   if(result.State is "QUEUED" or "UNKNOWN" or "COMPLETED")held[key]=result;
   return new(result.Code,result.State switch{"QUEUED"=>"Comando gravado. O motor MM da VM fará a revalidação no próximo ciclo (até 3 minutos) e uma única tentativa; não clique novamente.","COMPLETED"=>"Protocolo já confirmado; novo envio bloqueado.","UNKNOWN"=>"Resultado do comando não confirmado. Não repetir; consulte o estado durável.",_=>MmRetryPolicy.Describe(result.Code)},result);
  }
  catch
  {
   if(submitted is not null)
   {var unknown=new MmRetryReceipt(submitted.CommandId,submitted.Pedido,submitted.Item,"UNKNOWN","MM_RETRY_TRANSPORT_UNKNOWN_NO_RETRY");held[key]=unknown;return new(unknown.Code,"Resultado do comando não confirmado. Não repetir; consultar estado durável.",unknown);}
   return new("MM_RETRY_COMMAND_UNAVAILABLE","Não foi possível validar o comando MM. Nenhum envio direto foi feito pela tela.");
  }
  finally{busy.TryRemove(key,out _);}
 }
 public async Task RefreshAsync(CancellationToken ct)
 {
  if(client is null)return;
  foreach(var entry in held.ToArray())
  {
   if(entry.Value.State=="COMPLETED")continue;
   try{var status=await client.StatusMmAsync(entry.Value.CommandId,ct);if(status is null||status.Pedido!=entry.Value.Pedido||status.Item!=entry.Value.Item)continue;
    if(status.State=="BLOCKED"){lastReason[entry.Key]=status.Code;held.TryRemove(entry.Key,out _);}else held[entry.Key]=status;}
   catch(OperationCanceledException) when(ct.IsCancellationRequested){return;}
   catch{/* An unavailable status never releases an uncertain command. */}
  }
 }
}
