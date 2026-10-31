using Microsoft.AspNetCore.Http.Features;
using FugaTES.Aplicacao.Contratos.Recebimento;
namespace FugaTES.Callback.Http;

// Availability probes are separate HTTP verbs. Never create a receipt or claim durable acceptance.
public static class ToledoCallbackProbeEndpoint
{
 public const string BaseRoute="/api/guardian/set26/callback/";
 public const string ReceiptRoute="/api/guardian/set26/callback/recebimento";
 public static void Map(IEndpointRouteBuilder endpoints,string receiptRoute)
 {
  if(receiptRoute!=ReceiptRoute)return;
  endpoints.MapMethods(BaseRoute,["GET","HEAD"],HandleAsync);
  endpoints.MapMethods(ReceiptRoute,["GET","HEAD"],HandleAsync);
 }
 static async Task HandleAsync(HttpContext context)
 {
  context.Response.Headers.CacheControl="no-store";
  var admission=context.RequestServices.GetService<IToledoCallbackAdmission>();
  if(admission is null||context.RequestServices.GetService<IRegistroPesagemToledoStore>() is null)
  {context.Response.StatusCode=404;return;}
  var actor=await admission.AuthorizeAsync(context,context.RequestAborted);
  if(!actor.Accepted){context.Response.StatusCode=actor.HttpStatus;await context.Response.WriteAsJsonAsync(new{error=actor.Code},context.RequestAborted);return;}
  if(context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody==true||context.Request.ContentLength is >0||context.Request.Headers.TransferEncoding.Count>0||context.Request.QueryString.HasValue)
  {context.Response.StatusCode=400;await context.Response.WriteAsJsonAsync(new{error="PROBE_REQUIRES_EMPTY_GET_OR_HEAD"},context.RequestAborted);return;}
  context.Response.StatusCode=200;
  await context.Response.WriteAsJsonAsync(new{status="RECEIVER_READY",probe=true,persisted=false,receiptCreated=false,method="POST",endpoint=ReceiptRoute},context.RequestAborted);
 }
}
