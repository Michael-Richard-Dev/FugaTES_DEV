namespace FugaTES.Callback.Http;

public static class UnifiedCallbackRoutes
{
    // Called by the coordinated Program patch, not by a new host or service.
    public static void Map(WebApplication app,CallbackHttpOptions? legacy)
    {
        MasterdataCallbackEndpoint.Map(app);
        if(legacy is not null)GuardianCallbackEndpoint.MapDual(app,legacy.Route);
        // Fluxo SD: rota propria. So e exposta quando a composicao do receptor esta ligada -
        // publicar o endereco sem ter onde gravar faria o Guardian receber 503 por POST.
        if(app.Services.GetService<FugaTES.Aplicacao.Contratos.Sd.IDevolutivasSdRepositorio>() is not null
           || app.Configuration[DevolutivaSdComposition.Flag] == "true")
            DevolutivaSdEndpoint.Map(app);
    }
}
