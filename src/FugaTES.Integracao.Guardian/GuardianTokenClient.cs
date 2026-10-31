using System.Net.Http.Json;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Recebimento;

namespace FugaTES.Integracao.Guardian;

public sealed class GuardianTokenClient
{
    private const string TokenEndpoint = "/api/v2/CreateToken";
    private readonly HttpClient httpClient;
    private readonly GuardianHttpOptions options;
    private readonly ICredenciaisGuardianSet26Provider credenciaisProvider;
    private readonly Func<DateTimeOffset> now;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private string? cachedToken;
    private DateTimeOffset expiresAt;

    public GuardianTokenClient(HttpClient httpClient, GuardianHttpOptions options, ICredenciaisGuardianSet26Provider credenciaisProvider)
        : this(httpClient, options, credenciaisProvider, () => DateTimeOffset.UtcNow) { }

    public GuardianTokenClient(HttpClient httpClient, GuardianHttpOptions options, ICredenciaisGuardianSet26Provider credenciaisProvider, Func<DateTimeOffset> now)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.credenciaisProvider = credenciaisProvider;
        this.now = now;
    }

    public async Task<string> ObterTokenAsync(CancellationToken cancellationToken)
    {
        if (options.TokenCacheLifetime < TimeSpan.Zero || options.TokenCacheLifetime > TimeSpan.FromMinutes(30))
            throw new GuardianHttpException("Guardian token cache lifetime is invalid.");
        if (options.TokenCacheLifetime == TimeSpan.Zero)
            return await RequestTokenAsync(cancellationToken);
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedToken is not null && now() < expiresAt)
                return cachedToken;
            var token = await RequestTokenAsync(cancellationToken);
            cachedToken = token;
            expiresAt = now().Add(options.TokenCacheLifetime);
            return token;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public void Invalidar(string token)
    {
        refreshLock.Wait();
        try
        {
            if (cachedToken == token)
                cachedToken = null;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<string> RequestTokenAsync(CancellationToken cancellationToken)
    {
        var endpoint = options.ResolveEndpoint(TokenEndpoint, nameof(TokenEndpoint));
        var credenciais = await credenciaisProvider.ObterAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(credenciais.Usuario) || string.IsNullOrWhiteSpace(credenciais.Senha) ||
            string.IsNullOrWhiteSpace(credenciais.Assinatura))
            throw new GuardianHttpException("Guardian runtime credentials are absent.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        int? httpStatus = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new Dictionary<string, string>
                {
                    ["email"] = credenciais.Usuario,
                    ["password"] = credenciais.Senha,
                    ["assinatura"] = credenciais.Assinatura
                })
            };
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            httpStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                throw new GuardianHttpException($"Guardian token request failed with HTTP {(int)response.StatusCode}.")
                    { HttpStatusCode = httpStatus };

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("token", out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(tokenElement.GetString()))
                throw new GuardianHttpException("Guardian token response is invalid.") { HttpStatusCode = httpStatus };

            return tokenElement.GetString()!;
        }
        catch (JsonException exception)
        {
            throw new GuardianHttpException("Guardian token response is invalid.", exception) { HttpStatusCode = httpStatus };
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GuardianHttpException("Guardian token request timed out.", exception) { HttpStatusCode = httpStatus };
        }
    }
}
