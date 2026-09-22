using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;

namespace FugaTES.Integracao.Guardian;

public sealed class GuardianSet26HttpClient(HttpClient httpClient, GuardianHttpOptions options, GuardianTokenClient tokenClient)
    : IPreCadastroGuardianSet26Gateway, IEmissorGuardianSet26Gateway, ITransportadoraGuardianSet26Gateway
{
    private const string EmissorEndpoint = "/api/v0/Guardian/Emissor";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task EnviarAsync(EmissorGuardianSet26 emissor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(emissor);
        ValidateEmissor(emissor);
        using var response = await SendAsync(options.ResolveEndpoint(EmissorEndpoint, nameof(EmissorEndpoint)), emissor, cancellationToken);
    }

    public async Task EnviarAsync(TransportadoraGuardianSet26 transportadora, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transportadora);
        ValidateText(transportadora.Codigo, 20, false, "codigo");
        ValidateText(transportadora.Descricao, 50, true, "descricao");
        ValidateText(transportadora.RazaoSocial, 50, true, "razaoSocial");
        using var response = await SendAsync(options.ResolveEndpoint(options.TransportadoraEndpoint, nameof(options.TransportadoraEndpoint)), transportadora, cancellationToken);
    }

    public async Task<ProtocoloGuardianSet26> EnviarAsync(PreCadastroRecebimentoGuardianSet26 preCadastro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preCadastro);
        var endpoint = options.ResolveEndpoint(options.PreCadastroEndpoint, nameof(options.PreCadastroEndpoint));
        var responseField = GuardianHttpOptions.RequireField(options.PreCadastroProtocolResponseField, nameof(options.PreCadastroProtocolResponseField));
        var payload = new
        {
            configuracao = new { urlParaRetorno = preCadastro.UrlParaRetorno },
            dadosProtocoloPreCadastro = preCadastro
        };
        using var response = await SendAsync(endpoint, payload, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty(responseField, out var protocolElement) ||
                protocolElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(protocolElement.GetString()))
                throw new GuardianHttpException("Guardian pre-registration response is invalid.");
            return new ProtocoloGuardianSet26(protocolElement.GetString()!);
        }
        catch (JsonException exception)
        {
            throw new GuardianHttpException("Guardian pre-registration response is invalid.", exception);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri endpoint, object payload, CancellationToken cancellationToken)
    {
        var token = await tokenClient.ObterTokenAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (response.IsSuccessStatusCode)
                return response;
            response.Dispose();
            throw new GuardianHttpException($"Guardian operation failed with HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GuardianHttpException("Guardian operation timed out.", exception);
        }
    }

    private static void ValidateEmissor(EmissorGuardianSet26 emissor)
    {
        ValidateText(emissor.Codigo, 20, false, "codigo");
        ValidateText(emissor.Descricao, 50, true, "descricao");
        ValidateText(emissor.RazaoSocial, 50, true, "razaoSocial");
        if (emissor.Estado is not ("0" or "1") || emissor.TipoDocumento is not ("0" or "1"))
            throw new GuardianHttpException("Guardian emissor state or document type is invalid.");
    }

    private static void ValidateText(string? value, int maxLength, bool required, string field)
    {
        if ((required && string.IsNullOrWhiteSpace(value)) || value?.Length > maxLength)
            throw new GuardianHttpException($"Guardian field {field} is invalid.");
    }
}
