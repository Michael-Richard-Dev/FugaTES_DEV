using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Aplicacao.Servicos.Mm;
namespace FugaTES.Callback.Http;
public static class MmRetryEndpoint
{
 public const string Route="/api/v1/mm/manual-retry/{pedido}/{item}";
 public static void MapCandidate(IEndpointRouteBuilder app)
 {
  app.MapGet(Route+"/preview",Preview).RequireAuthorization(AcmManualRetryEndpoint.Policy);
  app.MapGet("/api/v1/mm/manual-retry/commands/{commandId:guid}",Status).RequireAuthorization(AcmManualRetryEndpoint.Policy);
  app.MapPost(Route,Submit).RequireAuthorization(AcmManualRetryEndpoint.Policy);
 }
 static AcmRetryActor Actor(HttpContext c)
 {var sids=c.User.FindAll(ClaimTypes.PrimarySid).Select(x=>x.Value).Distinct().ToArray();return new(c.User.Identity?.IsAuthenticated==true&&sids.Length==1,sids.Length==1?sids[0]:"");}
 static async Task<IResult> Preview(HttpContext c,string pedido,string item,MmRetryService service)
 {
  c.Response.Headers.CacheControl="no-store";var actor=Actor(c);if(!actor.Authenticated)return Results.StatusCode(403);
  try{return Results.Json(await service.PreviewAsync(pedido,item,actor,c.RequestAborted));}
  catch{return Results.Json(new{code="MM_RETRY_PREFLIGHT_UNAVAILABLE"},statusCode:503);}
 }
 static async Task<IResult> Status(HttpContext c,Guid commandId,MmRetryService service)
 {
  c.Response.Headers.CacheControl="no-store";
  try{var value=await service.StatusAsync(commandId,Actor(c),c.RequestAborted);return value is null?Results.NotFound():Results.Json(value);}
  catch{return Results.Json(new{code="MM_RETRY_STATUS_UNAVAILABLE"},statusCode:503);}
 }
 static async Task<IResult> Submit(HttpContext c,string pedido,string item,MmRetryService service)
 {
  c.Response.Headers.CacheControl="no-store";var actor=Actor(c);if(!actor.Authenticated)return Results.StatusCode(403);
  if(!c.Request.HasJsonContentType())return Results.StatusCode(415);
  var bytes=new byte[4097];int n=0;
  while(n<bytes.Length){var read=await c.Request.Body.ReadAsync(bytes.AsMemory(n),c.RequestAborted);if(read==0)break;n+=read;}
  if(n>4096)return Results.StatusCode(413);
  try
  {
   var value=JsonSerializer.Deserialize<MmRetryRequest>(bytes.AsSpan(0,n),new JsonSerializerOptions(JsonSerializerDefaults.Web){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow});
   if(value is null||value.Pedido!=pedido||value.Item!=item)return Results.BadRequest();
   var result=await service.SubmitAsync(value,actor,c.RequestAborted);
   return Results.Json(result,statusCode:result.State=="QUEUED"?202:result.State=="COMPLETED"?200:409);
  }
  catch(JsonException){return Results.BadRequest();}
  catch{return Results.Json(new{code="MM_RETRY_COMMIT_UNVERIFIED_NO_RETRY"},statusCode:503);}
 }
}
