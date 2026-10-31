using System.Security.Claims;
using FugaTES.Aplicacao.Servicos.ManualRetry;

namespace FugaTES.Callback.Http;

// Separate command surface, deliberately NOT mapped in Program or DevUnifiedReadApi.
// Future homologated command host must supply its authenticated operator scheme and this authorization policy.
public static class AcmManualRetryEndpoint
{
    public const string Route="/api/v1/acm/manual-retry/{integrationId:guid}";
    public const string Policy="AcmManualRetry";
    public static void MapCandidate(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route+"/preview",Preview).RequireAuthorization(Policy);
        endpoints.MapPost(Route,Submit).RequireAuthorization(Policy);
    }
    static AcmRetryActor Actor(HttpContext c)
    {
        var sids=c.User.FindAll(ClaimTypes.PrimarySid).Select(v=>v.Value).Distinct().ToArray();
        return new(c.User.Identity?.IsAuthenticated==true&&sids.Length==1,sids.Length==1?sids[0]:"");
    }
    static async Task<IResult> Preview(HttpContext context,Guid integrationId,AcmManualRetryService service)
    {
        context.Response.Headers.CacheControl="no-store";var actor=Actor(context);
        if(!actor.Authenticated)return Results.StatusCode(403);
        try{return Results.Json(await service.PreviewAsync(integrationId,actor,context.RequestAborted));}
        catch{return Results.Json(new{code="RETRY_PREVIEW_UNAVAILABLE"},statusCode:503);}
    }
    static async Task<IResult> Submit(HttpContext context,Guid integrationId,AcmManualRetryService service)
    {
        context.Response.Headers.CacheControl="no-store";var actor=Actor(context);
        if(!actor.Authenticated)return Results.StatusCode(403);
        if(!context.Request.HasJsonContentType())return Results.StatusCode(415);
        var bytes=new byte[4097];var n=0;
        while(n<bytes.Length){var read=await context.Request.Body.ReadAsync(bytes.AsMemory(n),context.RequestAborted);if(read==0)break;n+=read;}
        if(n>4096)return Results.StatusCode(413);
        try
        {
            var request=System.Text.Json.JsonSerializer.Deserialize<AcmRetryRequest>(bytes.AsSpan(0,n),new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web){UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow});
            if(request is null||request.IntegrationId!=integrationId)return Results.BadRequest();
            var result=await service.RequestAsync(request,actor,context.RequestAborted);
            return Results.Json(result,statusCode:result.Accepted?202:result.State=="UNKNOWN"?503:409);
        }
        catch(System.Text.Json.JsonException){return Results.BadRequest();}
        catch{return Results.Json(new{state="UNKNOWN",code="COMMAND_UNVERIFIED_NO_RETRY"},statusCode:503);}
    }
}
