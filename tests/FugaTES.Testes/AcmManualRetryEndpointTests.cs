using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FugaTES.Callback.Http;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class AcmManualRetryEndpointTests
{
    [Fact]public async Task Command_endpoint_uses_authenticated_claims_not_forged_operator_headers()
    {
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("OfflineOnly").AddScheme<AuthenticationSchemeOptions,OfflineAuth>("OfflineOnly",_=>{});
        builder.Services.AddAuthorization(o=>o.AddPolicy(AcmManualRetryEndpoint.Policy,p=>p.RequireAuthenticatedUser().RequireClaim("permission","acm.retry")));
        var store=new NoWriteStore();builder.Services.AddSingleton(new AcmManualRetryService(store,new NoApproval(),TimeProvider.System));
        await using var app=builder.Build();app.UseAuthentication();app.UseAuthorization();AcmManualRetryEndpoint.MapCandidate(app);await app.StartAsync();
        using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
        var id=Guid.NewGuid();var route=AcmManualRetryEndpoint.Route.Replace("{integrationId:guid}",id.ToString("D"));
        http.DefaultRequestHeaders.Add("X-FugaTES-Operator-Sid","S-1-5-21-1-2-3-1001");
        Assert.Equal(HttpStatusCode.Unauthorized,(await http.GetAsync(route+"/preview")).StatusCode);Assert.Equal(0,store.Reads);
        http.DefaultRequestHeaders.Add("OfflineTestAuth","no-permission");
        Assert.Equal(HttpStatusCode.Forbidden,(await http.GetAsync(route+"/preview")).StatusCode);Assert.Equal(0,store.Reads);
        http.DefaultRequestHeaders.Remove("OfflineTestAuth");http.DefaultRequestHeaders.Add("OfflineTestAuth","valid");
        var preview=await http.GetFromJsonAsync<AcmRetryPreview>(route+"/preview");Assert.False(preview!.Decision.Eligible);
        var bad=new StringContent("{\"operatorSid\":\"FORGED\"}",System.Text.Encoding.UTF8,"application/json");
        Assert.Equal(HttpStatusCode.BadRequest,(await http.PostAsync(route,bad)).StatusCode);Assert.Equal(0,store.Writes);
        await app.StopAsync();
    }
    sealed class OfflineAuth(IOptionsMonitor<AuthenticationSchemeOptions> o,ILoggerFactory l,UrlEncoder e):AuthenticationHandler<AuthenticationSchemeOptions>(o,l,e)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if(!Request.Headers.TryGetValue("OfflineTestAuth",out var header))return Task.FromResult(AuthenticateResult.NoResult());
            var claims=new List<Claim>{new(ClaimTypes.PrimarySid,"S-1-5-21-1-2-3-1001")};if(header=="valid")claims.Add(new("permission","acm.retry"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims,Scheme.Name)),Scheme.Name)));
        }
    }
    sealed class NoApproval:IAcmRetryApprovals{public Task<AcmRetryApproval?> ResolveAsync(AcmRetryActor a,Guid id,CancellationToken ct)=>Task.FromResult<AcmRetryApproval?>(null);}
    sealed class NoWriteStore:IAcmManualRetryStore
    {
        public int Reads,Writes;
        public Task<AcmRetrySnapshot?> ReadAsync(Guid id,CancellationToken ct){Reads++;return Task.FromResult<AcmRetrySnapshot?>(null);}
        public Task<AcmRetryAdmission> AdmitAsync(AcmRetryRequest r,AcmRetryActor a,AcmRetryApproval p,DateTimeOffset now,CancellationToken ct){Writes++;throw new InvalidOperationException("NO_DB_IN_ENDPOINT_TEST");}
        public Task<AcmRetryWork?> StartAsync(Guid id,AcmRetryApproval a,string w,DateTimeOffset n,CancellationToken ct)=>throw new InvalidOperationException();
        public Task CompleteAsync(AcmRetryWork w,string r,DateTimeOffset n,CancellationToken ct)=>throw new InvalidOperationException();
        public Task<Guid?> PendingCommandAsync(AcmRetryApproval a,CancellationToken ct)=>throw new InvalidOperationException();
    }
}
