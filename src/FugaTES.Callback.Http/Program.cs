using FugaTES.Callback.Http;

var builder = WebApplication.CreateBuilder(args);
var routes = InboundHttpHostComposition.Configurar(
    builder.Services, builder.Configuration, builder.Environment.IsDevelopment());

var app = builder.Build();
HealthEndpoint.Map(app);
if (routes.Callback is { } callback)
    GuardianCallbackEndpoint.Map(app, callback.Route);
if (routes.SapProductive)
    SapContractInboundEndpoint.MapDurable(app);
if (routes.SapOffline)
    SapContractInboundEndpoint.Map(app);
app.Run();
