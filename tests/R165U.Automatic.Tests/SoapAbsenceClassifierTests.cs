using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Integracao.Guardian;
using Xunit;

public sealed class SoapAbsenceClassifierTests
{
    private const string Ns = "http://toledobrasil.com.br/WS_Guardian";
    private const string Description = "O c\u00f3digo do contrato informado n\u00e3o existe!";

    [Theory]
    [InlineData("1106")]
    [InlineData("1147")]
    [InlineData("1148")]
    [InlineData("0000001147")]
    [InlineData("987654")]
    public async Task Proven_business_absence_belongs_to_exact_requested_identity(string code)
    {
        using var wire = new SyntheticWire(Envelope(Entry()));
        using var http = new HttpClient(wire);
        var result = await Client(http).ConsultarAsync(code, default);
        Assert.Equal(EstadoConsultaContratoGuardian.InexistenteComprovado, result.Estado);
        Assert.Equal(code, wire.RequestedCode);
        Assert.Equal(1, wire.Requests);
        Assert.Null(result.Contrato);
    }

    [Theory]
    [InlineData("missing_error")]
    [InlineData("malformed_error")]
    [InlineData("other_error")]
    [InlineData("success_without_contract")]
    [InlineData("missing_description")]
    [InlineData("other_description")]
    [InlineData("duplicate_error")]
    [InlineData("duplicate_result")]
    [InlineData("different_contract")]
    [InlineData("nil_false_contract")]
    [InlineData("wrong_response_operation")]
    [InlineData("missing_result")]
    public async Task Ambiguous_response_never_proves_absence_or_dispatches_create(string scenario)
    {
        var entry = Entry();
        var ns = (XNamespace)Ns;
        switch (scenario)
        {
            case "missing_error": entry.Element(ns + "Erro")!.Remove(); break;
            case "malformed_error": entry.Element(ns + "Erro")!.Value = "unknown"; break;
            case "other_error": entry.Element(ns + "Erro")!.Value = "999999"; break;
            case "success_without_contract": entry.Element(ns + "Erro")!.Value = "0"; break;
            case "missing_description": entry.Element(ns + "Descricao")!.Remove(); break;
            case "other_description": entry.Element(ns + "Descricao")!.Value = "Access denied"; break;
            case "duplicate_error": entry.Add(new XElement(ns + "Erro", "309851")); break;
            case "different_contract": entry.Add(new XElement(ns + "Contrato", new XElement(ns + "Codigo", "1106"))); break;
            case "nil_false_contract": entry.Add(new XElement(ns + "Contrato", new XAttribute(XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance"), "false"))); break;
        }
        var xml = scenario switch
        {
            "duplicate_result" => Envelope(entry, Entry()),
            "wrong_response_operation" => Envelope(entry).Replace("ConsultaContratoResponse", "CadastraContratoResponse"),
            "missing_result" => Envelope(),
            _ => Envelope(entry)
        };
        using var wire = new SyntheticWire(xml);
        using var http = new HttpClient(wire);
        var result = await Client(http).ConsultarAsync("1147", default);
        Assert.Equal(EstadoConsultaContratoGuardian.Indeterminado, result.Estado);
        Assert.Equal(1, wire.Requests);
    }

    [Fact]
    public async Task Soap_fault_is_not_remote_absence()
    {
        using var wire = new SyntheticWire("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultcode>s:Server</faultcode></s:Fault></s:Body></s:Envelope>");
        using var http = new HttpClient(wire);
        await Assert.ThrowsAsync<GuardianHttpException>(() => Client(http).ConsultarAsync("1147", default));
        Assert.Equal(1, wire.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task Authentication_transport_or_noncanonical_status_is_not_absence(HttpStatusCode status)
    {
        using var wire = new SyntheticWire(Envelope(Entry()), status);
        using var http = new HttpClient(wire);
        await Assert.ThrowsAsync<GuardianHttpException>(() => Client(http).ConsultarAsync("1147", default));
        Assert.Equal(1, wire.Requests);
    }

    [Fact]
    public async Task Timeout_never_retries_or_authorizes_create()
    {
        using var wire = new SyntheticWire(Envelope(Entry()), failure: new TaskCanceledException("SYNTHETIC_TIMEOUT"));
        using var http = new HttpClient(wire);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).ConsultarAsync("1147", default));
        Assert.Equal(1, wire.Requests);
    }

    [Fact]
    public async Task Network_failure_never_retries_or_authorizes_create()
    {
        using var wire = new SyntheticWire(Envelope(Entry()), failure: new HttpRequestException("SYNTHETIC_NETWORK_FAILURE"));
        using var http = new HttpClient(wire);
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(http).ConsultarAsync("1147", default));
        Assert.Equal(1, wire.Requests);
    }

    [Fact]
    public async Task Malformed_xml_never_proves_absence()
    {
        using var wire = new SyntheticWire("<malformed");
        using var http = new HttpClient(wire);
        await Assert.ThrowsAsync<XmlException>(() => Client(http).ConsultarAsync("1147", default));
        Assert.Equal(1, wire.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Missing_identity_prevents_even_lookup(string? code)
    {
        using var wire = new SyntheticWire(Envelope(Entry()));
        using var http = new HttpClient(wire);
        await Assert.ThrowsAsync<GuardianHttpException>(() => Client(http).ConsultarAsync(code!, default));
        Assert.Equal(0, wire.Requests);
    }

    [Fact]
    public async Task Identical_error_from_other_endpoint_is_not_an_approved_absence()
    {
        using var wire = new SyntheticWire(Envelope(Entry()));
        using var http = new HttpClient(wire);
        var gateway = new GuardianContratoSoapClient(http, new GuardianHttpOptions
        {
            ContratoSoapEndpoint = new("https://synthetic.invalid/other.asmx"),
            ContratoSoapEnvironment = "HML", Timeout = TimeSpan.FromSeconds(2)
        });
        Assert.Equal(EstadoConsultaContratoGuardian.Indeterminado,
            (await gateway.ConsultarAsync("1147", default)).Estado);
        Assert.Equal(1, wire.Requests);
    }

    private static GuardianContratoSoapClient Client(HttpClient http) => new(http, new GuardianHttpOptions
    {
        ContratoSoapEndpoint = new("http://192.168.17.91/WS_GUARDIAN/WS_GUARDIAN_PLUS.asmx"),
        ContratoSoapEnvironment = "HML", ContratoSoapInternalHmlHttpApproved = true,
        Timeout = TimeSpan.FromSeconds(2)
    });

    private static XElement Entry() => new((XNamespace)Ns + "VORetornoContrato",
        new XElement((XNamespace)Ns + "Erro", "309851"), new XElement((XNamespace)Ns + "Descricao", Description));

    private static string Envelope(params XElement[] entries)
    {
        var soap = (XNamespace)"http://schemas.xmlsoap.org/soap/envelope/";
        var ns = (XNamespace)Ns;
        return new XDocument(new XElement(soap + "Envelope", new XElement(soap + "Body",
            new XElement(ns + "ConsultaContratoResponse", new XElement(ns + "voRetornoContrato", entries)))))
            .ToString(SaveOptions.DisableFormatting);
    }

    // All HTTP is intercepted in-process. No socket/real client handler is composed.
    private sealed class SyntheticWire(string response, HttpStatusCode status = HttpStatusCode.OK, Exception? failure = null) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? RequestedCode { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("\"" + Ns + "/ConsultaContrato\"", request.Headers.GetValues("SOAPAction").Single());
            var doc = XDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            RequestedCode = Assert.Single(doc.Descendants((XNamespace)Ns + "CodigoContrato")).Value;
            if (failure is not null) throw failure;
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "text/xml") };
        }
    }
}
