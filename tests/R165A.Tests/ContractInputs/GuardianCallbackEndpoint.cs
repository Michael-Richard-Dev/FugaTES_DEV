using System.Text.Json;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Callback.Http;

public static class GuardianCallbackEndpoint
{
    public const string Route = "/api/guardian/set26/callback/recebimento";

    public static void Map(IEndpointRouteBuilder endpoints)
        => Map(endpoints, Route);

    public static void Map(IEndpointRouteBuilder endpoints, string route)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (string.IsNullOrWhiteSpace(route)) throw new ArgumentException("Callback route is required.", nameof(route));
        endpoints.MapPost(route, (HttpContext context) => HandleAsync(context));
    }

    public static void MapEvento(IEndpointRouteBuilder endpoints, string route)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (string.IsNullOrWhiteSpace(route)) throw new ArgumentException("Callback route is required.", nameof(route));
        endpoints.MapPost(route, (HttpContext context) => HandleEventoAsync(context));
    }

    public static async Task HandleEventoAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Request.HasJsonContentType())
        {
            await WriteErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "INVALID_CONTENT_TYPE");
            return;
        }
        try
        {
            var dto = await context.Request.ReadFromJsonAsync<GuardianEventoHttpDto>(context.RequestAborted)
                ?? throw new ArgumentException("Callback body is required.");
            await context.RequestServices.GetRequiredService<ProcessarEventoGuardianCasoDeUso>()
                .ExecutarAsync(dto.Mapear(), context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("OK", context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "INVALID_JSON");
        }
        catch (ArgumentException)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "INVALID_CALLBACK");
        }
        catch (InvalidOperationException)
        {
            await WriteErrorAsync(context, StatusCodes.Status409Conflict, "CALLBACK_CONFLICT");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "CALLBACK_FAILED");
        }
    }

    public static async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var dto = await context.Request.ReadFromJsonAsync<GuardianCallbackHttpDto>(context.RequestAborted)
                ?? throw new ArgumentException("Callback body is required.");
            var useCase = context.RequestServices.GetRequiredService<ProcessarCallbackGuardianMvpCasoDeUso>();
            var result = await useCase.ExecutarAsync(dto.Mapear(), context.RequestAborted);
            var status = result.Status == StatusCallbackGuardianMvp.Aceito ? "ACEITO" : "IDEMPOTENTE";
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsJsonAsync(new { status }, context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "INVALID_JSON");
        }
        catch (ArgumentException)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "INVALID_CALLBACK");
        }
        catch (InvalidOperationException)
        {
            await WriteErrorAsync(context, StatusCodes.Status409Conflict, "CALLBACK_CONFLICT");
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string error)
    {
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new { error }, CancellationToken.None);
    }
}
