namespace FugaTES.Callback.Http;

public static class UnifiedCallbackRoutes
{
    // Called by the coordinated Program patch, not by a new host or service.
    public static void Map(WebApplication app,CallbackHttpOptions? legacy)
    {
        MasterdataCallbackEndpoint.Map(app);
        if(legacy is not null)GuardianCallbackEndpoint.MapDual(app,legacy.Route);
        // A rota do fluxo SD NAO e registrada aqui: este metodo nao e chamado pelo Program atual,
        // e registrar nos dois lugares arriscaria mapear a mesma rota duas vezes se um dia passar
        // a ser. O registro vive em Program.cs, junto com os demais.
    }
}
