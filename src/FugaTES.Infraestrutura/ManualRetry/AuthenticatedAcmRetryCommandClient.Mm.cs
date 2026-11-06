using System.Net;
using System.Net.Http.Json;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
namespace FugaTES.Infraestrutura.ManualRetry;
public sealed partial class AuthenticatedAcmRetryCommandClient:IMmRetryCommandClient
{
 static string MmRoute(string pedido,string item)
 {
  if(!MmRetryPolicy.Key(pedido,10)||!MmRetryPolicy.Key(item,5))throw new InvalidOperationException("MM_RETRY_KEY_INVALID");
  return "api/v1/mm/manual-retry/"+pedido+"/"+item;
 }
 public async Task<MmRetryPreview> PreviewMmAsync(string pedido,string item,CancellationToken ct)
 {
  if(!Configured)throw new InvalidOperationException("MM_RETRY_COMMAND_NOT_CONFIGURED");
  if(prepare is not null)await prepare(ct);
  if(!Configured)throw new InvalidOperationException("MM_RETRY_COMMAND_NOT_CONFIGURED");
  using var response=await http.GetAsync(new Uri(binding.Endpoint,MmRoute(pedido,item)+"/preview"),ct);response.EnsureSuccessStatusCode();
  var value=await response.Content.ReadFromJsonAsync<MmRetryPreview>(ct);
  if(value is null||value.Decision is null||value.Pedido!=pedido||value.Item!=item||value.OperatorSid!=binding.OperatorSid||value.Decision.Allowed&&(value.ReservaId==Guid.Empty||!MmRetryOptions.Hash(value.SnapshotDigest)))throw new InvalidOperationException("MM_RETRY_PREVIEW_IDENTITY_REJECTED");return value;
 }
 public async Task<MmRetryReceipt?> StatusMmAsync(Guid commandId,CancellationToken ct)
 {
  if(!Configured||commandId==Guid.Empty)return null;
  if(prepare is not null)await prepare(ct);
  using var response=await http.GetAsync(new Uri(binding.Endpoint,"api/v1/mm/manual-retry/commands/"+commandId.ToString("D")),ct);response.EnsureSuccessStatusCode();
  var value=await response.Content.ReadFromJsonAsync<MmRetryReceipt>(ct);return value?.CommandId==commandId?value:null;
 }
 public async Task<MmRetryReceipt> SubmitMmAsync(MmRetryRequest request,CancellationToken ct)
 {
  MmRetryReceipt Unknown(string code)=>new(request.CommandId,request.Pedido,request.Item,"UNKNOWN",code);
  if(!Configured)return new(request.CommandId,request.Pedido,request.Item,"BLOCKED","MM_RETRY_COMMAND_NOT_CONFIGURED");
  try
  {
   if(prepare is not null)await prepare(ct);
   if(!Configured)return new(request.CommandId,request.Pedido,request.Item,"BLOCKED","MM_RETRY_COMMAND_NOT_CONFIGURED");
   using var response=await http.PostAsJsonAsync(new Uri(binding.Endpoint,MmRoute(request.Pedido,request.Item)),request,ct);
   if(response.StatusCode is not(HttpStatusCode.Accepted or HttpStatusCode.OK or HttpStatusCode.Conflict))return Unknown("MM_RETRY_RECEIPT_UNVERIFIED");
   var value=await response.Content.ReadFromJsonAsync<MmRetryReceipt>(ct);
   if(value is null||value.CommandId!=request.CommandId||value.Pedido!=request.Pedido||value.Item!=request.Item||!((response.StatusCode==HttpStatusCode.Accepted&&value.State=="QUEUED")||(response.StatusCode==HttpStatusCode.OK&&value.State=="COMPLETED")||(response.StatusCode==HttpStatusCode.Conflict&&value.State is "BLOCKED" or "UNKNOWN")))return Unknown("MM_RETRY_RECEIPT_UNVERIFIED");return value;
  }
  catch{return Unknown("MM_RETRY_TRANSPORT_UNKNOWN_NO_RETRY");}
 }
}
