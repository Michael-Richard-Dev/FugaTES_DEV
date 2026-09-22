using FugaTES.Callback.Http;

var builder = WebApplication.CreateBuilder(args);
var callbackOptions = GuardianCallbackHttpComposition.ConfigurarServicos(builder.Services, builder.Configuration);
var sapContractInboundOfflineEnabled = SapContractInboundOfflineComposition.ConfigurarServicos(
    builder.Services,
    builder.Configuration);

var app = builder.Build();
HealthEndpoint.Map(app);
if (callbackOptions.Enabled)
    GuardianCallbackEndpoint.Map(app, callbackOptions.Route);
if (sapContractInboundOfflineEnabled)
    SapContractInboundEndpoint.Map(app);
app.Run();
