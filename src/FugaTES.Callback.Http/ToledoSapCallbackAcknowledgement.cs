using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Servicos.Recebimento;

namespace FugaTES.Callback.Http;

public sealed record ToledoSapCallbackAckOptions(TimeSpan WaitTimeout,TimeSpan PollInterval)
{
 public static ToledoSapCallbackAckOptions Approved {get;}=new(TimeSpan.FromSeconds(45),TimeSpan.FromMilliseconds(250));
 public bool Valid=>WaitTimeout>TimeSpan.Zero&&WaitTimeout<=TimeSpan.FromSeconds(45)&&PollInterval>TimeSpan.Zero&&PollInterval<=WaitTimeout;
}
public sealed record ToledoSapCallbackAck(int HttpStatus,string Code,string Message,int? SapHttpStatus=null)
{
 public bool Confirmed=>HttpStatus==200&&Code=="SAP_CONFIRMED";
 public static ToledoSapCallbackAck Pending {get;}=new(504,"TOLEDO_SAP_CONFIRMATION_PENDING","Prazo de espera encerrado. A confirmação SAP ainda está pendente; não reenviar a pesagem automaticamente.");
 public static ToledoSapCallbackAck Unavailable {get;}=new(503,"TOLEDO_SAP_OUTCOME_UNAVAILABLE","Não foi possível comprovar o resultado durável no SAP. O recibo foi preservado; não reenviar automaticamente.");
}

// Waits for the existing motor only. Cancellation stops observation, never the independent worker.
public sealed class ToledoSapCallbackAcknowledgement(IToledoSapCallbackOutcomeRead reader,ToledoSapCallbackAckOptions options)
{
 public async Task<ToledoSapCallbackAck> WaitAsync(ReciboPesagemToledo receipt,string originalHash,TimeSpan elapsed,CancellationToken aborted)
 {
  if(!options.Valid||receipt.SapIntegracaoId is not{} integration||integration==Guid.Empty)return ToledoSapCallbackAck.Unavailable;
  var remaining=options.WaitTimeout-elapsed;if(remaining<=TimeSpan.Zero)return ToledoSapCallbackAck.Pending;
  using var deadline=new CancellationTokenSource(remaining);using var linked=CancellationTokenSource.CreateLinkedTokenSource(aborted,deadline.Token);
  try
  {
   while(true)
   {
    var state=await reader.ReadAsync(receipt.ReciboId,integration,linked.Token).WaitAsync(linked.Token);
    linked.Token.ThrowIfCancellationRequested();
    var verdict=Evaluate(state,receipt.ReciboId,integration,originalHash);
    if(verdict is not null)return verdict;
    await Task.Delay(options.PollInterval,linked.Token);
   }
  }
  catch(OperationCanceledException)when(aborted.IsCancellationRequested){throw;}
  catch(OperationCanceledException)when(deadline.IsCancellationRequested){return ToledoSapCallbackAck.Pending;}
  catch{return ToledoSapCallbackAck.Unavailable;}
 }
 public static ToledoSapCallbackAck? Evaluate(ToledoSapCallbackSnapshot? s,Guid receipt,Guid integration,string originalHash)
 {
  static bool Hash(string? h)=>h?.Length==64&&h.All(char.IsAsciiHexDigit);
  if(s is null||receipt==Guid.Empty||integration==Guid.Empty||s.Receipt!=receipt||s.Integration!=integration||s.Integrations!=1||!s.Admitted||s.Synthetic||!s.EvidenceValid||
   !Hash(originalHash)||!string.Equals(s.OriginalHash,originalHash,StringComparison.OrdinalIgnoreCase)||
   s.IntegrationHash!=ToledoSapWork.Fingerprint(receipt,s.OriginalHash)||s.Correlation!=$"toledo-sap:{receipt:D}")return ToledoSapCallbackAck.Unavailable;
  if(s.State=="RESULTADO_INDETERMINADO")return new(409,"TOLEDO_SAP_RESULT_UNKNOWN","O resultado SAP é indeterminado. É necessária conciliação antes de qualquer reenvio.");
  if(s.Active||s.State is "PENDENTE" or "EM_PROCESSAMENTO" or "AGUARDANDO_REPROCESSAMENTO")return null;
  if(s.Attempt is null||s.Attempt==Guid.Empty||s.Fence<1||s.SendPhase!="RESULTADO_RECEBIDO"||s.AttemptPhase!="RESULTADO_RECEBIDO")return ToledoSapCallbackAck.Unavailable;
  var all=s.Audits;
  if(all.Any(x=>x.Attempt!=s.Attempt||x.Fence!=s.Fence||string.IsNullOrWhiteSpace(x.Worker))||all.Count>2)return ToledoSapCallbackAck.Unavailable;
  var results=all.Where(x=>x.Event=="toledo_sap_resultado").ToArray();if(results.Length!=1||results[0].Result is not{} result)return ToledoSapCallbackAck.Unavailable;
  if(result.Indeterminado)return new(409,"TOLEDO_SAP_RESULT_UNKNOWN","O resultado SAP não foi confirmado. É necessária conciliação antes de qualquer reenvio.",result.HttpStatus);
  if(s.State=="CONCLUIDA")
  {
   var prepared=all.Where(x=>x.Event=="toledo_sap_documento_preparado").ToArray();
   if(s.AttemptOutcome!="SUCESSO"||!result.Confirmado||prepared.Length!=1||prepared[0].Worker!=results[0].Worker||
    !Hash(prepared[0].RequestSha256)||!string.Equals(prepared[0].RequestSha256,result.RequestSha256,StringComparison.OrdinalIgnoreCase)||prepared[0].PayloadVersion!=result.PayloadVersion)
    return ToledoSapCallbackAck.Unavailable;
   return new(200,"SAP_CONFIRMED","OK",result.HttpStatus);
  }
  if((s.State is "FALHA_DEFINITIVA" or "AGUARDANDO_ACAO_MANUAL")&&s.AttemptOutcome=="FALHA")
   return new(422,"TOLEDO_SAP_NOT_CONFIRMED","A pesagem foi recebida pelo FugaTES, mas o envio ao SAP não foi concluído. Consulte o monitor antes de solicitar recuperação.",result.HttpStatus);
  return ToledoSapCallbackAck.Unavailable;
 }
 public static async Task WriteAsync(HttpContext context,ReciboPesagemToledo receipt,ToledoSapCallbackAck verdict)
 {
  context.Response.StatusCode=verdict.HttpStatus;
  context.Response.Headers["X-FugaTES-Receipt-Id"]=receipt.ReciboId.ToString("D");
  if(verdict.Confirmed)
  {context.Response.ContentType="text/plain; charset=utf-8";await context.Response.WriteAsync("OK",context.RequestAborted);return;}
  var status=verdict.HttpStatus==504?"PENDING":verdict.Code=="TOLEDO_SAP_RESULT_UNKNOWN"?"INDETERMINATE":verdict.HttpStatus==503?"UNVERIFIED":"FAILED";
  await context.Response.WriteAsJsonAsync(new{status,error=verdict.Code,message=verdict.Message,
   receiptId=receipt.ReciboId,sapIntegrationId=receipt.SapIntegracaoId,sapHttpStatus=verdict.SapHttpStatus,persisted=true},context.RequestAborted);
 }
}
