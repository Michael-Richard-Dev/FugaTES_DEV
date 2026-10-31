using System.Net;
using System.Text.Json;
using FugaTES.Aplicacao.Resultados.SAP;

namespace FugaTES.Integracao.SAP.Http;

// Base para adapters GET OData. Mapeia HTTP -> StatusConsultaSap e faz o parse tolerante (V4 e V2).
public abstract class SapConsultaAdapterBase(SapODataHttpExecutor executor)
{
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true };

    protected SapODataHttpExecutor Executor { get; } = executor;

    protected async Task<ResultadoConsultaSap<T>> ConsultarAsync<T>(
        string path,
        string? extraQuery,
        Func<JsonElement, T?> mapear,
        CancellationToken cancellationToken)
        where T : class
    {
        Uri uri;
        try
        {
            uri = Executor.ConstruirUri(path, extraQuery);
        }
        catch (SapODataHttpExecutor.ConfiguracaoInvalidaException)
        {
            return ResultadoConsultaSap<T>.Falha(StatusConsultaSap.FalhaConfiguracao);
        }

        SapHttpResposta resposta;
        try
        {
            resposta = await Executor.ObterAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ResultadoConsultaSap<T>.Falha(StatusConsultaSap.FalhaTecnica);
        }
        catch (HttpRequestException)
        {
            return ResultadoConsultaSap<T>.Falha(StatusConsultaSap.FalhaTecnica);
        }

        return resposta.Status switch
        {
            HttpStatusCode.OK => Parse(resposta.Body, mapear),
            HttpStatusCode.NotFound => ResultadoConsultaSap<T>.Falha(StatusConsultaSap.NaoEncontrado),
            HttpStatusCode.Unauthorized => ResultadoConsultaSap<T>.Falha(StatusConsultaSap.FalhaAutenticacao),
            HttpStatusCode.Forbidden => ResultadoConsultaSap<T>.Falha(StatusConsultaSap.FalhaAutorizacao),
            HttpStatusCode.BadRequest => ResultadoConsultaSap<T>.Falha(StatusConsultaSap.FalhaContrato),
            >= HttpStatusCode.InternalServerError => ResultadoConsultaSap<T>.Falha(StatusConsultaSap.FalhaTecnica),
            _ => ResultadoConsultaSap<T>.Falha(StatusConsultaSap.FalhaTecnica)
        };
    }

    private static ResultadoConsultaSap<T> Parse<T>(string body, Func<JsonElement, T?> mapear)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return ResultadoConsultaSap<T>.Falha(StatusConsultaSap.FalhaContrato);
        }

        try
        {
            using var doc = JsonDocument.Parse(body, JsonOptions);
            var entidade = RaizEntidade(doc.RootElement);
            var mapeado = mapear(entidade);
            return mapeado is null
                ? ResultadoConsultaSap<T>.Falha(StatusConsultaSap.FalhaContrato)
                : ResultadoConsultaSap<T>.Sucesso(mapeado);
        }
        catch (JsonException)
        {
            return ResultadoConsultaSap<T>.Falha(StatusConsultaSap.FalhaContrato);
        }
    }

    // Single-entity queries must not silently select from an ambiguous collection.
    internal static JsonElement RaizEntidade(JsonElement root)
    {
        var entidade = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("d", out var d) ? d : root;
        if (entidade.ValueKind != JsonValueKind.Object) throw new JsonException("SAP entity is not an object.");
        if (entidade.TryGetProperty("__next", out _) || entidade.TryGetProperty("@odata.nextLink", out _))
            throw new JsonException("SAP single-entity response is incomplete.");
        if (entidade.TryGetProperty("results", out var results) || entidade.TryGetProperty("value", out results))
        {
            if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != 1 ||
                results[0].ValueKind != JsonValueKind.Object)
                throw new JsonException("SAP query requires exactly one entity.");
            return results[0];
        }
        return entidade;
    }

    /// <summary>
    /// Consulta de COLECAO. Separada de <c>ConsultarAsync</c> de proposito: aquela exige
    /// exatamente uma entidade e trata colecao como erro de contrato, o que e a garantia certa
    /// para busca por chave e a errada para varredura.
    /// </summary>
    protected async Task<ResultadoConsultaSap<IReadOnlyList<T>>> ConsultarColecaoAsync<T>(
        string path, string? extraQuery, Func<JsonElement, IEnumerable<T>> mapear,
        CancellationToken cancellationToken)
        where T : class
    {
        Uri uri;
        try { uri = Executor.ConstruirUri(path, extraQuery); }
        catch (SapODataHttpExecutor.ConfiguracaoInvalidaException)
        { return ResultadoConsultaSap<IReadOnlyList<T>>.Falha(StatusConsultaSap.FalhaConfiguracao); }

        SapHttpResposta resposta;
        try { resposta = await Executor.ObterAsync(uri, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ResultadoConsultaSap<IReadOnlyList<T>>.Falha(StatusConsultaSap.FalhaTecnica); }
        catch (HttpRequestException) { return ResultadoConsultaSap<IReadOnlyList<T>>.Falha(StatusConsultaSap.FalhaTecnica); }

        if (resposta.Status != HttpStatusCode.OK)
            return ResultadoConsultaSap<IReadOnlyList<T>>.Falha(resposta.Status switch
            {
                HttpStatusCode.NotFound => StatusConsultaSap.NaoEncontrado,
                HttpStatusCode.Unauthorized => StatusConsultaSap.FalhaAutenticacao,
                HttpStatusCode.Forbidden => StatusConsultaSap.FalhaAutorizacao,
                HttpStatusCode.BadRequest => StatusConsultaSap.FalhaContrato,
                _ => StatusConsultaSap.FalhaTecnica
            });

        if (string.IsNullOrWhiteSpace(resposta.Body))
            return ResultadoConsultaSap<IReadOnlyList<T>>.Falha(StatusConsultaSap.FalhaContrato);
        try
        {
            using var doc = JsonDocument.Parse(resposta.Body, JsonOptions);
            var itens = mapear(RaizColecao(doc.RootElement)).ToArray();
            return ResultadoConsultaSap<IReadOnlyList<T>>.Sucesso(itens);
        }
        catch (JsonException)
        { return ResultadoConsultaSap<IReadOnlyList<T>>.Falha(StatusConsultaSap.FalhaContrato); }
    }

    /// <summary>
    /// Devolve o array de resultados, aceitando V2 (<c>d.results</c>) e V4 (<c>value</c>).
    /// Resposta paginada e recusada: entregar so a primeira pagina como se fosse o conjunto
    /// completo faria a varredura ignorar ordens em silencio.
    /// </summary>
    public static JsonElement RaizColecao(JsonElement root)
    {
        var corpo = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("d", out var d) ? d : root;
        if (corpo.ValueKind == JsonValueKind.Array) return corpo;
        if (corpo.ValueKind != JsonValueKind.Object) throw new JsonException("SAP collection is not an object or array.");
        if (corpo.TryGetProperty("__next", out _) || corpo.TryGetProperty("@odata.nextLink", out _))
            throw new JsonException("SAP collection response is paginated; raise $top or page explicitly.");
        if (!corpo.TryGetProperty("results", out var results) && !corpo.TryGetProperty("value", out results))
            throw new JsonException("SAP collection response has no results array.");
        if (results.ValueKind != JsonValueKind.Array) throw new JsonException("SAP collection results is not an array.");
        return results;
    }

    internal static string? Texto(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static int? Inteiro(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
            _ => null
        };
    }

    internal static decimal? Decimal(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDecimal(out var n) => n,
            // SAP costuma serializar quantidades/decimais como string.
            JsonValueKind.String when decimal.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var n) => n,
            _ => null
        };
    }

    // Navegacao 0..* tolerante: aceita array V4 direto ou objeto V2 { "results": [...] }.
    internal static IEnumerable<JsonElement> Colecao(JsonElement parent, string navegacao)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(navegacao, out var nav))
        {
            yield break;
        }

        if (nav.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in nav.EnumerateArray())
            {
                yield return item;
            }
        }
        else if (nav.ValueKind == JsonValueKind.Object && nav.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                yield return item;
            }
        }
    }
}
