using FugaTES.Aplicacao.Contratos.Sd;
using System.Net;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.WinForms.Sd;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// Passo 3 visto de fora: o que a tela pode mandar pela rede, e o que ela monta a partir de uma
/// devolutiva ja gravada (a linha lida do banco, nao o evento original).
/// </summary>
public sealed class RemessaSdComposicaoTests
{
    private sealed class Contador : HttpMessageHandler
    {
        public int Chamadas { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Chamadas++;
            var resposta = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"d":{}}""") };
            resposta.Headers.TryAddWithoutValidation("X-CSRF-Token", "t");
            return Task.FromResult(resposta);
        }
    }

    private static async Task<(bool Passou, int Chamadas)> Enviar(HttpMethod metodo, string url)
    {
        var contador = new Contador();
        using var client = new HttpClient(new RemessaSdHttpGuard(contador, FugaTES.Aplicacao.Configuracao.AmbientesSap.Dev110.Origem));
        using var pedido = new HttpRequestMessage(metodo, url);
        try
        {
            using var _ = await client.SendAsync(pedido, CancellationToken.None);
            return (true, contador.Chamadas);
        }
        catch (InvalidOperationException) { return (false, contador.Chamadas); }
        catch (HttpRequestException erro) when (erro.InnerException is InvalidOperationException)
        { return (false, contador.Chamadas); }
    }

    private const string Origem = "https://vhfufds4ci.sap.fugacouros.com.br:44300";

    [Fact]
    public async Task Post_na_colecao_de_remessas_e_liberado()
    {
        var (passou, chamadas) = await Enviar(HttpMethod.Post, Origem + RemessaSdSapClient.Rota);
        Assert.True(passou);
        Assert.Equal(1, chamadas);
    }

    [Fact]
    public async Task Get_na_colecao_de_remessas_e_liberado_para_o_fetch_do_csrf()
        => Assert.True((await Enviar(HttpMethod.Get, Origem + RemessaSdSapClient.Rota)).Passou);

    [Fact]
    public async Task Get_na_ordem_de_venda_e_liberado_para_ler_o_setor()
        => Assert.True((await Enviar(HttpMethod.Get,
            Origem + "/sap/opu/odata/sap/API_SALES_ORDER_SRV/A_SalesOrder('2985')?$select=OrganizationDivision")).Passou);

    [Theory]
    // POST fora da colecao de remessas: nenhuma outra escrita sai desta tela.
    [InlineData("POST", "/sap/opu/odata/sap/API_SALES_ORDER_SRV/A_SalesOrder")]
    [InlineData("POST", "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryItem")]
    // Metodos que apagariam ou alterariam o que ja existe.
    [InlineData("DELETE", "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryHeader")]
    [InlineData("PATCH", "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryHeader")]
    // Servico que nao pertence ao passo 3.
    [InlineData("GET", "/sap/opu/odata/sap/API_MATERIAL_STOCK_SRV/A_MatlStkInAcctMod")]
    public async Task Rota_ou_metodo_fora_do_passo_3_nao_sai(string metodo, string caminho)
    {
        var (passou, chamadas) = await Enviar(new HttpMethod(metodo), Origem + caminho);
        Assert.False(passou);
        Assert.Equal(0, chamadas);
    }

    [Theory]
    [InlineData("http://vhfufds4ci.sap.fugacouros.com.br:44300")]
    [InlineData("https://vhfufds4ci.sap.fugacouros.com.br")]
    [InlineData("https://exemplo.invalido")]
    public async Task Origem_diferente_da_aprovada_nao_sai(string origem)
    {
        var (passou, chamadas) = await Enviar(HttpMethod.Post, origem + RemessaSdSapClient.Rota);
        Assert.False(passou);
        Assert.Equal(0, chamadas);
    }

    // --- Montagem a partir da linha lida do banco ------------------------------------------

    [Fact]
    public void Linha_de_setor_comum_vira_o_peso_liquido_em_quilo()
    {
        var pedido = MapeadorRemessaSd.Montar("2985", "10", 31_240.5m, SetorAtividadeSd.Graos);
        var item = Assert.Single(pedido.Itens.Results);
        Assert.Equal("2985", item.ReferenceSDDocument);
        Assert.Equal("10", item.ReferenceSDDocumentItem);
        Assert.Equal("31240.500", item.ActualDeliveryQuantity);
        Assert.Equal("KG", item.DeliveryQuantityUnit);
    }

    [Fact]
    public void Linha_de_biodiesel_vira_um_litro_mesmo_com_peso_na_balanca()
    {
        var item = Assert.Single(
            MapeadorRemessaSd.Montar("2985", "10", 31_240.5m, SetorAtividadeSd.Biodiesel).Itens.Results);
        Assert.Equal("1", item.ActualDeliveryQuantity);
        Assert.Equal("L", item.DeliveryQuantityUnit);
    }

    [Fact]
    public void As_duas_sobrecargas_montam_o_mesmo_corpo()
    {
        var devolutiva = new DevolutivaPesagemSd("77", "3011", "S", new DateTime(2026, 11, 1),
            40_000m, 15_000m, 25_000m, "2985", "10", "ABC1D23", "Fulano", "{}");
        Assert.Equal(
            MapeadorRemessaSd.Serializar(MapeadorRemessaSd.Montar(devolutiva, SetorAtividadeSd.Graos)),
            MapeadorRemessaSd.Serializar(MapeadorRemessaSd.Montar("2985", "10", 25_000m, SetorAtividadeSd.Graos)));
    }

    [Theory]
    [InlineData(null, "10")]
    [InlineData("2985", null)]
    [InlineData("  ", "10")]
    public void Linha_sem_correlacao_nao_monta_remessa(string? ordem, string? linha)
        => Assert.Equal("SD_REMESSA_SEM_CORRELACAO", Assert.Throws<RemessaSdBloqueadaException>(
            () => MapeadorRemessaSd.Montar(ordem, linha, 1m, SetorAtividadeSd.Graos)).Codigo);

    [Fact]
    public void Zeros_a_esquerda_vindos_do_banco_nao_entram_no_corpo()
    {
        // O SAP recusou "000010" com VL/367 em 01/11/2026: o item da ordem e "10".
        var item = Assert.Single(
            MapeadorRemessaSd.Montar(" 2985 ", " 10 ", 1m, SetorAtividadeSd.Graos).Itens.Results);
        Assert.Equal("2985", item.ReferenceSDDocument);
        Assert.Equal("10", item.ReferenceSDDocumentItem);
    }

    // --- Nova tentativa depois de uma recusa -------------------------------------------------
    //
    // Em 01/11/2026 o SAP recusou a pesagem 0020335 com "VL/096 Order is incomplete". A causa
    // estava na ordem de venda, nao em nos. A tela entao bloqueava qualquer segunda tentativa, e
    // destravar exigia apagar a linha no banco a mao.

    [Fact]
    public void Recusada_permite_tentar_de_novo()
        => Assert.True(EstadoRemessaSd.PermiteNovaTentativa(EstadoRemessaSd.Recusada));

    [Theory]
    // Criada: a remessa existe. Desconhecido: ela PODE existir - e supor que nao e o que cria a
    // segunda remessa para a mesma pesagem.
    [InlineData(EstadoRemessaSd.Criada)]
    [InlineData(EstadoRemessaSd.DesfechoDesconhecido)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("recusada")]
    public void Os_demais_desfechos_continuam_bloqueando(string? estado)
        => Assert.False(EstadoRemessaSd.PermiteNovaTentativa(estado));
}
