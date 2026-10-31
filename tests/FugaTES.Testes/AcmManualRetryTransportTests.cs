using System.Net;
using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Infraestrutura.ManualRetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public sealed class AcmManualRetryTransportTests
{
    const string Sid="S-1-5-21-1-2-3-1001";
    [Theory][InlineData("http://127.0.0.1:18761")][InlineData("http://127.0.0.1:18762")][InlineData("https://guardian.cloudprix.com.br")]
    public void Client_cannot_target_read_API_or_external_business_endpoint(string endpoint)
        =>Assert.Throws<InvalidOperationException>(()=>new AuthenticatedAcmRetryCommandClient(new(new(endpoint),true,"OFFLINE_ONLY",Sid,DateTimeOffset.UtcNow.AddMinutes(1)),new string('X',40),TimeProvider.System));
    [Fact]public async Task Unhomologated_binding_makes_no_request_and_redirect_is_not_followed()
    {
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();var calls=0;var redirectsFollowed=0;
        app.MapPost("/api/v1/acm/manual-retry/{id:guid}",(Guid id)=>{calls++;return Results.Redirect("/forbidden-secondary-request",permanent:false,preserveMethod:true);});
        app.MapPost("/forbidden-secondary-request",()=>{redirectsFollowed++;return Results.Ok();});await app.StartAsync();
        var uri=new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var command=new AcmRetryRequest(Guid.NewGuid(),Guid.NewGuid(),new string('A',64),"Offline justification only",true);
        using(var disabled=new AuthenticatedAcmRetryCommandClient(new(uri,false,"OFFLINE_ONLY",Sid,DateTimeOffset.UtcNow.AddMinutes(1)),new string('X',40),TimeProvider.System))
            Assert.Equal("BLOCKED",(await disabled.SubmitAsync(command,default)).State);
        Assert.Equal(0,calls);
        using(var enabled=new AuthenticatedAcmRetryCommandClient(new(uri,true,"OFFLINE_ONLY",Sid,DateTimeOffset.UtcNow.AddMinutes(1)),new string('X',40),TimeProvider.System))
            Assert.Equal("UNKNOWN",(await enabled.SubmitAsync(command,default)).State);
        Assert.Equal(1,calls);Assert.Equal(0,redirectsFollowed);await app.StopAsync();
    }
}
