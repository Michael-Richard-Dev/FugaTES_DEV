using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Integracao.Guardian;
using Xunit;

namespace FugaTES.Testes;

// Gate FUGATES-MVP-GUARDIAN-IMPLEMENT-NOW-POST-MEETING-R1.
// Prova o wire do Guardian Item (POST /api/v0/Guardian/Item) e o wire ja existente de CadastraContrato.
// Nenhuma chamada Guardian real: HttpMessageHandler fake.
public sealed class MvpGuardianItemAndContratoWireTests
{
    // ---------------- Guardian Item ----------------

    [Fact]
    public async Task Item_Post_NoEndpointComBearer()
    {
        var handler = SuccessHandler();
        await Cliente(handler).EnviarAsync(Item(), CancellationToken.None);

        var item = ItemRequest(handler);
        Assert.Equal(HttpMethod.Post, item.Method);
        Assert.Equal("https://guardian.invalid/api/v0/Guardian/Item", item.Uri.AbsoluteUri);
        Assert.Equal("application/json", item.MediaType);
        Assert.Equal("Bearer", item.AuthScheme);
        Assert.Equal("TOKEN-1", item.AuthParameter);
    }

    [Fact]
    public async Task Item_MinimumJson_CodigoTipoNomeFatorConversao()
    {
        var handler = SuccessHandler();
        await Cliente(handler).EnviarAsync(Item(), CancellationToken.None);

        var body = JsonNode.Parse(ItemRequest(handler).Body!)!.AsObject();
        Assert.Equal("ITEM-1", (string?)body["codigo"]);
        Assert.Equal("T1", (string?)body["tipo"]);
        Assert.Equal("Milho", (string?)body["nome"]);
        Assert.Equal(2.5m, body["fatorConversao"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task Item_Estado_OmitidoQuandoNulo()
    {
        var handler = SuccessHandler();
        await Cliente(handler).EnviarAsync(Item(), CancellationToken.None);

        var body = JsonNode.Parse(ItemRequest(handler).Body!)!.AsObject();
        Assert.False(body.ContainsKey("estado"));
    }

    [Fact]
    public async Task Item_Estado_EnviadoQuandoInformado()
    {
        var handler = SuccessHandler();
        await Cliente(handler).EnviarAsync(Item() with { Estado = "1" }, CancellationToken.None);

        var body = JsonNode.Parse(ItemRequest(handler).Body!)!.AsObject();
        Assert.Equal("1", (string?)body["estado"]);
    }

    // tipo segue verbatim: nenhum enum/lista de valores e imposto pelo adapter.
    [Theory]
    [InlineData("T1")]
    [InlineData("ZZ-99_x")]
    [InlineData("0")]
    public async Task Item_Tipo_PreservadoVerbatim(string tipo)
    {
        var handler = SuccessHandler();
        await Cliente(handler).EnviarAsync(new ItemGuardianSet26("ITEM-1", tipo, "Milho", 2.5m), CancellationToken.None);

        var body = JsonNode.Parse(ItemRequest(handler).Body!)!.AsObject();
        Assert.Equal(tipo, (string?)body["tipo"]);
    }

    // fatorConversao segue verbatim: sem default, sem arredondamento, sem regra de faixa.
    [Theory]
    [InlineData("2.5")]
    [InlineData("0.001")]
    [InlineData("1000")]
    public async Task Item_FatorConversao_PreservadoVerbatim(string fator)
    {
        var valor = decimal.Parse(fator, System.Globalization.CultureInfo.InvariantCulture);
        var handler = SuccessHandler();
        await Cliente(handler).EnviarAsync(new ItemGuardianSet26("ITEM-1", "T1", "Milho", valor), CancellationToken.None);

        var raw = ItemRequest(handler).Body!;
        Assert.Equal(valor, JsonNode.Parse(raw)!.AsObject()["fatorConversao"]!.GetValue<decimal>());
        Assert.Contains("\"fatorConversao\":" + fator, raw, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "T1", "Milho")]
    [InlineData("ITEM-1", "", "Milho")]
    [InlineData("ITEM-1", "T1", "")]
    public async Task Item_MinimumWireAusente_FalhaFechadoSemHttp(string codigo, string tipo, string nome)
    {
        var handler = SuccessHandler();
        await Assert.ThrowsAsync<GuardianHttpException>(() =>
            Cliente(handler).EnviarAsync(new ItemGuardianSet26(codigo, tipo, nome, 1m), CancellationToken.None));

        Assert.DoesNotContain(handler.Requests, r => r.Uri.AbsolutePath.EndsWith("/Item", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Item_Http500_FalhaFechadoSemBlindRetry()
    {
        var handler = new HandlerFake(r => IsToken(r)
            ? Json(HttpStatusCode.OK, "{\"token\":\"TOKEN-1\"}")
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<GuardianHttpException>(() => Cliente(handler).EnviarAsync(Item(), CancellationToken.None));

        Assert.Equal(1, handler.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/Item", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Item_401_InvalidaTokenEReobtemNaProximaChamada()
    {
        var itemCalls = 0;
        var handler = new HandlerFake(r =>
        {
            if (IsToken(r)) return Json(HttpStatusCode.OK, "{\"token\":\"TOKEN-1\"}");
            return new HttpResponseMessage(++itemCalls == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
        });
        var cliente = Cliente(handler, Options() with { TokenCacheLifetime = TimeSpan.FromMinutes(5) });

        await Assert.ThrowsAsync<GuardianHttpException>(() => cliente.EnviarAsync(Item(), CancellationToken.None));
        await cliente.EnviarAsync(Item(), CancellationToken.None);

        // Mecanismo Bearer preservado: 401 invalida o token em cache e a chamada seguinte o reobtem.
        Assert.Equal(2, handler.Requests.Count(IsTokenRecorded));
    }

    // ---------------- CadastraContrato (SOAP) : provas de wire ----------------

    [Fact]
    public async Task CadastraContrato_UsaPostEContentTypeTextXmlCharsetUtf8()
    {
        var handler = new SoapHandlerFake(HttpStatusCode.OK, SoapOk);
        await new GuardianContratoSoapClient(new HttpClient(handler), SoapOptions()).CadastrarAsync(Contrato(), CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("text/xml; charset=utf-8", handler.ContentTypeCompleto);
        Assert.Equal("\"http://toledobrasil.com.br/WS_Guardian/CadastraContrato\"", handler.SoapActionBruto);
    }

    [Fact]
    public async Task CadastraContrato_EnvelopeSoap11()
    {
        var handler = new SoapHandlerFake(HttpStatusCode.OK, SoapOk);
        await new GuardianContratoSoapClient(new HttpClient(handler), SoapOptions()).CadastrarAsync(Contrato(), CancellationToken.None);

        var raiz = XDocument.Parse(handler.Body!).Root!;
        Assert.Equal(XName.Get("Envelope", "http://schemas.xmlsoap.org/soap/envelope/"), raiz.Name);
    }

    [Fact]
    public async Task CadastraContrato_EndpointConfiguravel()
    {
        var handler = new SoapHandlerFake(HttpStatusCode.OK, SoapOk);
        var opcoes = SoapOptions() with { ContratoSoapEndpoint = new Uri("https://outro-guardian.invalid/alt/WS.asmx") };
        await new GuardianContratoSoapClient(new HttpClient(handler), opcoes).CadastrarAsync(Contrato(), CancellationToken.None);

        Assert.Equal("https://outro-guardian.invalid/alt/WS.asmx", handler.Uri!.AbsoluteUri);
    }

    [Fact]
    public async Task CadastraContrato_SemBlindRetry_UmaUnicaRequisicao()
    {
        var handler = new SoapHandlerFake(HttpStatusCode.InternalServerError, "<Envelope/>");
        await Assert.ThrowsAsync<GuardianHttpException>(() =>
            new GuardianContratoSoapClient(new HttpClient(handler), SoapOptions()).CadastrarAsync(Contrato(), CancellationToken.None));

        Assert.Equal(1, handler.Chamadas);
    }

    // ---------------- harness ----------------

    private const string SoapOk = "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><CadastraContratoResponse xmlns=\"http://toledobrasil.com.br/WS_Guardian\"><Erro>0</Erro></CadastraContratoResponse></soap:Body></soap:Envelope>";

    private static ItemGuardianSet26 Item() => new("ITEM-1", "T1", "Milho", 2.5m);

    private static ContratoGuardianSet26 Contrato() => new("CON-1", "Contrato 1", 12.5m, "1", 2.5m, "A");

    private static GuardianSet26HttpClient Cliente(HandlerFake handler, GuardianHttpOptions? opcoes = null)
    {
        var http = new HttpClient(handler);
        var configurado = opcoes ?? Options();
        return new GuardianSet26HttpClient(http, configurado, new GuardianTokenClient(http, configurado, new CredenciaisFake()));
    }

    private static GuardianHttpOptions Options() => new()
    {
        BaseUrl = new Uri("https://guardian.invalid/"),
        Timeout = TimeSpan.FromSeconds(10),
        TokenCacheLifetime = TimeSpan.Zero,
        PreCadastroEndpoint = "/api/v1/Guardian/ProtocoloPreCadastro"
    };

    private static GuardianHttpOptions SoapOptions() => new()
    {
        ContratoSoapEndpoint = new Uri("https://guardian.invalid/WS_GUARDIAN_PLUS.asmx"),
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static bool IsToken(HttpRequestMessage request)
        => request.RequestUri!.AbsolutePath.EndsWith("CreateToken", StringComparison.Ordinal);

    private static bool IsTokenRecorded(RequestRecord record)
        => record.Uri.AbsolutePath.EndsWith("CreateToken", StringComparison.Ordinal);

    private static RequestRecord ItemRequest(HandlerFake handler)
        => handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/Item", StringComparison.Ordinal));

    private static HandlerFake SuccessHandler() => new(r => IsToken(r)
        ? Json(HttpStatusCode.OK, "{\"token\":\"TOKEN-1\"}")
        : Json(HttpStatusCode.OK, "{}"));

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record RequestRecord(HttpMethod Method, Uri Uri, string? MediaType, string? Body, string? AuthScheme, string? AuthParameter);

    private sealed class CredenciaisFake : ICredenciaisGuardianSet26Provider
    {
        public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken cancellationToken)
            => Task.FromResult(new CredenciaisGuardianSet26("usuario-runtime", "senha-runtime", "assinatura-runtime"));
    }

    private sealed class HandlerFake(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestRecord(
                request.Method,
                request.RequestUri!,
                request.Content?.Headers.ContentType?.MediaType,
                body,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));
            return responder(request);
        }
    }

    private sealed class SoapHandlerFake(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public int Chamadas { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? ContentTypeCompleto { get; private set; }
        public string? SoapActionBruto { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Chamadas++;
            Method = request.Method;
            Uri = request.RequestUri;
            ContentTypeCompleto = request.Content?.Headers.ContentType?.ToString();
            SoapActionBruto = request.Headers.TryGetValues("SOAPAction", out var values) ? values.Single() : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "text/xml") };
        }
    }
}
