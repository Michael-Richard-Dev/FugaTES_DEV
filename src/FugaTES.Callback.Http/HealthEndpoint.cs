namespace FugaTES.Callback.Http;

public static class HealthEndpoint
{
    public const string Route = "/health";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(Route, () => Results.Text("OK", "text/plain"));
    }
}
