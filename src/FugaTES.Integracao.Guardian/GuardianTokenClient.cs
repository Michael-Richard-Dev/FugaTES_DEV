using System.Net.Http.Json;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Recebimento;

namespace FugaTES.Integracao.Guardian;

public sealed class GuardianTokenClient(HttpClient httpClient, GuardianHttpOptions options, ICredenciaisGuardianSet26Provider credenciaisProvider)
{
    private const string TokenEndpoint = "/api/v2/CreateToken/";

    public async Task<string> ObterTokenAsync(CancellationToken cancellationToken)
    {
        var endpoint = options.ResolveEndpoint(TokenEndpoint, nameof(TokenEndpoint));
        var userField = GuardianHttpOptions.RequireField(options.TokenUserField, nameof(options.TokenUserField));
        var passwordField = GuardianHttpOptions.RequireField(options.TokenPasswordField, nameof(options.TokenPasswordField));
        var responseField = GuardianHttpOptions.RequireField(options.TokenResponseField, nameof(options.TokenResponseField));
        var credenciais = await credenciaisProvider.ObterAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(credenciais.Usuario) || string.IsNullOrWhiteSpace(credenciais.Senha))
            throw new GuardianHttpException("Guardian runtime credentials are absent.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new Dictionary<string, string>
                {
                    [userField] = credenciais.Usuario,
                    [passwordField] = credenciais.Senha
                })
            };
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new GuardianHttpException($"Guardian token request failed with HTTP {(int)response.StatusCode}.");

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            if (!document.RootElement.TryGetProperty(responseField, out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(tokenElement.GetString()))
                throw new GuardianHttpException("Guardian token response is invalid.");

            return tokenElement.GetString()!;
        }
        catch (JsonException exception)
        {
            throw new GuardianHttpException("Guardian token response is invalid.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GuardianHttpException("Guardian token request timed out.", exception);
        }
    }
}
