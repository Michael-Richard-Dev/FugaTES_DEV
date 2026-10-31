namespace FugaTES.Callback.Http;

public static class UnifiedCallbackRoutes
{
    // Called by the coordinated Program patch, not by a new host or service.
    public static void Map(WebApplication app,CallbackHttpOptions? legacy)
    {
        MasterdataCallbackEndpoint.Map(app);
        if(legacy is not null)GuardianCallbackEndpoint.MapDual(app,legacy.Route);
    }
}
