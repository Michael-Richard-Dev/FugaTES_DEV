using FugaTES.Aplicacao.Contratos.Sd;
using System.Net;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.WinForms.Sd;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// Passo 6: picking e saida de mercadoria. A saida move estoque, entao o que estes testes
/// protegem e que nada alem destas duas acoes consiga sair daqui.
/// </summary>
public sealed class ExpedicaoSdTests
{
    [Fact]
    public void Remessa_vai_na_query_entre_aspas_simples()
        => Assert.Equal("DeliveryDocument='80001997'", ExpedicaoSdSapClient.Parametro("80001997"));

    [Theory]
    [InlineData("80001997'&x='")]
    [InlineData("abc")]
    [InlineData(" ")]
    [InlineData("12345678901")]
    public void Remessa_que_nao_e_numero_nao_entra_na_query(string numero)
        => Assert.ThrowsAny<ArgumentException>(() => ExpedicaoSdSapClient.Parametro(numero));

    [Fact]
    public void Rotas_sao_acoes_do_servico_sem_barra_dupla()
    {
        // O documento da Fuga escreve ";v=2//ConfirmPickingAllItems"; barra dupla no caminho e
        // aceita por alguns servidores e recusada por outros.
        Assert.DoesNotContain("//", ExpedicaoSdSapClient.RotaPicking[8..], StringComparison.Ordinal);
        Assert.EndsWith("/ConfirmPickingAllItems", ExpedicaoSdSapClient.RotaPicking, StringComparison.Ordinal);
        Assert.EndsWith("/PostGoodsIssue", ExpedicaoSdSapClient.RotaSaida, StringComparison.Ordinal);
    }

    // --- Guarda HTTP ---------------------------------------------------------------------------

    private sealed class Contador : HttpMessageHandler
    {
        public int Chamadas { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Chamadas++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private const string Origem = "https://vhfufds4ci.sap.fugacouros.com.br:44300";

    private static async Task<(bool Passou, int Chamadas)> Enviar(HttpMethod metodo, string caminho)
    {
        var contador = new Contador();
        using var client = new HttpClient(new RemessaSdHttpGuard(contador));
        using var pedido = new HttpRequestMessage(metodo, Origem + caminho);
        try
        {
            using var _ = await client.SendAsync(pedido, CancellationToken.None);
            return (true, contador.Chamadas);
        }
        catch (InvalidOperationException) { return (false, contador.Chamadas); }
        catch (HttpRequestException erro) when (erro.InnerException is InvalidOperationException)
        { return (false, contador.Chamadas); }
    }

    [Fact]
    public async Task Post_nas_duas_acoes_e_liberado()
    {
        Assert.True((await Enviar(HttpMethod.Post,
            ExpedicaoSdSapClient.RotaPicking + "?DeliveryDocument='80001997'")).Passou);
        Assert.True((await Enviar(HttpMethod.Post,
            ExpedicaoSdSapClient.RotaSaida + "?DeliveryDocument='80001997'")).Passou);
    }

    [Theory]
    // Outras acoes do mesmo servico nao fazem parte do fluxo.
    [InlineData("POST", "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/ReverseGoodsIssue")]
    [InlineData("POST", "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/DeleteDelivery")]
    // Metodo diferente de POST na acao.
    [InlineData("GET", "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/PostGoodsIssue")]
    [InlineData("DELETE", "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/PostGoodsIssue")]
    // Caminho que apenas COMECA como a acao: prefixo nao basta, tem de ser o caminho exato.
    [InlineData("POST", "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/PostGoodsIssueXyz")]
    public async Task Acao_fora_do_passo_6_nao_sai(string metodo, string caminho)
    {
        var (passou, chamadas) = await Enviar(new HttpMethod(metodo), caminho);
        Assert.False(passou);
        Assert.Equal(0, chamadas);
    }

    [Fact]
    public async Task Saida_em_outra_origem_nao_sai()
    {
        var contador = new Contador();
        using var client = new HttpClient(new RemessaSdHttpGuard(contador));
        using var pedido = new HttpRequestMessage(HttpMethod.Post,
            "https://exemplo.invalido" + ExpedicaoSdSapClient.RotaSaida);
        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(pedido, CancellationToken.None));
        Assert.Equal(0, contador.Chamadas);
    }

    // --- Escopo: ate onde vai o FugaTES -----------------------------------------------------
    //
    // Decisao do operador em 02/11/2026, conferida com o negocio: o FugaTES vai ate o texto da
    // NF-e. Separacao e saida de mercadoria sao feitas dentro do SAP, por quem confere o
    // carregamento. O documento "APIS SD para FUGATES" lista as duas APIs, mas enumerar a API nao
    // diz quem a chama - foi uma suposicao minha ao construir o passo 6.

    [Fact]
    public void Expedicao_nao_e_executada_pelo_fugates()
        => Assert.False(EscopoExpedicaoSd.ExecutadaPeloFugaTES);

    [Fact]
    public void O_motivo_diz_onde_a_expedicao_acontece()
    {
        // A mensagem precisa dizer que e limite de ESCOPO, e nao falha de configuracao: quem le
        // "indisponivel" vai procurar defeito onde nao ha.
        Assert.Contains("dentro do SAP", EscopoExpedicaoSd.Motivo, StringComparison.Ordinal);
        Assert.DoesNotContain("indisponível", EscopoExpedicaoSd.Motivo, StringComparison.Ordinal);
    }
}
