using System.Net;
using FugaTES.WinForms.Runtime;
using FugaTES.WinForms.Sd;
using Microsoft.Extensions.Configuration;

namespace R158.SapInboundReadiness.Tests;

// SD: o que protege a tela de ordens de venda antes de qualquer requisicao - o perfil (origem,
// client e referencia de credencial) e o guarda de rota/metodo. Nenhuma senha participa destes
// testes: a credencial e resolvida por requisicao no Gerenciador de Credenciais do Windows.
public sealed class OrdensVendaSdComposicaoTests
{
    private static IConfiguration Perfil(params (string Chave, string? Valor)[] mudancas)
    {
        var valores = new Dictionary<string, string?>
        {
            ["Sap:BaseUrl"] = OrdensVendaSdComposition.OrigemAprovada + "/sap/opu/odata/sap/API_BUSINESS_PARTNER/",
            ["Sap:SapClient"] = OrdensVendaSdComposition.SapClientAprovado,
            ["Carrier:Secrets:SapUser"] = "FUGATES",
            ["Carrier:Secrets:SapCredentialTarget"] = "FugaTES/Sap/Dev"
        };
        foreach (var (chave, valor) in mudancas) valores[chave] = valor;
        return new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
    }

    [Fact]
    public void Perfil_aprovado_passa()
        => OrdensVendaSdComposition.Validar(Perfil());

    // A origem do perfil aponta para o servico de parceiros; o que importa e a AUTORIDADE, porque
    // o caminho da colecao SD vem do adapter.
    [Fact]
    public void Origem_e_conferida_pela_autoridade_e_nao_pelo_caminho()
        => OrdensVendaSdComposition.Validar(Perfil(("Sap:BaseUrl", OrdensVendaSdComposition.OrigemAprovada)));

    [Theory]
    [InlineData("Sap:BaseUrl", "http://vhfufds4ci.sap.fugacouros.com.br:44300")]  // sem TLS
    [InlineData("Sap:BaseUrl", "https://vhfufds4ci.sap.fugacouros.com.br")]       // outra porta
    [InlineData("Sap:BaseUrl", "https://outro.invalido:44300")]
    [InlineData("Sap:BaseUrl", "https://usuario:senha@vhfufds4ci.sap.fugacouros.com.br:44300")]
    [InlineData("Sap:BaseUrl", null)]
    [InlineData("Sap:SapClient", "100")]
    [InlineData("Sap:SapClient", null)]
    [InlineData("Carrier:Secrets:SapUser", null)]
    [InlineData("Carrier:Secrets:SapUser", " ")]
    [InlineData("Carrier:Secrets:SapCredentialTarget", null)]
    public void Perfil_fora_do_aprovado_e_recusado(string chave, string? valor)
        => Assert.Throws<DesktopStartupException>(() => OrdensVendaSdComposition.Validar(Perfil((chave, valor))));

    private sealed class Contador : HttpMessageHandler
    {
        public int Chamadas { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Chamadas++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"d":{"results":[]}}""") });
        }
    }

    private static async Task<(HttpStatusCode? Status, int Chamadas)> Enviar(HttpMethod metodo, string url)
    {
        var contador = new Contador();
        using var client = new HttpClient(new OrdensVendaSdHttpGuard(contador));
        using var request = new HttpRequestMessage(metodo, url);
        try { using var resposta = await client.SendAsync(request); return (resposta.StatusCode, contador.Chamadas); }
        catch (InvalidOperationException) { return (null, contador.Chamadas); }
    }

    [Fact]
    public async Task Get_na_colecao_aprovada_passa()
    {
        var (status, chamadas) = await Enviar(HttpMethod.Get,
            OrdensVendaSdComposition.OrigemAprovada + "/sap/opu/odata/sap/API_SALES_ORDER_SRV/A_SalesOrder?sap-client=110");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, chamadas);
    }

    // Recusa ANTES de virar requisicao: o handler interno nao pode ter sido chamado.
    [Theory]
    [InlineData("POST", "https://vhfufds4ci.sap.fugacouros.com.br:44300/sap/opu/odata/sap/API_SALES_ORDER_SRV/A_SalesOrder")]
    [InlineData("PATCH", "https://vhfufds4ci.sap.fugacouros.com.br:44300/sap/opu/odata/sap/API_SALES_ORDER_SRV/A_SalesOrder")]
    [InlineData("DELETE", "https://vhfufds4ci.sap.fugacouros.com.br:44300/sap/opu/odata/sap/API_SALES_ORDER_SRV/A_SalesOrder")]
    [InlineData("GET", "https://vhfufds4ci.sap.fugacouros.com.br:44300/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner")]
    [InlineData("GET", "https://vhfufds4ci.sap.fugacouros.com.br:44300/sap/bc/adt")]
    [InlineData("GET", "https://outro.invalido/sap/opu/odata/sap/API_SALES_ORDER_SRV/A_SalesOrder")]
    [InlineData("GET", "http://vhfufds4ci.sap.fugacouros.com.br:44300/sap/opu/odata/sap/API_SALES_ORDER_SRV/A_SalesOrder")]
    public async Task Metodo_rota_ou_origem_fora_do_recorte_nao_sai(string metodo, string url)
    {
        var (status, chamadas) = await Enviar(new HttpMethod(metodo), url);
        Assert.Null(status);
        Assert.Equal(0, chamadas);
    }
}
