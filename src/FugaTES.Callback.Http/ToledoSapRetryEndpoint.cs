using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Aplicacao.Servicos.Recebimento;
namespace FugaTES.Callback.Http;
public static class ToledoSapRetryEndpoint
{
 public const string Route="/api/v1/toledo/sap/manual-retry/{receipt:guid}";
 public static void MapCandidate(IEndpointRouteBuilder endpoints)
 {
  endpoints.MapGet(Route+"/preview",Preview).RequireAuthorization(AcmManualRetryEndpoint.Policy);
  endpoints.MapPost(Route,Submit).RequireAuthorization(AcmManualRetryEndpoint.Policy);
 }
 static AcmRetryActor Actor(HttpContext c)
 {var sids=c.User.FindAll(ClaimTypes.PrimarySid).Select(x=>x.Value).Distinct().ToArray();return new(c.User.Identity?.IsAuthenticated==true&&sids.Length==1,sids.Length==1?sids[0]:"");}
 static async Task<IResult> Preview(HttpContext c,Guid receipt,ReenvioToledoSapService service)
 {
  c.Response.Headers.CacheControl="no-store";
  var actor=Actor(c);if(!actor.Authenticated)return Results.StatusCode(403);
  try{var preview=await service.PrepararAsync(receipt,actor,c.RequestAborted);return preview is null?Results.StatusCode(403):Results.Json(preview);}
  catch{return Results.Json(new{code="TOLEDO_SAP_RETRY_PREFLIGHT_UNAVAILABLE"},statusCode:503);}
 }
 static async Task<IResult> Submit(HttpContext c,Guid receipt,ReenvioToledoSapService service)
 {
  c.Response.Headers.CacheControl="no-store";var actor=Actor(c);if(!actor.Authenticated)return Results.StatusCode(403);
  if(!c.Request.HasJsonContentType())return Results.StatusCode(415);
  var bytes=new byte[4097];var n=0;
  while(n<bytes.Length){var read=await c.Request.Body.ReadAsync(bytes.AsMemory(n),c.RequestAborted);if(read==0)break;n+=read;}
  if(n>4096)return Results.StatusCode(413);
  try
  {
   var command=JsonSerializer.Deserialize<ComandoReenvioToledoSap>(bytes.AsSpan(0,n),new JsonSerializerOptions(JsonSerializerDefaults.Web){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow});
   if(command is null||command.ReciboId!=receipt)return Results.BadRequest();
   var result=await service.SolicitarAsync(command,actor,c.RequestAborted);return Results.Json(result,statusCode:result.Estado=="QUEUED"?202:409);
  }
  catch(JsonException){return Results.BadRequest();}
  catch{return Results.Json(new{state="UNKNOWN",code="TOLEDO_SAP_COMMAND_UNVERIFIED_NO_RETRY"},statusCode:503);}
 }
}
