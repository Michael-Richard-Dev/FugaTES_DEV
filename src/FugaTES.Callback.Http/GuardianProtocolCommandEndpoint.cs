using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.HostIntegracao;

namespace FugaTES.Callback.Http;

public static class GuardianProtocolCommandEndpoint
{
    public const string Route = "/api/v1/acm/guardian/protocolo/{integrationId:guid}";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static bool Configurar(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("AcmProtocolCommand");
        var options = section.Get<GuardianProtocolCommandOptions>() ?? new();
        if (!options.Enabled) return false;
        var windowText = configuration["MotorIntegracao:AcmInboundWindow:Enabled"];
        if (windowText is not null && (!bool.TryParse(windowText, out var windowEnabled) || windowEnabled))
            throw new InvalidOperationException("ACM_PROTOCOL_COMMAND_HML_BINDING_REQUIRED");
        var secret = section["BearerSecret"];
        if (options.Environment != "HML" || options.IntegrationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(options.Contract) || string.IsNullOrWhiteSpace(options.OperatorSid) ||
            !options.OperatorSid.StartsWith("S-1-5-21-", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(secret) || secret.Length < 32 || secret.Any(char.IsControl) ||
            !bool.TryParse(configuration["FUGATES_INTEGRATION_WORKER_ENABLED"], out var worker) || worker ||
            configuration["HostIntegracao:PersistenceMode"] != "PostgreSql" ||
            configuration["HostIntegracao:ExternalMode"] != "Guardian" ||
            configuration["AcmR57:Normal:GuardianEnvironment"] != "HML")
            throw new InvalidOperationException("ACM_PROTOCOL_COMMAND_HML_BINDING_REQUIRED");
        HostIntegracaoComposition.ConfigurarServicos(services, configuration);
        services.AddSingleton(options);
        services.AddScoped<IGuardianProtocolCommandService, GuardianProtocolCommandService>();
        return true;
    }

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route + "/preview", HandlePreviewAsync);
        endpoints.MapPost(Route + "/send", HandleSendAsync);
    }

    public static async Task HandlePreviewAsync(HttpContext context, Guid integrationId)
    {
        if (!Authorized(context, integrationId)) { await Denied(context); return; }
        try
        {
            var view = await context.RequestServices.GetRequiredService<IGuardianProtocolCommandService>()
                .PreviewAsync(integrationId, context.RequestAborted);
            await Write(context, view);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch
        {
            context.Response.StatusCode = 503;
            await Write(context, new { canSend = false, state = "UNAVAILABLE", blockCode = "ACM_PROTOCOL_PREVIEW_UNAVAILABLE" });
        }
    }

    public static async Task HandleSendAsync(HttpContext context, Guid integrationId)
    {
        if (!Authorized(context, integrationId)) { await Denied(context); return; }
        if (!context.Request.HasJsonContentType()) { context.Response.StatusCode = 415; return; }
        try
        {
            var buffer = new byte[4097];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(count), context.RequestAborted);
                if (read == 0) break;
                count += read;
            }
            if (count > 4096) { context.Response.StatusCode = 413; return; }
            var command = JsonSerializer.Deserialize<SendRequest>(buffer.AsSpan(0, count), JsonOptions);
            if (command is null || !command.Confirm || command.PayloadSha256 is not { Length: 64 } hash ||
                !hash.All(char.IsAsciiHexDigit) || command.PreviewId is not { Length: 64 } previewId ||
                !previewId.All(char.IsAsciiHexDigit))
            {
                context.Response.StatusCode = 400;
                await Write(context, new { canSend = false, state = "BLOCKED", blockCode = "ACM_PROTOCOL_CONFIRMATION_REQUIRED" });
                return;
            }
            var result = await context.RequestServices.GetRequiredService<IGuardianProtocolCommandService>()
                .SendAsync(integrationId, hash, previewId, true, context.RequestAborted);
            await Write(context, result);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = 400;
            await Write(context, new { canSend = false, state = "BLOCKED", blockCode = "ACM_PROTOCOL_INVALID_COMMAND" });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch
        {
            // The backend may already have dispatched; never present a transport failure as retryable.
            context.Response.StatusCode = 503;
            await Write(context, new { canSend = false, state = "UNKNOWN", blockCode = "ACM_PROTOCOL_RECONCILIATION_REQUIRED" });
        }
    }

    private static bool Authorized(HttpContext context, Guid id)
    {
        var options = context.RequestServices.GetService<GuardianProtocolCommandOptions>();
        if (options is not { Enabled: true, Environment: "HML" } || options.IntegrationId != id ||
            context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
            string.IsNullOrWhiteSpace(options.OperatorSid) ||
            context.Request.Headers["X-FugaTES-Operator-Sid"].ToString() != options.OperatorSid)
            return false;
        var configuration = context.RequestServices.GetService<IConfiguration>();
        var secret = configuration?["AcmProtocolCommand:BearerSecret"];
        if (secret is null || secret.Length < 32 ||
            !AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter)) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
            SHA256.HashData(Encoding.UTF8.GetBytes(authorization.Parameter)));
    }

    private static async Task Denied(HttpContext context)
    {
        context.Response.StatusCode = 403;
        await Write(context, new { canSend = false, state = "BLOCKED", blockCode = "ACM_PROTOCOL_COMMAND_ACCESS_DENIED" });
    }

    private static Task Write(HttpContext context, object value)
    {
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(value, context.RequestAborted);
    }

    private sealed record SendRequest(string PayloadSha256, string PreviewId, bool Confirm);
}
