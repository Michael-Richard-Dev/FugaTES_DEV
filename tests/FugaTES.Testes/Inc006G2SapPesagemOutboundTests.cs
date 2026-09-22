using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Dtos.Pesagem;
using FugaTES.Integracao.SAP.Http;

namespace FugaTES.Testes;

public sealed class Inc006G2SapPesagemOutboundTests
{
    [Fact]
    public async Task CsrfGetUsaHeaderFetch()
    {
        var handler = SuccessHandler();
        await Gateway(handler).EnviarAsync(Fixture(), CancellationToken.None);
        Assert.Equal("Fetch", handler.Requests[0].Header("X-CSRF-Token"));
    }

    [Fact]
    public async Task TokenRetornadoEhUsadoNoPost()
    {
        var handler = SuccessHandler();
        await Gateway(handler).EnviarAsync(Fixture(), CancellationToken.None);
        Assert.Equal("csrf-001", handler.Requests[1].Header("X-CSRF-Token"));
    }

    [Fact]
    public async Task CookieDoGetEhUsadoNoPost()
    {
        var handler = SuccessHandler();
        await Gateway(handler).EnviarAsync(Fixture(), CancellationToken.None);
        Assert.Contains("SAP_SESSION=abc123", handler.Requests[1].Header("Cookie"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostEhExecutadoNoMaximoUmaVez()
    {
        var handler = SuccessHandler();
        await Gateway(handler).EnviarAsync(Fixture(), CancellationToken.None);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ContratoAcmAusenteBloqueiaAntesDoHttp()
    {
        var handler = SuccessHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => Gateway(handler).EnviarAsync(Fixture() with { ContratoACM = "" }, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TipoOperacaoEhPreservadoNoWire()
    {
        var handler = SuccessHandler();
        await Gateway(handler).EnviarAsync(Fixture(tipoOperacao: 50), CancellationToken.None);
        Assert.Equal(50, PostJson(handler)["TipoOperacao"]!.GetValue<int>());
    }

    [Fact]
    public async Task PedidoComprasNuloPermaneceOpcional()
    {
        var handler = SuccessHandler();
        await Gateway(handler).EnviarAsync(Fixture(pedidoCompras: null), CancellationToken.None);
        Assert.Null(PostJson(handler)["to_Pesagem"]![0]!["PedidoCompras"]);
    }

    [Fact]
    public async Task ContratoAcmUsaSomenteValorExplicitoDoMvp()
    {
        var handler = SuccessHandler();
        await Gateway(handler).EnviarAsync(Fixture(contratoAcm: "ACM-WIRE"), CancellationToken.None);
        Assert.Equal("ACM-WIRE", PostJson(handler)["to_Pesagem"]![0]!["ContratoACM"]!.GetValue<string>());
    }

    [Fact]
    public async Task OperacaoContratoNaoParticipaDoTipoOperacao()
    {
        var handler = SuccessHandler();
        await Gateway(handler).EnviarAsync(Fixture(tipoOperacao: 10), CancellationToken.None);
        Assert.Equal(10, PostJson(handler)["TipoOperacao"]!.GetValue<int>());
    }

    [Fact]
    public async Task NrProtocoloNaoEhDerivadoDoDocumentoOuProtocoloGuardian()
    {
        var handler = SuccessHandler();
        await Gateway(handler).EnviarAsync(Fixture(), CancellationToken.None);
        Assert.Null(PostJson(handler)["to_Pesagem"]![0]!["NrProtocolo"]);
    }

    [Theory]
    [InlineData("COMPRA_PJ", 1, "AC3P")]
    [InlineData("COMPRA_PF", 10, "AC3P")]
    [InlineData("VENDA_PJ", 40, "VENDA")]
    [InlineData("VENDA_PF", 40, "VENDA")]
    [InlineData("CONTA_ORDEM", 50, "VENDA")]
    [InlineData("ENTREGA_FUTURA", 50, "VENDA")]
    public async Task SeisFixturesSerializam(string id, short tipoOperacao, string tipoDocumento)
    {
        var handler = SuccessHandler();
        await Gateway(handler).EnviarAsync(Fixture(id, tipoOperacao, tipoDocumento, null, $"ACM-{id}"), CancellationToken.None);
        var json = PostJson(handler);
        Assert.Equal(tipoOperacao, json["TipoOperacao"]!.GetValue<int>());
        Assert.Equal(tipoDocumento, json["to_Pesagem"]![0]!["TipoDocumento"]!.GetValue<string>());
        Assert.Equal($"ACM-{id}", json["to_Pesagem"]![0]!["ContratoACM"]!.GetValue<string>());
    }

    [Fact]
    public async Task JsonWireUsaNomesExatos()
    {
        var handler = SuccessHandler();
        await Gateway(handler).EnviarAsync(Fixture(), CancellationToken.None);
        var json = PostJson(handler);
        Assert.NotNull(json["SistemaBalanca"]);
        Assert.NotNull(json["to_QualidPes"]);
        Assert.NotNull(json["to_Pesagem"]);
        Assert.True(json["to_Pesagem"]![0]!.AsObject().ContainsKey("PercentualDesdobro"));
        Assert.Null(json["ToPesagem"]);
    }

    [Fact]
    public void QualidadeSuportaValorRealEZero()
    {
        var dto = new QualidadePesagemSapWireDto("UMIDADE", "Q1", 12.5m);
        var zero = new QualidadePesagemSapWireDto("IMPUREZA", "Q2", 0m);
        var json = JsonSerializer.SerializeToNode(new[] { dto, zero })!.AsArray();
        Assert.Equal(12.5m, json[0]!["ValorAferido"]!.GetValue<decimal>());
        Assert.Equal(0m, json[1]!["ValorAferido"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task ErroNoGetCsrfBloqueiaPost()
    {
        var handler = new RecordingHandler((_, call) => call == 1 ? new(HttpStatusCode.BadGateway) : new(HttpStatusCode.OK));
        await Assert.ThrowsAsync<PesagemSapHttpException>(() => Gateway(handler).EnviarAsync(Fixture(), CancellationToken.None));
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task TokenCsrfAusenteBloqueiaPost()
    {
        var handler = new RecordingHandler((_, _) => new(HttpStatusCode.OK));
        await Assert.ThrowsAsync<PesagemSapHttpException>(() => Gateway(handler).EnviarAsync(Fixture(), CancellationToken.None));
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task PostHttpSuccessEhCapturado()
    {
        var handler = SuccessHandler();
        await Gateway(handler).EnviarAsync(Fixture(), CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, handler.Responses.Last().StatusCode);
    }

    [Fact]
    public async Task PostHttpErrorEhCapturadoSemRetry()
    {
        var handler = HandlerWithPost(new(HttpStatusCode.UnprocessableEntity));
        var error = await Assert.ThrowsAsync<PesagemSapHttpException>(() => Gateway(handler).EnviarAsync(Fixture(), CancellationToken.None));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, error.StatusCode);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ExceptionNoPostNaoGeraBlindRetry()
    {
        var handler = HandlerWithPostException(new TaskCanceledException("synthetic timeout"));
        await Assert.ThrowsAsync<TaskCanceledException>(() => Gateway(handler).EnviarAsync(Fixture(), CancellationToken.None));
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    private static PesagemSapHttpGateway Gateway(RecordingHandler handler) =>
        new(new HttpClient(handler), new OpcoesSap { BaseUrl = new("https://sap.invalid/"), SapClient = "110", Timeout = TimeSpan.FromSeconds(5) }, new NoAuth(), new CookieContainer());

    private static RecordingHandler SuccessHandler() => HandlerWithPost(new(HttpStatusCode.Created));

    private static RecordingHandler HandlerWithPost(HttpResponseMessage post) =>
        new((_, call) => call == 1 ? CsrfResponse() : post);

    private static RecordingHandler HandlerWithPostException(Exception exception) =>
        new((_, call) => call == 1 ? CsrfResponse() : throw exception);

    private static HttpResponseMessage CsrfResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("X-CSRF-Token", "csrf-001");
        response.Headers.Add("Set-Cookie", "SAP_SESSION=abc123; Path=/; HttpOnly");
        return response;
    }

    private static JsonObject PostJson(RecordingHandler handler) =>
        JsonNode.Parse(handler.Requests.Single(request => request.Method == HttpMethod.Post).Body!)!.AsObject();

    private static PesagemSapMvp Fixture(
        string id = "COMPRA_PJ",
        short tipoOperacao = 1,
        string tipoDocumento = "AC3P",
        string? pedidoCompras = null,
        string contratoAcm = "ACM-001") =>
        new("Toledo", "3011", "E", id, 123, "TRANSP-1", "MAT-1", "PARCEIRO-1", 1200m, 200m, 1000m, tipoDocumento, pedidoCompras, "DOC-1", tipoOperacao, contratoAcm);

    private sealed class NoAuth : IAutenticacaoSap
    {
        public Task AplicarAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        public List<HttpResponseMessage> Responses { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await CapturedRequest.CreateAsync(request, cancellationToken));
            var response = responder(request, Requests.Count);
            Responses.Add(response);
            return response;
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body)
    {
        public string Header(string name) => Headers.GetValueOrDefault(name) ?? string.Empty;

        public static async Task<CapturedRequest> CreateAsync(HttpRequestMessage request, CancellationToken token)
        {
            var contentHeaders = request.Content is null
                ? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()
                : request.Content.Headers;
            var headers = request.Headers.Concat(contentHeaders)
                .ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            return new(request.Method, request.RequestUri!, headers, body);
        }
    }
}
