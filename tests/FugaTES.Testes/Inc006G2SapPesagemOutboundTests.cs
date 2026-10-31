using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
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
        await EnviarSerializadoAsync(handler, Fixture());
        Assert.Equal("Fetch", handler.Requests[0].Header("X-CSRF-Token"));
    }

    [Fact]
    public async Task TokenRetornadoEhUsadoNoPost()
    {
        var handler = SuccessHandler();
        await EnviarSerializadoAsync(handler, Fixture());
        Assert.Equal("csrf-001", handler.Requests[1].Header("X-CSRF-Token"));
    }

    [Fact]
    public async Task CookieDoGetEhUsadoNoPost()
    {
        var handler = SuccessHandler();
        await EnviarSerializadoAsync(handler, Fixture());
        Assert.Contains("SAP_SESSION=abc123", handler.Requests[1].Header("Cookie"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostEhExecutadoNoMaximoUmaVez()
    {
        var handler = SuccessHandler();
        await EnviarSerializadoAsync(handler, Fixture());
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
        await EnviarSerializadoAsync(handler, Fixture(tipoOperacao: 50));
        Assert.Equal(50, PostJson(handler)["TipoOperacao"]!.GetValue<int>());
    }

    [Fact]
    public async Task PedidoComprasNuloPermaneceOpcional()
    {
        var handler = SuccessHandler();
        await EnviarSerializadoAsync(handler, Fixture(pedidoCompras: null));
        Assert.Null(PostJson(handler)["to_Pesagem"]![0]!["PedidoCompras"]);
    }

    [Fact]
    public async Task ContratoAcmUsaSomenteValorExplicitoDoMvp()
    {
        var handler = SuccessHandler();
        await EnviarSerializadoAsync(handler, Fixture(contratoAcm: "ACM-WIRE"));
        Assert.Equal("ACM-WIRE", PostJson(handler)["to_Pesagem"]![0]!["ContratoACM"]!.GetValue<string>());
    }

    [Fact]
    public async Task OperacaoContratoNaoParticipaDoTipoOperacao()
    {
        var handler = SuccessHandler();
        await EnviarSerializadoAsync(handler, Fixture(tipoOperacao: 10));
        Assert.Equal(10, PostJson(handler)["TipoOperacao"]!.GetValue<int>());
    }

    [Fact]
    public async Task NrProtocoloAusenteBloqueiaAntesDoHttp()
    {
        var handler = SuccessHandler();
        var failure = await Assert.ThrowsAsync<FalhaPesagemSapMvpException>(() =>
            Gateway(handler).EnviarAsync(Fixture() with { NumeroProtocolo = null }, CancellationToken.None));
        Assert.Equal("R161B_TICKET_IDENTITY_OR_WEIGHTS_UNVERIFIED", failure.Message);
        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva, failure.Classificacao);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NrProtocoloEhOmitidoNoWireAcmSemPerderCorrelacaoGuardian()
    {
        var handler = SuccessHandler();
        var pesagem = Fixture(numeroProtocolo: "12345") with { ProtocoloCriadoEm = new DateTimeOffset(2026, 9, 24, 11, 38, 28, TimeSpan.FromHours(-3)) };
        await EnviarSerializadoAsync(handler, pesagem);
        // R164B, spreadsheet row 55: omit ACM wire field, retain the Guardian link internally.
        var item = PostJson(handler)["to_Pesagem"]![0]!.AsObject();
        Assert.False(item.ContainsKey("NrProtocolo"));
        Assert.Equal("12345", pesagem.NumeroProtocolo);
        var evidencia = Assert.IsType<AcmSapTicketEvidence>(pesagem.EvidenciaTicket);
        Assert.Equal(pesagem.NumeroProtocolo, evidencia.ProtocoloGuardian);
        Assert.Equal(pesagem.ContratoACM, evidencia.ContratoACM);
        Assert.Null(item["DtProtocolo"]);
        Assert.Null(item["HrProtocolo"]);
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
        await EnviarSerializadoAsync(handler, Fixture(id, tipoOperacao, tipoDocumento, null, $"ACM-{id}"));
        var json = PostJson(handler);
        Assert.Equal(tipoOperacao, json["TipoOperacao"]!.GetValue<int>());
        Assert.Equal(tipoDocumento, json["to_Pesagem"]![0]!["TipoDocumento"]!.GetValue<string>());
        Assert.Equal($"ACM-{id}", json["to_Pesagem"]![0]!["ContratoACM"]!.GetValue<string>());
    }

    [Fact]
    public async Task JsonWireUsaNomesExatos()
    {
        var handler = SuccessHandler();
        await EnviarSerializadoAsync(handler, Fixture());
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
        await EnviarSerializadoAsync(handler, Fixture());
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
        var failure = await Assert.ThrowsAsync<FalhaPesagemSapMvpException>(() => Gateway(handler).EnviarAsync(Fixture(), CancellationToken.None));
        Assert.Equal(FugaTES.Aplicacao.Modelos.MotorIntegracao.ClassificacaoFalhaIntegracao.ResultadoIndeterminado, failure.Classificacao);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, ClassificacaoFalhaIntegracao.FalhaDefinitiva)]
    [InlineData(HttpStatusCode.InternalServerError, ClassificacaoFalhaIntegracao.ResultadoIndeterminado)]
    [InlineData(HttpStatusCode.TooManyRequests, ClassificacaoFalhaIntegracao.ResultadoIndeterminado)]
    public async Task PostNao2xxTemClassificacaoConservadoraSemRetry(
        HttpStatusCode status, ClassificacaoFalhaIntegracao expected)
    {
        var handler = HandlerWithPost(new HttpResponseMessage(status));
        var failure = await Assert.ThrowsAsync<PesagemSapHttpException>(() =>
            Gateway(handler).EnviarAsync(Fixture(), CancellationToken.None));
        Assert.Equal(expected, failure.Classificacao);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task TokenCsrfAusenteFalhaAntesDoPost()
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var failure = await Assert.ThrowsAsync<PesagemSapHttpException>(() =>
            Gateway(handler).EnviarAsync(Fixture(), CancellationToken.None));
        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaTransitoriaSegura, failure.Classificacao);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    private static PesagemSapHttpGateway Gateway(RecordingHandler handler) =>
        new(new HttpClient(handler), new OpcoesSap { BaseUrl = new("https://sap.invalid/"), SapClient = "110", Timeout = TimeSpan.FromSeconds(5) }, new NoAuth(), new CookieContainer());

    private static async Task EnviarSerializadoAsync(RecordingHandler handler, PesagemSapMvp pesagem)
    {
        if (pesagem.Centro == "3011" && pesagem.TipoDocumento == "AC3P")
        {
            // R164B does not equate transport success with SAP business confirmation.
            var failure = await Assert.ThrowsAsync<FalhaPesagemSapMvpException>(() =>
                Gateway(handler).EnviarAsync(pesagem, CancellationToken.None));
            Assert.Equal("R164B_SAP_BUSINESS_CONFIRMATION_NOT_PROVEN", failure.Message);
            Assert.Equal(ClassificacaoFalhaIntegracao.ResultadoIndeterminado, failure.Classificacao);
        }
        else
            await Gateway(handler).EnviarAsync(pesagem, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

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
        string contratoAcm = "ACM-001",
        string numeroProtocolo = "9471")
    {
        var pesagem = new PesagemSapMvp("Toledo", "3011", "E", id, 123, "TRANSP-1", "7000000", "PARCEIRO-1",
            1200m, 200m, 1000m, tipoDocumento, pedidoCompras, "DOC-1", tipoOperacao, contratoAcm);
        if (tipoDocumento != "AC3P") return pesagem;

        // R164E synthetic evidence shape; no provider attestation or operational authorization.
        var callback = new CallbackGuardianMvp(numeroProtocolo, id, 1200m, 200m, 1000m, "5");
        var pessoaFisica = id == "COMPRA_PF";
        return pesagem with
        {
            NumeroProtocolo = numeroProtocolo,
            EvidenciaTicket = new AcmSapTicketEvidence("SYNTHETIC-G2-TICKET", numeroProtocolo, id,
                CallbackGuardianMvpFingerprint.Calcular(callback),
                contratoAcm, pesagem.Centro, pesagem.Material, pesagem.IdParceiro,
                pessoaFisica ? "PF" : "PJ", pessoaFisica ? "PROP-1" : null, pesagem.TipoPesagem,
                new DateTimeOffset(2026, 10, 21, 16, 30, 0, TimeSpan.FromHours(-3)),
                [new("SYNTHETIC_QUALITY", null, 1.2m)], ChaveAcessoNFe: new string('1', 44),
                TipoOperacaoSap: tipoOperacao, TipoOperacaoSapProveniencia: "SYNTHETIC_OPERATION_RULE",
                TipoOperacaoSapEvidenceReference: "SYNTHETIC-G2-OPERATION")
        };
    }

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
