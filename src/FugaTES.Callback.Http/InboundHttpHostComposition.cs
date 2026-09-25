namespace FugaTES.Callback.Http;

public sealed record InboundHttpRoutes(
    CallbackHttpOptions? Callback, bool SapProductive, bool SapOffline);

public static class InboundHttpHostComposition
{
    public static InboundHttpRoutes Configurar(
        IServiceCollection services, IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        CallbackHttpOptions? callback = null;
        var callbackText = configuration["Callback:Enabled"];
        if (callbackText is not null)
        {
            if (!bool.TryParse(callbackText, out var callbackEnabled))
                throw new InvalidOperationException("Callback:Enabled must be true or false.");
            if (callbackEnabled)
                callback = GuardianCallbackHttpComposition.ConfigurarServicos(services, configuration);
        }

        var productive = SapContractInboundProductiveComposition.ConfigurarServicos(services, configuration);
        var offlineText = configuration["SapContractInbound:OfflineEnabled"];
        var offline = false;
        if (offlineText is not null)
        {
            if (!bool.TryParse(offlineText, out var offlineEnabled))
                throw new InvalidOperationException("SapContractInbound:OfflineEnabled must be true or false.");
            if (offlineEnabled)
            {
                if (!isDevelopment || productive)
                    throw new InvalidOperationException("Offline SAP inbound is restricted to Development without productive mode.");
                offline = SapContractInboundOfflineComposition.ConfigurarServicos(services, configuration);
            }
        }

        return new(callback, productive, offline);
    }
}
