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
        var handler = new HandlerFake(_ => Json(HttpStatusCode.OK, "{\"token\":\"TOKEN-1\"}"));
        var token = await TokenClient(handler).ObterTokenAsync(CancellationToken.None);

        Assert.Equal("TOKEN-1", token);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://guardian.invalid/api/v2/CreateToken", request.Uri.AbsoluteUri);
        Assert.Equal("application/json", request.ContentType);
        var body = JsonNode.Parse(request.Body)!.AsObject();
        Assert.Equal("usuario-runtime", (string?)body["email"]);
        Assert.Equal("senha-runtime", (string?)body["password"]);
        Assert.Equal("assinatura-runtime", (string?)body["assinatura"]);
        Assert.False(body.ContainsKey("nomeApiToken"));
    }

    [Fact]
    public async Task Token_em_cache_e_reutilizado_ate_expirar()
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var handler = SuccessHandler();
        var client = new GuardianTokenClient(new HttpClient(handler), Options() with { TokenCacheLifetime = TimeSpan.FromMinutes(5) }, new CredenciaisFake(), () => now);

        Assert.Equal("TOKEN-1", await client.ObterTokenAsync(CancellationToken.None));
        Assert.Equal("TOKEN-1", await client.ObterTokenAsync(CancellationToken.None));
        Assert.Single(handler.Requests);
        now = now.AddMinutes(5);
        Assert.Equal("TOKEN-1", await client.ObterTokenAsync(CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Token_default_expira_em_30_minutos()
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var handler = SuccessHandler();
        var client = new GuardianTokenClient(new HttpClient(handler), Options(), new CredenciaisFake(), () => now);

        await client.ObterTokenAsync(CancellationToken.None);
        now = now.AddMinutes(29);
        await client.ObterTokenAsync(CancellationToken.None);
        Assert.Single(handler.Requests);
        now = now.AddMinutes(1);
        await client.ObterTokenAsync(CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Token_em_chamadas_concorrentes_faz_um_refresh()
    {
        var handler = SuccessHandler();
        var client = TokenClient(handler, Options() with { TokenCacheLifetime = TimeSpan.FromMinutes(5) });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.ObterTokenAsync(CancellationToken.None)));

        Assert.Single(handler.Requests);
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
    [InlineData("{\"token\":\"\"}")]
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
        Assert.Equal("https://guardian.invalid/api/v1/Guardian/ProtocoloPreCadastro", request.Uri.AbsoluteUri);
        var dados = JsonNode.Parse(request.Body)!["dadosProtocoloPreCadastro"]!;
        Assert.Equal("https://feedback.invalid/pre-cadastro", (string?)JsonNode.Parse(request.Body)!["configuracao"]!["urlParaRetorno"]);
        Assert.Null(dados["urlParaRetorno"]);
        Assert.Empty(dados["documentos"]!.AsArray());
        Assert.Equal([1000, 1002, 1003], dados["camposAdicionais"]!.AsArray().Select(x => (int)x!["numero"]!));
        Assert.Equal("CONTRATO-001", (string?)dados["codigoContrato"]);
        Assert.DoesNotContain("Interno", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreCadastro_captura_numeroTicket_quando_presente()
    {
        var handler = SuccessHandler("{\"id\":12345,\"dataCadastro\":\"2026-09-24T11:38:28-03:00\",\"numeroTicket\":\"67890\"}");

        var protocolo = await HttpClient(handler).EnviarAsync(PreCadastro(), CancellationToken.None);

        Assert.Equal("67890", protocolo.NumeroTicket);
    }

    [Fact]
    public async Task PreCadastro_serializa_documentos_tipo_veiculo_e_tipos_numerico_e_data()
    {
        var handler = SuccessHandler("{\"id\":12345,\"dataCadastro\":\"2026-09-24T11:38:28-03:00\"}");
        var request = PreCadastro() with
        {
            Documentos = [new Dictionary<string, string> { ["descricao"] = "DOC-1", ["pesoOrigem"] = "10.000" }],
            CodigoTipoVeiculo = "CAMINHAO",
            PrevisaoCargaContrato = 12.5m,
            DataAgendamento = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.FromHours(-3))
        };

        await HttpClient(handler).EnviarAsync(request, CancellationToken.None);

        var dados = JsonNode.Parse(handler.Requests[1].Body)!["dadosProtocoloPreCadastro"]!;
        Assert.Equal(0, (int)dados["id"]!);
        Assert.Equal("DOC-1", (string?)dados["documentos"]![0]!["descricao"]);
        Assert.Equal("CAMINHAO", (string?)dados["codigoTipoVeiculo"]);
        Assert.Equal(12.5m, (decimal)dados["previsaoCargaContrato"]!);
        Assert.Equal("2026-09-30T10:00:00-03:00", (string?)dados["dataAgendamento"]);
    }

    [Theory]
    [InlineData("FLX_EXPEDICAO")]
    [InlineData("FLX_EXPEDIÇÃO")]
    public async Task Fluxo_expedicao_pendente_falha_antes_do_http(string fluxo)
    {
        var handler = new HandlerFake(_ => throw new InvalidOperationException("HTTP must not run"));
        await Assert.ThrowsAsync<GuardianHttpException>(() => HttpClient(handler).EnviarAsync(PreCadastro() with { CodigoFluxo = fluxo }, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("{\"id\":12345,\"dataCadastro\":\"2026-09-24T11:38:28-03:00\",\"numeroTicket\":0}")]
    [InlineData("{\"id\":12345,\"dataCadastro\":\"2026-09-24T11:38:28-03:00\",\"numeroTicket\":\"\"}")]
    public async Task PreCadastro_numeroTicket_invalido_falha_fechado(string body)
    {
        var handler = SuccessHandler(body);
        await Assert.ThrowsAsync<GuardianHttpException>(() => HttpClient(handler).EnviarAsync(PreCadastro(), CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Operacao_auth_falha_invalida_token_sem_repetir_post(HttpStatusCode status)
    {
        var tokenCalls = 0;
        var operationCalls = 0;
        var handler = new HandlerFake(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("CreateToken", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, $"{{\"token\":\"TOKEN-{++tokenCalls}\"}}");
            operationCalls++;
            return Json(operationCalls == 1 ? status : HttpStatusCode.OK, "{}");
        });
        var client = HttpClient(handler, Options() with { TokenCacheLifetime = TimeSpan.FromMinutes(5) });

        await Assert.ThrowsAsync<GuardianHttpException>(() => client.EnviarAsync(new EmissorGuardianSet26("BP-1", "Emissor", "Emissor SA", "1", "1"), CancellationToken.None));
        Assert.Equal(1, operationCalls);
        await client.EnviarAsync(new EmissorGuardianSet26("BP-1", "Emissor", "Emissor SA", "1", "1"), CancellationToken.None);
        Assert.Equal(2, tokenCalls);
        Assert.Equal(2, operationCalls);
    }

    [Theory]
    [InlineData("{\"id\":\"12345\",\"dataCadastro\":\"2026-09-24T11:38:28-03:00\"}")]
    [InlineData("{\"id\":12345}")]
    [InlineData("{\"id\":12345,\"dataCadastro\":\"2026-09-24T11:38:28\"}")]
    public async Task PreCadastro_resposta_incompleta_ou_ambigua_falha_fechado(string body)
    {
        var handler = SuccessHandler(body);
        await Assert.ThrowsAsync<GuardianHttpException>(() => HttpClient(handler).EnviarAsync(PreCadastro(), CancellationToken.None));
        Assert.Equal(1, handler.Requests.Count(x => x.Uri.AbsolutePath.EndsWith("ProtocoloPreCadastro", StringComparison.Ordinal)));
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
        var handler = new HandlerFake(request => request.RequestUri!.AbsolutePath.EndsWith("CreateToken", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, "{\"token\":\"TOKEN-1\"}")
            : Json(HttpStatusCode.BadRequest, "{}"));

        await Assert.ThrowsAsync<GuardianHttpException>(() => HttpClient(handler).EnviarAsync(new EmissorGuardianSet26("BP-1", "Emissor", "Emissor SA", "1", "1"), CancellationToken.None));
    }

    private static GuardianTokenClient TokenClient(HandlerFake handler, GuardianHttpOptions? options = null)
        => new(new HttpClient(handler), options ?? Options(), new CredenciaisFake());

    [Fact]
    public async Task PreCadastro_referencia_maior_que_dez_rejeitada_antes_do_token_e_post()
    {
        var handler = SuccessHandler("{\"id\":12345,\"dataCadastro\":\"2026-09-24T11:38:28-03:00\"}");
        await Assert.ThrowsAsync<GuardianHttpException>(() => HttpClient(handler).EnviarAsync(
            PreCadastro() with { NumeroReferencia = "OC0000001031" }, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PreCadastro_acm_wire_preserva_estrutura_sem_deposito()
    {
        var handler = SuccessHandler("{\"id\":12345,\"dataCadastro\":\"2026-09-24T11:38:28-03:00\"}");
        var payload = PreCadastro() with { CodigoItem = "000000000007000000",
            NumeroReferencia = "OC1031", CamposAdicionais = [new(1000, "AC3P"), new(1002, "3011"), new(1003, "10")] };
        await HttpClient(handler).EnviarAsync(payload, default);
        var request = Assert.Single(handler.Requests, r => r.Uri.AbsolutePath.EndsWith("ProtocoloPreCadastro", StringComparison.Ordinal));
        using var document = System.Text.Json.JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal(2, root.EnumerateObject().Count());
        Assert.Equal(payload.UrlParaRetorno, root.GetProperty("configuracao").GetProperty("urlParaRetorno").GetString());
        var data = root.GetProperty("dadosProtocoloPreCadastro");
        Assert.Equal("FLX_RECEBIMENTO", data.GetProperty("codigoFluxo").GetString());
        Assert.Equal("OC1031", data.GetProperty("numeroReferencia").GetString());
        Assert.Equal(payload.CodigoItem, data.GetProperty("codigoItem").GetString());
        Assert.DoesNotContain("TG01", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("deposito", request.Body, StringComparison.OrdinalIgnoreCase);
        var fields = data.GetProperty("camposAdicionais").EnumerateArray().ToArray();
        Assert.Equal(new[] { 1000, 1002, 1003 }, fields.Select(f => f.GetProperty("numero").GetInt32()));
        Assert.Equal(new[] { "AC3P", "3011", "10" }, fields.Select(f => f.GetProperty("valor").GetString()));
    }

    private static GuardianSet26HttpClient HttpClient(HandlerFake handler, GuardianHttpOptions? options = null)
    {
        var http = new HttpClient(handler);
        var configured = options ?? Options();
        return new(http, configured, new GuardianTokenClient(http, configured, new CredenciaisFake()));
    }

    private static HandlerFake SuccessHandler(string operationBody = "{}") => new(request =>
        request.RequestUri!.AbsolutePath.EndsWith("CreateToken", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, "{\"token\":\"TOKEN-1\"}")
            : Json(HttpStatusCode.OK, operationBody));

    private static GuardianHttpOptions Options() => new()
    {
        BaseUrl = new UriBuilder("https", "guardian.invalid").Uri,
        Timeout = TimeSpan.FromSeconds(10),
        TransportadoraEndpoint = "/api/set26/transportadora",
        PreCadastroEndpoint = "/api/v1/Guardian/ProtocoloPreCadastro",
        PreCadastroProtocolResponseField = "id"
    };

    private static PreCadastroRecebimentoGuardianSet26 PreCadastro() => new(0, "FLX_RECEBIMENTO", "MAT-001", "BP-E", "BP-T", "CONTRATO-001", 1, "DOC-001", "https://feedback.invalid/pre-cadastro", [new(1000, "COMPRA"), new(1002, "CENTRO"), new(1003, "ENTRADA")]);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class CredenciaisFake : ICredenciaisGuardianSet26Provider
    {
        public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken cancellationToken) => Task.FromResult(new CredenciaisGuardianSet26("usuario-runtime", "senha-runtime", "assinatura-runtime"));
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
