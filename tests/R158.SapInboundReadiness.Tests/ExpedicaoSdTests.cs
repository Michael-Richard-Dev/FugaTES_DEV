using FugaTES.Aplicacao.Modelos.Sd;
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
        // A saida de mercadoria nao tem mais rota neste cliente: o codigo foi removido, e o que
        // sobra e a guarda HTTP, testada abaixo.
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
            "https://exemplo.invalido" + "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/PostGoodsIssue");
        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(pedido, CancellationToken.None));
        Assert.Equal(0, contador.Chamadas);
    }

    // --- Escopo: ate onde vai o FugaTES -----------------------------------------------------
    // --- Escopo, por ETAPA ------------------------------------------------------------------
    //
    // Decisao do operador em 02/11/2026, em duas partes: a SAIDA de mercadoria e feita dentro do
    // SAP, por quem confere o carregamento, e nunca e nossa. A SEPARACAO e nossa, mas so em casca
    // (62) e fibra (66) - nos demais setores ela nao existe no processo.
    //
    // Eu tinha desligado as duas por escopo, de uma vez; o operador corrigiu: picking de casca e
    // fibra e parte do fluxo e acontece depois do texto da NF-e.

    [Fact]
    public void A_saida_nunca_e_do_fugates()
        => Assert.False(EscopoExpedicaoSd.SaidaNoFugaTES);

    [Theory]
    [InlineData(SetorAtividadeSd.Casca)]
    [InlineData(SetorAtividadeSd.Fibra)]
    public void O_picking_de_casca_e_fibra_e_do_fugates(string setor)
        => Assert.True(EscopoExpedicaoSd.PickingNoFugaTES(setor));

    [Theory]
    [InlineData(SetorAtividadeSd.Biodiesel)]
    [InlineData(SetorAtividadeSd.Graos)]
    [InlineData(SetorAtividadeSd.Retrovenda)]
    [InlineData(null)]
    public void Nos_demais_setores_nao_ha_separacao(string? setor)
        => Assert.False(EscopoExpedicaoSd.PickingNoFugaTES(setor));

    [Fact]
    public void As_mensagens_dizem_o_motivo_e_nao_sugerem_defeito()
    {
        // Quem le "indisponivel" vai procurar falha de configuracao onde ha limite de processo.
        Assert.Contains("dentro do SAP", EscopoExpedicaoSd.MotivoSaida, StringComparison.Ordinal);
        Assert.DoesNotContain("indisponível", EscopoExpedicaoSd.MotivoSaida, StringComparison.Ordinal);
        var picking = EscopoExpedicaoSd.MotivoPicking(SetorAtividadeSd.Biodiesel);
        Assert.Contains("casca (62) e fibra (66)", picking, StringComparison.Ordinal);
        Assert.DoesNotContain("indisponível", picking, StringComparison.Ordinal);
    }

    // --- A guarda HTTP acompanha a decisao ---------------------------------------------------

    [Fact]
    public async Task O_picking_sai_pela_tela()
        => Assert.True((await Enviar(HttpMethod.Post,
            ExpedicaoSdSapClient.RotaPicking + "?DeliveryDocument='80001997'")).Passou);

    [Fact]
    public async Task A_saida_de_mercadoria_nao_sai_nem_pela_tela()
    {
        // Mesmo com o botao fora de escopo, a guarda recusa: duas travas para o passo que move
        // estoque e nao se desfaz.
        var (passou, chamadas) = await Enviar(HttpMethod.Post,
            "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/PostGoodsIssue" + "?DeliveryDocument='80001997'");
        Assert.False(passou);
        Assert.Equal(0, chamadas);
    }
}
