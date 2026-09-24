using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Integracao.Guardian;

namespace FugaTES.Testes;

public sealed class Inc006GuardianHttpSet26Tests
{
    [Fact]
    public async Task Token_endpoint_recebe_post_com_credenciais_configuradas()
    {
        var handler = new HandlerFake(_ => Json(HttpStatusCode.OK, "{\"access_token\":\"TOKEN-1\"}"));
        var token = await TokenClient(handler).ObterTokenAsync(CancellationToken.None);

        Assert.Equal("TOKEN-1", token);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://guardian.invalid/api/v2/CreateToken/", request.Uri.AbsoluteUri);
        Assert.Equal("application/json", request.ContentType);
        var body = JsonNode.Parse(request.Body)!.AsObject();
        Assert.Equal("usuario-runtime", (string?)body["login"]);
        Assert.Equal("senha-runtime", (string?)body["secret"]);
    }

    [Fact]
    public async Task BaseUrl_ausente_falha_antes_do_http()
    {
        var handler = new HandlerFake(_ => throw new InvalidOperationException("HTTP must not run"));
        var options = Options() with { BaseUrl = null };

        await Assert.ThrowsAsync<GuardianHttpException>(() => TokenClient(handler, options).ObterTokenAsync(CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"access_token\":\"\"}")]
    [InlineData("not-json")]
    public async Task Token_vazio_ausente_ou_invalido_falha_fechado(string body)
    {
        var handler = new HandlerFake(_ => Json(HttpStatusCode.OK, body));
        await Assert.ThrowsAsync<GuardianHttpException>(() => TokenClient(handler).ObterTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Token_http_nao_success_falha_explicito()
    {
        var handler = new HandlerFake(_ => Json(HttpStatusCode.Unauthorized, "{}"));
        await Assert.ThrowsAsync<GuardianHttpException>(() => TokenClient(handler).ObterTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Token_timeout_falha_fechado_sem_expor_credenciais()
    {
        var handler = new BlockingHandler();
        var options = Options() with { Timeout = TimeSpan.FromMilliseconds(10) };

        var exception = await Assert.ThrowsAsync<GuardianHttpException>(() =>
            new GuardianTokenClient(new HttpClient(handler), options, new CredenciaisFake()).ObterTokenAsync(CancellationToken.None));

        Assert.Contains("timed out", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("usuario-runtime", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("senha-runtime", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public async Task Emissor_usa_post_rota_confirmada_bearer_e_tipo_documento_canonico(string tipoDocumento)
    {
        var handler = SuccessHandler();
        var client = HttpClient(handler);

        await client.EnviarAsync(new EmissorGuardianSet26("BP-1", "Emissor", "Emissor SA", "1", tipoDocumento), CancellationToken.None);

        var request = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://guardian.invalid/api/v0/Guardian/Emissor", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer", request.Authorization!.Scheme);
        Assert.Equal("TOKEN-1", request.Authorization.Parameter);
        Assert.Equal(tipoDocumento, (string?)JsonNode.Parse(request.Body)!["tipoDocumento"]);
    }

    [Fact]
    public async Task Transportadora_usa_endpoint_configurado_e_wire_set26()
    {
        var handler = SuccessHandler();
        await HttpClient(handler).EnviarAsync(new TransportadoraGuardianSet26("TR-1", "Transportadora", "Transportadora SA"), CancellationToken.None);

        var request = handler.Requests[1];
        Assert.Equal("https://guardian.invalid/api/set26/transportadora", request.Uri.AbsoluteUri);
        var body = JsonNode.Parse(request.Body)!.AsObject();
        Assert.Equal("TR-1", (string?)body["codigo"]);
        Assert.Equal("Transportadora", (string?)body["descricao"]);
        Assert.Equal("Transportadora SA", (string?)body["razaoSocial"]);
    }

    [Fact]
    public async Task PreCadastro_usa_payload_set26_sem_legados_e_retorna_protocolo()
    {
        var handler = SuccessHandler("{\"id\":12345,\"dataCadastro\":\"2026-09-24T11:38:28-03:00\"}");
        var resultado = await HttpClient(handler).EnviarAsync(PreCadastro(), CancellationToken.None);

        Assert.Equal("12345", resultado.Valor);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 11, 38, 28, TimeSpan.FromHours(-3)), resultado.DataCadastro);
        var request = handler.Requests[1];
        Assert.Equal("https://guardian.invalid/api/set26/pre-cadastro", request.Uri.AbsoluteUri);
        var dados = JsonNode.Parse(request.Body)!["dadosProtocoloPreCadastro"]!;
        Assert.Equal([1000, 1002, 1003], dados["camposAdicionais"]!.AsArray().Select(x => (int)x!["numero"]!));
        Assert.Equal("CONTRATO-001", (string?)dados["codigoContrato"]);
        Assert.DoesNotContain("Interno", request.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"id\":\"12345\",\"dataCadastro\":\"2026-09-24T11:38:28-03:00\"}")]
    [InlineData("{\"id\":12345}")]
    [InlineData("{\"id\":12345,\"dataCadastro\":\"2026-09-24T11:38:28\"}")]
    public async Task PreCadastro_resposta_incompleta_ou_ambigua_falha_fechado(string body)
    {
        var handler = SuccessHandler(body);
        await Assert.ThrowsAsync<GuardianHttpException>(() => HttpClient(handler).EnviarAsync(PreCadastro(), CancellationToken.None));
        Assert.Equal(1, handler.Requests.Count(x => x.Uri.AbsolutePath.EndsWith("pre-cadastro", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PreCadastro_id_canonico_independe_do_campo_legado_configurado()
    {
        var handler = SuccessHandler("{\"id\":12345,\"dataCadastro\":\"2026-09-24T11:38:28-03:00\"}");
        var client = HttpClient(handler, Options() with { PreCadastroProtocolResponseField = "protocol" });

        var result = await client.EnviarAsync(PreCadastro(), CancellationToken.None);

        Assert.Equal("12345", result.Valor);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PreCadastro_endpoint_ausente_falha_antes_do_http()
    {
        var handler = new HandlerFake(_ => throw new InvalidOperationException("HTTP must not run"));
        var client = HttpClient(handler, Options() with { PreCadastroEndpoint = null });

        await Assert.ThrowsAsync<GuardianHttpException>(() => client.EnviarAsync(PreCadastro(), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Operacao_http_nao_success_falha_explicito()
    {
        var handler = new HandlerFake(request => request.RequestUri!.AbsolutePath.EndsWith("CreateToken/", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, "{\"access_token\":\"TOKEN-1\"}")
            : Json(HttpStatusCode.BadRequest, "{}"));

        await Assert.ThrowsAsync<GuardianHttpException>(() => HttpClient(handler).EnviarAsync(new EmissorGuardianSet26("BP-1", "Emissor", "Emissor SA", "1", "1"), CancellationToken.None));
    }

    private static GuardianTokenClient TokenClient(HandlerFake handler, GuardianHttpOptions? options = null)
        => new(new HttpClient(handler), options ?? Options(), new CredenciaisFake());

    private static GuardianSet26HttpClient HttpClient(HandlerFake handler, GuardianHttpOptions? options = null)
    {
        var http = new HttpClient(handler);
        var configured = options ?? Options();
        return new(http, configured, new GuardianTokenClient(http, configured, new CredenciaisFake()));
    }

    private static HandlerFake SuccessHandler(string operationBody = "{}") => new(request =>
        request.RequestUri!.AbsolutePath.EndsWith("CreateToken/", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, "{\"access_token\":\"TOKEN-1\"}")
            : Json(HttpStatusCode.OK, operationBody));

    private static GuardianHttpOptions Options() => new()
    {
        BaseUrl = new UriBuilder("https", "guardian.invalid").Uri,
        Timeout = TimeSpan.FromSeconds(10),
        TransportadoraEndpoint = "/api/set26/transportadora",
        PreCadastroEndpoint = "/api/set26/pre-cadastro",
        TokenUserField = "login",
        TokenPasswordField = "secret",
        TokenResponseField = "access_token",
        PreCadastroProtocolResponseField = "id"
    };

    private static PreCadastroRecebimentoGuardianSet26 PreCadastro() => new(0, "FLX_RECEBIMENTO", "MAT-001", "BP-E", "BP-T", "CONTRATO-001", 1, "DOC-001", "https://callback.invalid/guardian", [new(1000, "COMPRA"), new(1002, "CENTRO"), new(1003, "ENTRADA")]);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class CredenciaisFake : ICredenciaisGuardianSet26Provider
    {
        public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken cancellationToken) => Task.FromResult(new CredenciaisGuardianSet26("usuario-runtime", "senha-runtime"));
    }

    private sealed class HandlerFake(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RequestCapturada> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri!, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken), request.Content?.Headers.ContentType?.MediaType, request.Headers.Authorization));
            return responder(request);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed record RequestCapturada(HttpMethod Method, Uri Uri, string Body, string? ContentType, AuthenticationHeaderValue? Authorization);
}
