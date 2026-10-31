using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;

namespace FugaTES.Integracao.Guardian;

public sealed partial class GuardianSet26HttpClient(HttpClient httpClient, GuardianHttpOptions options, GuardianTokenClient tokenClient)
    : IPreCadastroGuardianSet26Gateway, IEmissorGuardianSet26Gateway, ITransportadoraGuardianSet26Gateway, IItemGuardianSet26Gateway
{
    private const string EmissorEndpoint = "/api/v0/Guardian/Emissor";
    private const string ItemEndpoint = "/api/v0/Guardian/Item";
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

    // Guardian Item: POST /api/v0/Guardian/Item. Valida apenas a PRESENCA do minimum wire comprovado
    // (codigo, tipo, nome); estado e opcional. Nenhuma regra de negocio sobre o VALOR de tipo ou
    // fatorConversao e aplicada aqui: ambos seguem verbatim para o wire.
    public async Task EnviarAsync(ItemGuardianSet26 item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Codigo) || string.IsNullOrWhiteSpace(item.Tipo) || string.IsNullOrWhiteSpace(item.Nome))
            throw new GuardianHttpException("Guardian item request is incomplete.");
        using var response = await SendAsync(options.ResolveEndpoint(ItemEndpoint, nameof(ItemEndpoint)), item, cancellationToken);
    }

    public async Task<ProtocoloGuardianSet26> EnviarAsync(PreCadastroRecebimentoGuardianSet26 preCadastro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preCadastro);
        if (preCadastro.NumeroReferencia is { Length: > 10 })
            throw new GuardianHttpException("Guardian pre-registration reference exceeds ten characters.");
        if (preCadastro.Id != 0 || preCadastro.CodigoFluxo != "FLX_RECEBIMENTO" ||
            new[] { preCadastro.CodigoItem, preCadastro.CodigoEmissor,
                preCadastro.CodigoContrato, preCadastro.NumeroReferencia, preCadastro.UrlParaRetorno }.Any(string.IsNullOrWhiteSpace) ||
            preCadastro.IdAgenda <= 0 || preCadastro.CamposAdicionais is null)
            throw new GuardianHttpException("Guardian pre-registration request is invalid.");
        ValidatePreCadastroText(preCadastro.CodigoFluxo, 50, false, "codigoFluxo");
        ValidatePreCadastroText(preCadastro.CodigoItem, 30, false, "codigoItem");
        ValidatePreCadastroText(preCadastro.CodigoEmissor, 20, false, "codigoEmissor");
        // The approved ACM reception flow may leave the carrier to the Guardian agent.
        // Null is omitted by the DTO. A supplied carrier is still validated verbatim.
        ValidatePreCadastroText(preCadastro.CodigoTransportadora, 20, true, "codigoTransportadora");
        ValidatePreCadastroText(preCadastro.CodigoContrato, 20, false, "codigoContrato");
        ValidatePreCadastroText(preCadastro.NumeroReferencia, 10, false, "numeroReferencia");
        ValidatePreCadastroText(preCadastro.UrlParaRetorno, int.MaxValue, false, "urlParaRetorno");
        if (!Uri.TryCreate(preCadastro.UrlParaRetorno, UriKind.Absolute, out var callback) ||
            callback.Scheme is not ("http" or "https"))
            throw new GuardianHttpException("Guardian pre-registration callback is invalid.");
        ValidatePreCadastroText(preCadastro.PlacaVeiculo, 20, true, "placaVeiculo");
        ValidatePreCadastroText(preCadastro.PlacaCarreta, 20, true, "placaCarreta");
        ValidatePreCadastroText(preCadastro.Identificador, 24, true, "identificador");
        ValidatePreCadastroText(preCadastro.CodigoMotorista, 20, true, "codigoMotorista");
        ValidatePreCadastroText(preCadastro.CodigoVeiculo, 20, true, "codigoVeiculo");
        ValidatePreCadastroText(preCadastro.CodigoTipoVeiculo, 20, true, "codigoTipoVeiculo");
        var numbers = new HashSet<int>();
        foreach (var field in preCadastro.CamposAdicionais)
        {
            if (field is null || !numbers.Add(field.Numero))
                throw new GuardianHttpException("Guardian pre-registration additional fields are ambiguous.");
            ValidatePreCadastroText(field.Valor, 50, false, "camposAdicionais.valor");
        }
        if (!new[] { 1000, 1002, 1003 }.All(numbers.Contains))
            throw new GuardianHttpException("Guardian pre-registration additional fields are incomplete.");
        if (preCadastro.TransportadoraPreenchidaNoGuardian)
        {
            if (preCadastro.CodigoTransportadora is not null ||
                !preCadastro.CamposAdicionais.Any(c => c.Numero == 1000 && c.Valor == "AC3P") ||
                !preCadastro.CamposAdicionais.Any(c => c.Numero == 1002 && c.Valor == "3011") ||
                !preCadastro.CamposAdicionais.Any(c => c.Numero == 1003 && c.Valor == "10"))
                throw new GuardianHttpException("Guardian carrier completion context is not approved.");
        }
        else if (preCadastro.CodigoTransportadora is null)
            throw new GuardianHttpException("Guardian carrier completion decision is required.");
        if (preCadastro.Documentos is null)
            throw new GuardianHttpException("Guardian pre-registration documents are invalid.");
        foreach (var document in preCadastro.Documentos)
        {
            if (document is null || document.Count != 2 ||
                document.Keys.Any(key => key is not ("descricao" or "pesoOrigem")) ||
                !document.TryGetValue("descricao", out var description) ||
                !document.TryGetValue("pesoOrigem", out var originWeight))
                throw new GuardianHttpException("Guardian pre-registration document schema is invalid.");
            ValidatePreCadastroText(description, 40, false, "documentos.descricao");
            ValidatePreCadastroText(originWeight, 13, false, "documentos.pesoOrigem");
        }
        var endpoint = options.ResolveEndpoint(options.PreCadastroEndpoint, nameof(options.PreCadastroEndpoint));
        var payload = new GuardianPreCadastroRequestDto(
            new GuardianPreCadastroConfiguracaoDto(preCadastro.UrlParaRetorno), preCadastro);
        using var response = await SendAsync(endpoint, payload, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("id", out var protocolElement) ||
                protocolElement.ValueKind != JsonValueKind.Number ||
                !document.RootElement.TryGetProperty("dataCadastro", out var createdElement) ||
                createdElement.ValueKind != JsonValueKind.String ||
                !Regex.IsMatch(createdElement.GetString()!, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant))
                throw new GuardianHttpException("Guardian pre-registration response is invalid.");
            var dto = document.RootElement.Deserialize<GuardianPreCadastroResponseDto>(JsonOptions);
            if (dto is null || dto.Id <= 0 || dto.DataCadastro is null)
                throw new GuardianHttpException("Guardian pre-registration response is invalid.");
            if (document.RootElement.TryGetProperty("numeroTicket", out var ticketElement))
            {
                if (ticketElement.ValueKind != JsonValueKind.Null &&
                    (ticketElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(ticketElement.GetString())))
                    throw new GuardianHttpException("Guardian pre-registration ticket is invalid.");
            }
            return new ProtocoloGuardianSet26(dto.Id.ToString(CultureInfo.InvariantCulture), dto.DataCadastro, dto.NumeroTicket);
        }
        catch (JsonException exception)
        {
            throw new GuardianHttpException("Guardian pre-registration response is invalid.", exception);
        }
    }

    private static void ValidatePreCadastroText(string? value, int maximumLength, bool optional, string field)
    {
        if (optional && value is null) return;
        var marker = value?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            string.Equals(marker, "NÃO VALIDADO", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(marker, "NAO VALIDADO", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(marker, "PENDENTE", StringComparison.OrdinalIgnoreCase) ||
            (marker!.StartsWith('[') && marker.EndsWith(']')))
            throw new GuardianHttpException($"Guardian pre-registration field {field} is invalid or unverified.");
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
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                tokenClient.Invalidar(token);
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
