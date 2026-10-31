using System.Net;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;

namespace FugaTES.Testes;

public sealed class CarrierMasterSapJsonNegotiationTests
{
    [Theory]
    [InlineData("BP-CARRIER-A")]
    [InlineData("BP-CARRIER-B")]
    public async Task Carrier_read_negotiates_json_when_SAP_defaults_to_atom(string code)
    {
        var handler = new Handler(code, carrier: true);
        var adapter = Adapter(handler);
        var result = await adapter.ConsultarAsync(code, default);
        Assert.Equal(StatusConsultaSap.Sucesso, result.Status);
        Assert.Equal(code, result.Dados!.BusinessPartner);
        Assert.Equal(1, handler.Gets);
        Assert.Equal(0, handler.Writes);
    }

    [Fact]
    public async Task Supplier_without_CRM010_is_not_a_carrier()
    {
        var handler = new Handler("BP-SUPPLIER", carrier: false);
        var result = await Adapter(handler).ConsultarAsync("BP-SUPPLIER", default);
        Assert.Equal(StatusConsultaSap.CategoriaNaoCorrespondente, result.Status);
        Assert.Equal(0, handler.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_boolean_blocking_flag_uses_existing_blocking_rules(bool blocked)
    {
        var result = await Adapter(new Handler("BP-CARRIER-BOOLEAN", true, blocked)).ConsultarAsync("BP-CARRIER-BOOLEAN", default);
        Assert.Equal(StatusConsultaSap.Sucesso, result.Status);
        Assert.Equal(blocked ? FugaTES.Aplicacao.Modelos.SAP.SituacaoBloqueioSap.Bloqueado : FugaTES.Aplicacao.Modelos.SAP.SituacaoBloqueioSap.NaoBloqueado, result.Dados!.BloqueioSap);
    }

    private static ConsultaTransportadoraSapAdapter Adapter(Handler handler) => new(
        new ClienteHttpParceiroNegocioSap(new HttpClient(handler),
            new OpcoesSap { BaseUrl = new Uri("https://sap.invalid/API_BUSINESS_PARTNER/") }, new Auth()), TimeProvider.System);

    private sealed class Auth : IAutenticacaoSap
    {
        public Task AplicarAsync(HttpRequestMessage request, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Handler(string code, bool carrier, bool? blocked = null) : HttpMessageHandler
    {
        public int Gets, Writes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) Gets++; else Writes++;
            if (!request.Headers.Accept.Any(h => h.MediaType == "application/json"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("<feed />", System.Text.Encoding.UTF8, "application/atom+xml") });
            var roles = carrier ? "{\"BusinessPartnerRole\":\"CRM010\",\"ValidFrom\":\"/Date(1577836800000)/\",\"ValidTo\":\"/Date(4102444800000)/\"}" : "";
            var payload = "{\"d\":{\"BusinessPartner\":\"" + code + "\",\"BusinessPartnerCategory\":\"2\",\"BusinessPartnerIsBlocked\":\"false\",\"BusinessPartnerFullName\":\"Carrier\",\"to_Supplier\":{},\"to_BusinessPartnerRole\":{\"results\":[" + roles + "]},\"to_BusinessPartnerTax\":{\"results\":[]},\"to_BusinessPartnerAddress\":{\"results\":[]}}}";
            if (blocked is not null) payload = payload.Replace("\"BusinessPartnerIsBlocked\":\"false\"", "\"BusinessPartnerIsBlocked\":" + (blocked.Value ? "true" : "false"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}