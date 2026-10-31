using FugaTES.Callback.Http;

if(FugaTES.Callback.Http.ManualRetry.AcmManualRetryCommandHost.Selected(args))
{
    await FugaTES.Callback.Http.ManualRetry.AcmManualRetryCommandHost.RunAsync();
    return;
}

if(DevUnifiedReadApi.Selected(args))
{
    await DevUnifiedReadApi.RunAsync();
    return;
}

if(ToledoSapAuthenticationReadOnly.Selected(args))
{
    Environment.ExitCode=await ToledoSapAuthenticationReadOnly.RunAsync(args[1]);
    return;
}

var builder = WebApplication.CreateBuilder(args);
var routes = InboundHttpHostComposition.Configurar(
    builder.Services, builder.Configuration, builder.Environment.IsDevelopment());
var protocolCommands = GuardianProtocolCommandEndpoint.Configurar(builder.Services, builder.Configuration);

var app = builder.Build();
HealthEndpoint.Map(app);
if (protocolCommands)
    GuardianProtocolCommandEndpoint.Map(app);
MasterdataCallbackEndpoint.Map(app);
if (routes.Callback is { } callback)
{
    GuardianCallbackEndpoint.MapDual(app, callback.Route);
}
if (routes.SapProductive)
    SapContractInboundEndpoint.MapDurable(app);
if (routes.SapOffline)
    SapContractInboundEndpoint.Map(app);
app.Run();
