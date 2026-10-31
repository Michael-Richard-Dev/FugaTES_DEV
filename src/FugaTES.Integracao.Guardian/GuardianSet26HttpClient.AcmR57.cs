using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Servicos.Mvp;
using Microsoft.Extensions.Logging;
namespace FugaTES.Integracao.Guardian;

public sealed record GuardianPreCadastroAcmR57RequestDto(
    [property:JsonPropertyName("configuracao")] GuardianPreCadastroConfiguracaoDto Configuracao,
    [property:JsonPropertyName("dadosProtocoloPreCadastro")] DadosPreCadastroAcmR57 DadosProtocoloPreCadastro);

public sealed record GuardianR57ExchangeDiagnostic(int HttpStatus, string? SafeBusinessMessage,
    string? SafeResponseBody, long? ProtocolId, string PayloadSha256);

public sealed partial class GuardianSet26HttpClient : IAcmPreCadastroMinimoGatewayR57
{
    private readonly ILogger<GuardianSet26HttpClient>? diagnosticLogger;
    public GuardianR57ExchangeDiagnostic? LastR57Diagnostic { get; private set; }

    public GuardianSet26HttpClient(HttpClient httpClient, GuardianHttpOptions options, GuardianTokenClient tokenClient,
        ILogger<GuardianSet26HttpClient> logger) : this(httpClient, options, tokenClient)
    {
        diagnosticLogger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static GuardianPreCadastroAcmR57RequestDto CriarPayloadR57(PreCadastroAcmR57 request)
    {
        AcmR57Mapping.Validar(request);
        return new(new(request.Callback),request.Dados);
    }

    public static byte[] SerializarPayloadR57(PreCadastroAcmR57 request)
        => JsonSerializer.SerializeToUtf8Bytes(CriarPayloadR57(request), JsonOptions);

    public async Task<ProtocoloAcmR57> EnviarMinimoAcmAsync(PreCadastroAcmR57 request,CancellationToken ct)
    {
        LastR57Diagnostic = null;
        const string route="/api/v1/Guardian/ProtocoloPreCadastro";
        if(options.PreCadastroEndpoint!=route || options.BaseUrl?.Scheme!="https")
            throw new GuardianHttpException("Guardian R57 REST route/profile is invalid.");
        var endpoint = options.ResolveEndpoint(route, nameof(options.PreCadastroEndpoint));
        // Serialize once: fingerprint and transmitted bytes must describe the same request.
        var payloadBytes = SerializarPayloadR57(request);
        var payloadFingerprint = Convert.ToHexString(SHA256.HashData(payloadBytes));
        var requestId = Guid.NewGuid().ToString("N");
        var correlationId = Activity.Current?.TraceId.ToHexString() ?? requestId;
        string token;
        try { token = await tokenClient.ObterTokenAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var authStatus = ex switch
            {
                GuardianHttpException legacy => legacy.HttpStatusCode ?? 0,
                HttpRequestException { StatusCode: { } code } => (int)code,
                _ => 0
            };
            // No protocol request has been dispatched. Retain neither auth content nor inner exception.
            throw new GuardianRestR57HttpException(authStatus,
                authStatus >= 300 ? $"GUARDIAN_R57_AUTH_HTTP_{authStatus}" : "GUARDIAN_R57_AUTH_PRE_SEND_FAILED", false);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);
        var status = 0;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(payloadBytes)
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // R57 owns status-aware handling; the shared legacy transport remains unchanged.
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    tokenClient.Invalidar(token);
                string? businessMessage = null;
                byte[]? body = null;
                try
                {
                    body = await ReadBoundedR57Async(response.Content, 8192, timeout.Token);
                    businessMessage = GuardianRestR57HttpException.ReadSafeBusinessMessage(body, token);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
                {
                    // The received HTTP status is authoritative even if its optional body cannot be read.
                }
                var safeCode = $"GUARDIAN_R57_HTTP_{status}";
                var guardianErrorCode = GuardianRestR57HttpException.ReadSafeErrorCode(body, token);
                var bodyFingerprint = body is null ? null : Convert.ToHexString(SHA256.HashData(body));
                var bodyKind = GuardianRestR57HttpException.BodyKind(body);
                var safeBody = GuardianRestR57HttpException.ReadSafeResponseBody(body, token);
                LastR57Diagnostic = new(status, businessMessage, safeBody, null, payloadFingerprint);
                var responseRequestId = new[] { "X-Request-ID", "X-Correlation-ID", "Request-ID", "traceparent" }
                    .SelectMany(name => response.Headers.TryGetValues(name, out var values) ? values : [])
                    .Select(value => GuardianRestR57HttpException.SafeResponseRequestId(value, token)).FirstOrDefault(value => value is not null);
                if (status == 400)
                {
                    try
                    {
                        diagnosticLogger?.LogWarning(new EventId(16557, "GuardianR57Rejected"),
                            "GuardianR57Rejected HttpStatusCode={HttpStatusCode} SafeCode={SafeCode} SafeBusinessMessage={SafeBusinessMessage} Reference={Reference} GuardianErrorCode={GuardianErrorCode} CorrelationId={CorrelationId} RequestId={RequestId} ResponseRequestId={ResponseRequestId} RequestPayloadFingerprint={RequestPayloadFingerprint} RequestMethod={RequestMethod} RequestUrl={RequestUrl} RequestContentType={RequestContentType} RequestAccept={RequestAccept} ResponseContentType={ResponseContentType} ResponseBodyKind={ResponseBodyKind} ResponseBodyFingerprint={ResponseBodyFingerprint} SafeResponseBody={SafeResponseBody} RequestTransferMode={RequestTransferMode} AuthenticationContext={AuthenticationContext} RequestHost={RequestHost}",
                            status, safeCode, businessMessage, request.Dados.NumeroReferencia, guardianErrorCode, correlationId,
                            requestId, responseRequestId, payloadFingerprint, "POST",
                            new UriBuilder(endpoint) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path),
                            "application/json", "*/*", response.Content.Headers.ContentType?.MediaType, bodyKind, bodyFingerprint,
                            safeBody, "FIXED_CONTENT_LENGTH", "GUARDIAN_HML_BACKEND", endpoint.IdnHost);
                    }
                    catch { /* Diagnostics must not change the known HTTP outcome or authorize a retry. */ }
                }
                throw new GuardianRestR57HttpException(status, safeCode,
                    status is < 400 or >= 500, businessMessage)
                {
                    GuardianErrorCode = guardianErrorCode, RequestId = requestId, CorrelationId = correlationId,
                    RequestPayloadFingerprint = payloadFingerprint, ResponseRequestId = responseRequestId,
                    SafeResponseBody = safeBody
                };
            }

            var successBody = await ReadBoundedR57Async(response.Content, 65536, timeout.Token);
            LastR57Diagnostic = new(status, null,
                GuardianRestR57HttpException.ReadSafeResponseBody(successBody, token), null, payloadFingerprint);
            if (successBody is null) throw InvalidR57Response(status);
            var protocol = ParseR57Response(successBody, status, request);
            LastR57Diagnostic = LastR57Diagnostic with { ProtocolId = protocol.Id };
            try
            {
                diagnosticLogger?.LogInformation(new EventId(16558, "GuardianR57Accepted"),
                    "GuardianR57Accepted HttpStatusCode={HttpStatusCode} ProtocolId={ProtocolId} Reference={Reference}",
                    status, protocol.Id, request.Dados.NumeroReferencia);
            }
            catch { /* A logging failure must not discard a validated protocol identity. */ }
            return protocol;
        }
        catch (OperationCanceledException)
        {
            LastR57Diagnostic = new(status, null, null, null, payloadFingerprint);
            throw new GuardianRestR57HttpException(status, "GUARDIAN_R57_TIMEOUT", true);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            LastR57Diagnostic = new(status, null, null, null, payloadFingerprint);
            throw new GuardianRestR57HttpException(status,
                status == 0 ? "GUARDIAN_R57_TRANSPORT_FAILURE" : "GUARDIAN_R57_RESPONSE_INVALID", true);
        }
    }

    private static async Task<byte[]?> ReadBoundedR57Async(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) return null;
        using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maximum + 1 - (int)buffer.Length)), ct);
            if (read == 0) return buffer.ToArray();
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maximum) return null;
        }
    }

    private static GuardianRestR57HttpException InvalidR57Response(int status) =>
        new(status, "GUARDIAN_R57_RESPONSE_INVALID", true);

    private static ProtocoloAcmR57 ParseR57Response(byte[] body, int status, PreCadastroAcmR57 request)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
                throw InvalidR57Response(status);
            string Text(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : "";
            if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
                !id.TryGetInt64(out var number) || number <= 0)
                throw InvalidR57Response(status);
            string? ticket = null;
            if (root.TryGetProperty("numeroTicket", out var ticketValue) && ticketValue.ValueKind != JsonValueKind.Null)
            {
                if (ticketValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(ticketValue.GetString()))
                    throw InvalidR57Response(status);
                ticket = ticketValue.GetString();
            }
            var result = new ProtocoloAcmR57(number, Text("codigoContrato"), Text("codigoItem"), Text("codigoEmissor"),
                Text("numeroReferencia"), ticket);
            if (!AcmR57Mapping.RespostaConfere(result, request)) throw InvalidR57Response(status);
            return result;
        }
        catch (JsonException) { throw InvalidR57Response(status); }
    }
}
