using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace FugaTES.Callback.Http;

public static class MasterdataCallbackEndpoint
{
    public const string Route = "/api/guardian/masterdata/transportadoras/callback";

    public static void Map(WebApplication app)
    {
        var options = MasterdataCallbackOptions.Parse(app.Configuration);
        Map(app, options, new FileMasterdataCallbackReceiptStore(options.StoreDirectory));
    }

    public static void Map(IEndpointRouteBuilder endpoints, MasterdataCallbackOptions options, IMasterdataCallbackReceiptStore store)
        => endpoints.MapPost(Route, (HttpContext context) => HandleAsync(context, options, store));

    public static async Task HandleAsync(HttpContext context, MasterdataCallbackOptions options, IMasterdataCallbackReceiptStore store)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!options.Enabled) { context.Response.StatusCode = 404; return; }
        if (!options.Configured) { await Error(context, 503, "RECEIVER_NOT_CONFIGURED"); return; }
        // Deliberately ignore X-Forwarded-*; a local proxy is not proof of the external sender.
        var peer = context.Connection.RemoteIpAddress;
        var privateHmlProxy = options.AllowPrivateHmlProxyHttp && peer is not null && IPAddress.IsLoopback(peer)
            && context.Connection.LocalIpAddress is { } local && IPAddress.IsLoopback(local);
        if (peer is null || !options.AllowedRemoteAddresses.Contains(MasterdataCallbackOptions.Normalize(peer)) ||
            !context.Request.IsHttps && !(options.AllowLoopbackHttp && IPAddress.IsLoopback(peer)) && !privateHmlProxy)
        { await Error(context, 403, "SOURCE_NOT_ALLOWED"); return; }
        var secret = Environment.GetEnvironmentVariable(options.SharedSecretEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) is < 32 or > 1024)
        { await Error(context, 503, "RECEIVER_NOT_CONFIGURED"); return; }
        var header = context.Request.Headers[options.AuthenticationHeaderName];
        if (header.Count != 1 || header[0] is not { } supplied || supplied.Length > 4096 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
        { await Error(context, 401, "AUTHENTICATION_REQUIRED"); return; }
        if (!context.Request.HasJsonContentType() || context.Request.Headers.ContentEncoding.Any(x => x != "identity"))
        { await Error(context, 415, "UNSUPPORTED_CONTENT_TYPE"); return; }
        var charset = context.Request.GetTypedHeaders().ContentType?.Charset.ToString().Trim('"');
        if (!string.IsNullOrEmpty(charset) && !string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase))
        { await Error(context, 415, "UNSUPPORTED_ENCODING"); return; }
        if (context.Request.ContentLength > MasterdataCallbackOptions.MaximumBodyBytes)
        { await Error(context, 413, "BODY_TOO_LARGE"); return; }
        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MasterdataCallbackOptions.MaximumBodyBytes;
        byte[] body;
        try
        {
            using var received = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
            {
                if (received.Length + count > MasterdataCallbackOptions.MaximumBodyBytes)
                { await Error(context, 413, "BODY_TOO_LARGE"); return; }
                received.Write(buffer, 0, count);
            }
            body = received.ToArray();
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) { await Error(context, 400, "INVALID_JSON"); return; }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413) { await Error(context, 413, "BODY_TOO_LARGE"); return; }
        catch (JsonException) { await Error(context, 400, "INVALID_JSON"); return; }
        catch (IOException) { await Error(context, 400, "BODY_READ_FAILED"); return; }
        var receiptId = Guid.NewGuid();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("FugaTES.MasterdataCallback");
        try
        {
            await store.PersistAsync(receiptId, DateTimeOffset.UtcNow, body, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch
        {
            logger.LogWarning(new EventId(158002), "Masterdata callback persistence unavailable.");
            await Error(context, 503, "RECEIPT_STORAGE_UNAVAILABLE");
            return;
        }
        logger.LogInformation(new EventId(158001), "Masterdata callback receipt committed. ReceiptId={ReceiptId}", receiptId);
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(new { receiptId, status = "RECEIVED" }, context.RequestAborted);
    }

    private static async Task Error(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = code }, context.RequestAborted);
    }
}
