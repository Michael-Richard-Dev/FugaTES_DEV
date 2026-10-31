using System.Net.Http.Headers;
using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;

namespace FugaTES.Integracao.Guardian;

/// <summary>Resultado de UM envio. Guarda o que serve para auditar, nunca credencial.</summary>
public sealed record EnvioProtocoloSd(bool Sucesso, int HttpStatus, long? ProtocoloId,
    string ChaveNatural, string PayloadSha256, string? MensagemSegura);

public interface IEnviarProtocoloSdGateway
{
    Task<EnvioProtocoloSd> EnviarAsync(ProtocoloPreCadastroSdRequest pedido, string chaveNatural, CancellationToken ct);
}

/// <summary>
/// POST do protocolo de pre-cadastro do fluxo SD.
///
/// Separado do caminho do ACM de proposito: o ACM valida tres campos adicionais com valores
/// proprios e recusaria o payload de dez campos do SD. Afrouxar aquela validacao enfraqueceria um
/// caminho que hoje registra contrato em producao, entao o SD traz a sua.
/// </summary>
public sealed class GuardianProtocoloSdClient(
    HttpClient httpClient, GuardianHttpOptions options, GuardianTokenClient tokenClient)
    : IEnviarProtocoloSdGateway
{
    public const string Rota = "/api/v1/Guardian/ProtocoloPreCadastro";

    /// <summary>
    /// Impressao digital do pedido: SHA-256 dos bytes que serao transmitidos. Publica porque a
    /// reserva no banco acontece ANTES do envio e precisa gravar a mesma impressao - uma segunda
    /// definicao aqui faria a evidencia descrever um pedido diferente do que saiu.
    /// </summary>
    public static string Impressao(ProtocoloPreCadastroSdRequest pedido)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Bytes(pedido)));

    private static byte[] Bytes(ProtocoloPreCadastroSdRequest pedido)
        => System.Text.Encoding.UTF8.GetBytes(MapeadorProtocoloSd.Serializar(pedido));

    public async Task<EnvioProtocoloSd> EnviarAsync(
        ProtocoloPreCadastroSdRequest pedido, string chaveNatural, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        if (options.BaseUrl?.Scheme != "https")
            throw new GuardianHttpException("Guardian SD route requires HTTPS.");
        var endpoint = options.ResolveEndpoint(Rota, nameof(Rota));

        // Serializa UMA vez: a impressao digital e os bytes transmitidos tem de descrever o mesmo
        // pedido, senao a evidencia nao prova o que foi enviado.
        var bytes = Bytes(pedido);
        var digital = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

        var token = await tokenClient.ObterTokenAsync(ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        { Content = new ByteArrayContent(bytes) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token)
            .ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var corpo = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        return new EnvioProtocoloSd(response.IsSuccessStatusCode, status,
            LerProtocoloId(corpo), chaveNatural, digital, Mensagem(corpo, response.IsSuccessStatusCode));
    }

    /// <summary>
    /// Procura o id do protocolo na resposta, aceitando as formas que o Guardian ja usou. Exposto
    /// para teste: nao achar o id em um envio bem-sucedido deixaria a linha sem como ser rastreada.
    /// </summary>
    public static long? LerProtocoloId(string? corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return null;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            return Procurar(doc.RootElement, 0);
        }
        catch (JsonException) { return null; }

        static long? Procurar(JsonElement no, int profundidade)
        {
            if (profundidade > 4) return null;
            if (no.ValueKind == JsonValueKind.Number && no.TryGetInt64(out var direto)) return direto;
            if (no.ValueKind != JsonValueKind.Object) return null;
            foreach (var nome in new[] { "id", "protocolo", "idProtocolo", "numeroProtocolo" })
                if (no.TryGetProperty(nome, out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
                    if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var t)) return t;
                }
            foreach (var nome in new[] { "dadosProtocoloPreCadastro", "dados", "data", "result" })
                if (no.TryGetProperty(nome, out var filho) && Procurar(filho, profundidade + 1) is { } achado)
                    return achado;
            return null;
        }
    }

    // O corpo do Guardian pode trazer dado de negocio; em erro ele e a unica pista do motivo.
    // Limitado no tamanho para nao despejar uma pagina inteira na tela ou no arquivo de registro.
    private static string? Mensagem(string? corpo, bool sucesso)
    {
        if (sucesso || string.IsNullOrWhiteSpace(corpo)) return null;
        var limpo = corpo.Trim().ReplaceLineEndings(" ");
        return limpo.Length <= 400 ? limpo : limpo[..400] + "...";
    }
}
