using System.Net;
using System.Text;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Integracao.Guardian;

namespace FugaTES.Testes;

public sealed class AcmGuardianH4SoapIdentityOfflineTests
{
    private const string Ns = "http://toledobrasil.com.br/WS_Guardian";
    private static readonly ContratoGuardianSet26 Input = new("0000001031", "Synthetic", 1m, "308102", 0m, "309837");
    private static GuardianHttpOptions Options() => new()
    {
        ContratoSoapEndpoint = new Uri("https://guardian.invalid/WS_GUARDIAN_PLUS.asmx"),
        Timeout = TimeSpan.FromSeconds(10)
    };
    private static string Response(string inner)
        => $"<soap:Envelope xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'><soap:Body><CadastraContratoResponse xmlns='{Ns}'>{inner}</CadastraContratoResponse></soap:Body></soap:Envelope>";

    [Fact]
    public async Task Erro_zero_sem_codigo_retorna_identidade_nao_comprovada()
    {
        var client = new GuardianContratoSoapClient(new HttpClient(new SyntheticHandler(Response("<Erro>0</Erro>"))), Options());
        var response = await client.CadastrarComRespostaAsync(Input, CancellationToken.None);
        Assert.Null(response.CodigoContratoConfirmado);
    }

    [Fact]
    public async Task Codigo_presente_no_SOAP_e_retornado_sem_padding()
    {
        var client = new GuardianContratoSoapClient(new HttpClient(new SyntheticHandler(
            Response("<Erro>0</Erro><Contrato><Codigo>0000001031</Codigo></Contrato>"))), Options());
        var response = await client.CadastrarComRespostaAsync(Input, CancellationToken.None);
        Assert.Equal("0000001031", response.CodigoContratoConfirmado);
    }

    [Fact]
    public async Task Duas_identidades_na_resposta_bloqueiam()
    {
        var client = new GuardianContratoSoapClient(new HttpClient(new SyntheticHandler(
            Response("<Erro>0</Erro><Contrato><Codigo>A</Codigo></Contrato><Contrato><Codigo>B</Codigo></Contrato>"))), Options());
        await Assert.ThrowsAsync<GuardianHttpException>(() => client.CadastrarComRespostaAsync(Input, CancellationToken.None));
    }

    private sealed class SyntheticHandler(string xml) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "text/xml")
            });
    }
}
