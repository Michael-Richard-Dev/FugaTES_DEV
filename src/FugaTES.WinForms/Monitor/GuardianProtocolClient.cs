using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Principal;

namespace FugaTES.WinForms.Monitor;

public sealed record GuardianProtocolSnapshot
{
    public string? LocalSimulationDetails { get; init; }
    public Guid IntegrationId { get; init; }
    public string Contract { get; init; } = "";
    public int Version { get; init; }
    public string SnapshotFingerprint { get; init; } = "";
    public string? PayloadJson { get; init; }
    public string? PayloadSha256 { get; init; }
    public string? PreviewId { get; init; }
    public string Endpoint { get; init; } = "";
    public string Method { get; init; } = "";
    public string ContentType { get; init; } = "";
    public string Accept { get; init; } = "";
    public string TransferMode { get; init; } = "";
    public string AuthenticationContext { get; init; } = "";
    public bool CanSend { get; init; }
    public string? BlockCode { get; init; }
    public string State { get; init; } = "";
    public int? HttpStatus { get; init; }
    public string? SafeBusinessMessage { get; init; }
    public string? SafeResponseBody { get; init; }
    public long? ProtocolId { get; init; }
}

public interface IGuardianProtocolClient
{
    bool Configured { get; }
    bool PreviewAvailable { get; }
    string PreviewSource { get; }
    string LockReason { get; }
    Task<GuardianProtocolSnapshot> PreviewAsync(Guid integrationId, CancellationToken ct);
    Task<GuardianProtocolSnapshot> SendAsync(Guid integrationId, string payloadSha256, string previewId, bool confirm, CancellationToken ct);
}

public sealed class GuardianProtocolException(string code) : Exception(code);

public static class GuardianProtocolText
{
    public static string Safe(string? text, string? commandCredential = null)
    {
        if (text is null) return "";
        if (text.Length > 16384 || (!string.IsNullOrEmpty(commandCredential) && text.Contains(commandCredential, StringComparison.Ordinal))
            || Regex.IsMatch(text, @"(?i)authorization|bearer\s|basic\s|password|senha|token|secret|cookie|stacktrace|username=|host="))
            return "[CONTEUDO OMITIDO]";
        return text;
    }
}

public sealed class GuardianProtocolClient : IGuardianProtocolClient, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    private readonly Uri backend;
    private readonly string commandCredential;
    private readonly string operatorSid;
    public bool Configured => true;
    public bool PreviewAvailable => true;
    public string PreviewSource => "BACKEND_COMMAND";
    public string LockReason => "";

    public GuardianProtocolClient(GuardianProtocolProfile profile, string commandCredential, HttpMessageHandler handler,
        Func<string?>? getOperatorSid = null)
    {
        if (!profile.TryGetBackend(out var origin) || string.IsNullOrWhiteSpace(commandCredential)
            || commandCredential.Any(char.IsControl)) throw new GuardianProtocolException("BACKEND_COMMAND_CONFIG_REQUIRED");
        backend = origin!;
        operatorSid = (getOperatorSid ?? CurrentOperatorSid)() ?? "";
        if (operatorSid != profile.OperatorSid) throw new GuardianProtocolException("BACKEND_OPERATOR_SID_MISMATCH");
        this.commandCredential = commandCredential;
        http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(90), MaxResponseContentBufferSize = 1048576 };
    }

    public static HttpClientHandler CreateTransport() => new()
    {
        AllowAutoRedirect = false, UseDefaultCredentials = false, Credentials = null, UseCookies = false, UseProxy = false
    };

    internal static string? CurrentOperatorSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    }

    public Task<GuardianProtocolSnapshot> PreviewAsync(Guid integrationId, CancellationToken ct)
        => RequestAsync(integrationId, HttpMethod.Get, "preview", null, ct);

    public Task<GuardianProtocolSnapshot> SendAsync(Guid integrationId, string payloadSha256, string previewId, bool confirm, CancellationToken ct)
    {
        if (!confirm || !ValidHash(payloadSha256)) throw new GuardianProtocolException("BACKEND_CONFIRMATION_HASH_REQUIRED");
        if (!ValidHash(previewId)) throw new GuardianProtocolException("BACKEND_PREVIEW_ID_REQUIRED");
        return RequestAsync(integrationId, HttpMethod.Post, "send", new { payloadSha256, previewId, confirm = true }, ct);
    }

    internal static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    private async Task<GuardianProtocolSnapshot> RequestAsync(Guid id, HttpMethod method, string action, object? body, CancellationToken ct)
    {
        if (id == Guid.Empty) throw new GuardianProtocolException("BACKEND_IDENTITY_REQUIRED");
        using var request = new HttpRequestMessage(method, new Uri(backend, $"/api/v1/acm/guardian/protocolo/{id:D}/{action}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", commandCredential);
        request.Headers.Add("X-FugaTES-Operator-Sid", operatorSid);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400) throw new GuardianProtocolException("BACKEND_REDIRECT_BLOCKED");
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                throw new GuardianProtocolException("BACKEND_RESPONSE_INVALID");
            var result = JsonSerializer.Deserialize<GuardianProtocolSnapshot>(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), Json)
                ?? throw new GuardianProtocolException("BACKEND_RESPONSE_INVALID");
            if (result.IntegrationId != id) throw new GuardianProtocolException("BACKEND_IDENTITY_MISMATCH");
            if (result.PayloadJson?.Contains(commandCredential, StringComparison.Ordinal) == true)
                throw new GuardianProtocolException("BACKEND_PAYLOAD_UNSAFE");
            return result with
            {
                CanSend = response.IsSuccessStatusCode && result.CanSend,
                Endpoint = GuardianProtocolText.Safe(result.Endpoint, commandCredential),
                Method = GuardianProtocolText.Safe(result.Method, commandCredential),
                ContentType = GuardianProtocolText.Safe(result.ContentType, commandCredential),
                Accept = GuardianProtocolText.Safe(result.Accept, commandCredential),
                TransferMode = GuardianProtocolText.Safe(result.TransferMode, commandCredential),
                AuthenticationContext = GuardianProtocolText.Safe(result.AuthenticationContext, commandCredential),
                BlockCode = GuardianProtocolText.Safe(result.BlockCode, commandCredential),
                SafeBusinessMessage = GuardianProtocolText.Safe(result.SafeBusinessMessage, commandCredential),
                SafeResponseBody = GuardianProtocolText.Safe(result.SafeResponseBody, commandCredential)
            };
        }
        catch (GuardianProtocolException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            // Never surface transport details, raw response bodies or credentials.
            throw new GuardianProtocolException("BACKEND_COMMAND_UNAVAILABLE");
        }
    }

    public void Dispose() => http.Dispose();
}
