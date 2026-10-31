using System.Net;
using System.Text;
using System.Xml.Linq;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Integracao.Guardian;
using FugaTES.Aplicacao.Servicos.Mvp;

namespace FugaTES.Testes;

public sealed class Inc006GuardianContratoSoapTests
{
    [Fact]
    public async Task ValorProximidadeZeroEscopadoSerializaDecimalSemDefaultGlobal()
    {
        var handler = new HandlerFake(HttpStatusCode.OK, "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><CadastraContratoResponse xmlns=\"http://toledobrasil.com.br/WS_Guardian\"><Erro>0</Erro></CadastraContratoResponse></soap:Body></soap:Envelope>");
        var fixture = Contrato() with { Estado = "308102", TipoLancamento = "309837" };
        var comando = MapeadorContratoAcmGuardian.AplicarValorProximidadeFim(
            AcmGuardianReferenceMapperTests.VersaoAprovada(), fixture, 0m);
        Assert.Equal(fixture with { ValorProximidadeFimContrato = 0m }, comando);
        Assert.Equal(2.5m, fixture.ValorProximidadeFimContrato);
        await new GuardianContratoSoapClient(new HttpClient(handler), Options()).CadastrarAsync(comando, default);
        var document = XDocument.Parse(handler.Body!);
        XNamespace ns = "http://toledobrasil.com.br/WS_Guardian";
        Assert.Equal("0", Assert.Single(document.Descendants(ns + "ValorProximidadeFimContrato")).Value);
        Assert.Equal("308102", Assert.Single(document.Descendants(ns + "Estado")).Value);
        Assert.Equal("309837", Assert.Single(document.Descendants(ns + "TipoLancamento")).Value);
    }

    [Fact]
    public async Task CadastraContrato_envia_wire_configurado_e_aceita_Erro_zero()
    {
        var handler = new HandlerFake(HttpStatusCode.OK, "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><CadastraContratoResponse xmlns=\"http://toledobrasil.com.br/WS_Guardian\"><Erro>0</Erro></CadastraContratoResponse></soap:Body></soap:Envelope>");
        var client = new GuardianContratoSoapClient(new HttpClient(handler), Options());

        await client.CadastrarAsync(Contrato(), CancellationToken.None);

        Assert.Equal("https://guardian.invalid/WS_GUARDIAN_PLUS.asmx", handler.Uri!.AbsoluteUri);
        Assert.Equal("http://toledobrasil.com.br/WS_Guardian/CadastraContrato", handler.SoapAction);
        Assert.Equal("text/xml", handler.ContentType);
        var document = XDocument.Parse(handler.Body!);
        XNamespace ns = "http://toledobrasil.com.br/WS_Guardian";
        var operation = Assert.Single(document.Descendants(ns + "CadastraContrato")).Element(ns + "voDadosContrato")!.Element(ns + "Contrato")!;
        Assert.Equal("CON-1", operation.Element(ns + "Codigo")?.Value);
        Assert.Equal("Contrato 1", operation.Element(ns + "Descricao")?.Value);
        Assert.Equal("12.5", operation.Element(ns + "ValorContrato")?.Value);
        Assert.Equal("1", operation.Element(ns + "Estado")?.Value);
        Assert.Equal("2.5", operation.Element(ns + "ValorProximidadeFimContrato")?.Value);
        Assert.Equal("A", operation.Element(ns + "TipoLancamento")?.Value);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("")]
    public async Task CadastraContrato_Erro_nao_zero_ou_ausente_falha_fechado(string erro)
    {
        var body = erro.Length == 0 ? "<Envelope/>" : $"<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><CadastraContratoResponse xmlns=\"http://toledobrasil.com.br/WS_Guardian\"><Erro>{erro}</Erro></CadastraContratoResponse></soap:Body></soap:Envelope>";
        var client = new GuardianContratoSoapClient(new HttpClient(new HandlerFake(HttpStatusCode.OK, body)), Options());
        await Assert.ThrowsAsync<GuardianHttpException>(() => client.CadastrarAsync(Contrato(), CancellationToken.None));
    }

    private static GuardianHttpOptions Options() => new()
    {
        ContratoSoapEndpoint = new Uri("https://guardian.invalid/WS_GUARDIAN_PLUS.asmx"),
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static ContratoGuardianSet26 Contrato() => new("CON-1", "Contrato 1", 12.5m, "1", 2.5m, "A");

    private sealed class HandlerFake(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? SoapAction { get; private set; }
        public string? ContentType { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            SoapAction = request.Headers.TryGetValues("SOAPAction", out var values) ? values.Single().Trim('"') : null;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "text/xml") };
        }
    }
}
