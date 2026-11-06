using System.Text;
using System.Net;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;

namespace FugaTES.Integracao.SAP.Http;

// Executor HTTP OData reutilizavel para as capabilities SAP do MVP (SD/MM/ACM).
// Centraliza: montagem de URI com sap-client, GET, CSRF fetch, POST/PATCH fenced com cookies e If-Match/ETag.
// Nenhuma semantica funcional aqui: apenas transporte.
public sealed class SapODataHttpExecutor(
    HttpClient httpClient,
    OpcoesSap opcoes,
    IAutenticacaoSap autenticacao,
    CookieContainer cookieContainer)
{
    public sealed class ConfiguracaoInvalidaException(string message) : Exception(message);

    public Uri ConstruirUri(string path, string? extraQuery = null)
    {
        if (opcoes.BaseUrl is null || !opcoes.BaseUrl.IsAbsoluteUri
            || string.IsNullOrWhiteSpace(opcoes.SapClient) || opcoes.Timeout <= TimeSpan.Zero)
        {
            throw new ConfiguracaoInvalidaException("Configuracao SAP invalida (BaseUrl/SapClient/Timeout).");
        }

        var recurso = new Uri(opcoes.BaseUrl, path);
        var sb = recurso.AbsoluteUri;
        sb += (recurso.Query.Length == 0 ? "?" : "&") + "sap-client=" + Uri.EscapeDataString(opcoes.SapClient!);
        if (!string.IsNullOrWhiteSpace(extraQuery))
        {
            sb += "&" + extraQuery;
        }

        return new Uri(sb, UriKind.Absolute);
    }

    public async Task<SapHttpResposta> ObterAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(opcoes.Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        await autenticacao.AplicarAsync(request, timeout.Token).ConfigureAwait(false);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return new SapHttpResposta(response.StatusCode, body, LerETag(response));
    }

    // Bounded, cookie-free JSON GET for supplier admission; no CSRF/write or retry.
    public async Task<SapHttpResposta> ObterJsonLimitadoAsync(Uri uri,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(opcoes.Timeout);
        using var request=new HttpRequestMessage(HttpMethod.Get,uri);request.Headers.TryAddWithoutValidation("Accept","application/json");
        await autenticacao.AplicarAsync(request,timeout.Token);
        using var response=await httpClient.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        return new(response.StatusCode,await ReadBoundedAsync(response.Content,timeout.Token),LerETag(response));
    }
    // CSRF fetch: GET com X-CSRF-Token: Fetch; retorna o token e captura cookies de sessao.
    public async Task<string> ObterTokenCsrfAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(opcoes.Timeout);
        using var fetch = new HttpRequestMessage(HttpMethod.Get, uri);
        fetch.Headers.TryAddWithoutValidation("X-CSRF-Token", "Fetch");
        await autenticacao.AplicarAsync(fetch, timeout.Token).ConfigureAwait(false);
        using var response = await httpClient.SendAsync(fetch, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SapHttpStatusException("CSRF fetch falhou.", response.StatusCode);
        }

        if (!response.Headers.TryGetValues("X-CSRF-Token", out var tokens) || string.IsNullOrWhiteSpace(tokens.FirstOrDefault()))
        {
            throw new SapHttpStatusException("Token CSRF ausente.", response.StatusCode);
        }

        CapturarCookies(uri, response);
        return tokens.First();
    }

    public Task<SapHttpResposta> EnviarComCsrfAsync(HttpMethod metodo,Uri uri,HttpContent? conteudo,string tokenCsrf,string? ifMatch,CancellationToken ct)
        =>EnviarComCsrfCoreAsync(metodo,uri,conteudo,tokenCsrf,ifMatch,ct,"application/json");
    public Task<SapHttpResposta> EnviarAtomComCsrfAsync(HttpMethod metodo,Uri uri,HttpContent? conteudo,string tokenCsrf,string? ifMatch,CancellationToken ct)
        =>EnviarComCsrfCoreAsync(metodo,uri,conteudo,tokenCsrf,ifMatch,ct,"application/atom+xml");
    private async Task<SapHttpResposta> EnviarComCsrfCoreAsync(
        HttpMethod metodo,
        Uri uri,
        HttpContent? conteudo,
        string tokenCsrf,
        string? ifMatch,
        CancellationToken cancellationToken,string accept)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(opcoes.Timeout);
        using var request = new HttpRequestMessage(metodo, uri) { Content = conteudo };
        request.Headers.TryAddWithoutValidation("X-CSRF-Token", tokenCsrf);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        if (!string.IsNullOrWhiteSpace(ifMatch))
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        var cookies = cookieContainer.GetCookieHeader(uri);
        if (!string.IsNullOrWhiteSpace(cookies))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
        }

        await autenticacao.AplicarAsync(request, timeout.Token).ConfigureAwait(false);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        var body = accept=="application/atom+xml" ? await ReadBoundedAsync(response.Content,timeout.Token) : await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return new SapHttpResposta(response.StatusCode, body, LerETag(response));
    }

    /// <summary>
    /// POST de um $batch do OData V2. O boundary vai no Content-Type e TEM de ser o mesmo que
    /// montou o corpo - divergir produz um lote que o SAP aceita e ignora, respondendo 202 sem
    /// ter feito nada.
    ///
    /// Vai com CSRF e com o cookie de sessao, como qualquer escrita: o lote E uma escrita.
    /// </summary>
    public async Task<SapHttpResposta> EnviarLoteAsync(
        Uri uri, string corpoMultipart, string boundary, string tokenCsrf, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(opcoes.Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        using var conteudo = new ByteArrayContent(Encoding.UTF8.GetBytes(corpoMultipart));
        conteudo.Headers.TryAddWithoutValidation("Content-Type", "multipart/mixed; boundary=" + boundary);
        request.Content = conteudo;
        request.Headers.TryAddWithoutValidation("X-CSRF-Token", tokenCsrf);
        request.Headers.TryAddWithoutValidation("Accept", "multipart/mixed");

        var cookies = cookieContainer.GetCookieHeader(uri);
        if (!string.IsNullOrWhiteSpace(cookies))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
        }

        await autenticacao.AplicarAsync(request, timeout.Token).ConfigureAwait(false);
        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        var body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
        return new SapHttpResposta(response.StatusCode, body, LerETag(response));
    }

    public async Task<SapHttpResposta> ObterAtomAsync(Uri uri,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(opcoes.Timeout);
        using var request=new HttpRequestMessage(HttpMethod.Get,uri);
        request.Headers.TryAddWithoutValidation("Accept","application/atom+xml");
        var cookies=cookieContainer.GetCookieHeader(uri);if(cookies.Length>0)request.Headers.TryAddWithoutValidation("Cookie",cookies);
        await autenticacao.AplicarAsync(request,timeout.Token);
        using var response=await httpClient.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        var body=await ReadBoundedAsync(response.Content,timeout.Token);CapturarCookies(uri,response);
        return new(response.StatusCode,body,LerETag(response));
    }
    static async Task<string> ReadBoundedAsync(HttpContent content,CancellationToken ct)
    {
        if(content.Headers.ContentLength>1048576)throw new InvalidOperationException("SAP_RESPONSE_TOO_LARGE");
        await using var input=await content.ReadAsStreamAsync(ct);using var output=new MemoryStream();var buffer=new byte[8192];
        int n;while((n=await input.ReadAsync(buffer,ct))>0){if(output.Length+n>1048576)throw new InvalidOperationException("SAP_RESPONSE_TOO_LARGE");output.Write(buffer,0,n);}
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private void CapturarCookies(Uri uri, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        foreach (var value in values)
        {
            cookieContainer.SetCookies(uri, value);
        }
    }

    private static string? LerETag(HttpResponseMessage response)
    {
        var etag = response.Headers.ETag;
        if (etag is null)
        {
            return null;
        }

        return etag.IsWeak ? "W/" + etag.Tag : etag.Tag;
    }
}

public sealed record SapHttpResposta(HttpStatusCode Status, string Body, string? ETag);

public sealed class SapHttpStatusException(string message, HttpStatusCode statusCode) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
