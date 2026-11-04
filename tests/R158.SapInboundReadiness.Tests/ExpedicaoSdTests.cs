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
        Assert.DoesNotContain("//", ExpedicaoSdSapClient.RotaSaida[8..], StringComparison.Ordinal);
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
    //
    // Em 04/11/2026 o PROCESSO definiu: separacao e saida de mercadoria sao feitas dentro do SAP,
    // em todos os setores. O passo 6 saiu do nosso fluxo.
    //
    // Antes disso a regra mudou tres vezes em 02/11: desligada, depois nossa em casca (62) e
    // fibra (66), depois nossa ate a saida naqueles dois. O codigo de todas essas etapas continua
    // inteiro - o que mudou foi a chave ExpedicaoHabilitada. Os testes abaixo descrevem os DOIS
    // estados, para que religar seja trocar uma constante e nao redescobrir o comportamento.

    [Fact]
    public void O_passo_6_esta_desligado()
        => Assert.False(EscopoExpedicaoSd.ExpedicaoHabilitada);

    [Theory]
    [InlineData(SetorAtividadeSd.Casca)]
    [InlineData(SetorAtividadeSd.Fibra)]
    [InlineData(SetorAtividadeSd.Biodiesel)]
    [InlineData(SetorAtividadeSd.Graos)]
    [InlineData(SetorAtividadeSd.Retrovenda)]
    [InlineData(null)]
    public void Nenhum_setor_tem_separacao_nem_saida_pelo_fugates(string? setor)
    {
        Assert.False(EscopoExpedicaoSd.PickingNoFugaTES(setor));
        Assert.False(EscopoExpedicaoSd.SaidaNoFugaTES(setor));
    }

    // Casca e fibra sao o caso que importa: foram os unicos setores em que as duas etapas ja
    // foram nossas. Se alguem religar a chave sem querer, e aqui que aparece.
    [Theory]
    [InlineData(SetorAtividadeSd.Casca)]
    [InlineData(SetorAtividadeSd.Fibra)]
    public void Nem_casca_nem_fibra_separam_mais(string setor)
    {
        Assert.False(EscopoExpedicaoSd.PickingNoFugaTES(setor));
        Assert.False(EscopoExpedicaoSd.SaidaNoFugaTES(setor));
    }

    // O recorte por setor continua escrito e nao foi apagado: e o que volta a valer se o negocio
    // mudar de ideia de novo.
    [Theory]
    [InlineData(SetorAtividadeSd.Casca, true)]
    [InlineData(SetorAtividadeSd.Fibra, true)]
    [InlineData(SetorAtividadeSd.Biodiesel, false)]
    [InlineData(null, false)]
    public void O_recorte_de_casca_e_fibra_continua_preservado(string? setor, bool esperado)
        => Assert.Equal(esperado, SetorAtividadeSd.ExigePicking(setor));

    [Theory]
    [InlineData(SetorAtividadeSd.Casca)]
    [InlineData(SetorAtividadeSd.Biodiesel)]
    [InlineData(null)]
    public void Separacao_e_saida_andam_juntas(string? setor)
        => Assert.Equal(EscopoExpedicaoSd.PickingNoFugaTES(setor),
            EscopoExpedicaoSd.SaidaNoFugaTES(setor));

    [Fact]
    public void As_mensagens_dizem_quem_faz_e_nao_sugerem_defeito()
    {
        // Quem le "indisponivel" vai procurar falha de configuracao onde ha limite de processo.
        foreach (var motivo in new[]
                 { EscopoExpedicaoSd.MotivoSaida(SetorAtividadeSd.Casca),
                   EscopoExpedicaoSd.MotivoPicking(SetorAtividadeSd.Casca) })
        {
            Assert.Contains("dentro do SAP", motivo, StringComparison.Ordinal);
            Assert.DoesNotContain("indisponível", motivo, StringComparison.Ordinal);
        }
    }

    // --- A guarda HTTP acompanha a decisao ---------------------------------------------------
    //
    // Segunda tranca, atras da MESMA chave: com o passo 6 desligado, as duas rotas nao saem daqui
    // nem por engano - nem que alguem chame o cliente diretamente.

    [Fact]
    public async Task O_picking_nao_sai_mais()
    {
        var (passou, chamadas) = await Enviar(HttpMethod.Post,
            ExpedicaoSdSapClient.RotaPicking + "?DeliveryDocument='80001997'");
        Assert.False(passou);
        Assert.Equal(0, chamadas);
    }

    [Fact]
    public async Task A_saida_de_mercadoria_nao_sai_mais()
    {
        var (passou, chamadas) = await Enviar(HttpMethod.Post,
            ExpedicaoSdSapClient.RotaSaida + "?DeliveryDocument='80001997'");
        Assert.False(passou);
        Assert.Equal(0, chamadas);
    }

    [Fact]
    public async Task Desfazer_a_saida_continua_fora()
    {
        // ReverseGoodsIssue nao e passo do fluxo. Se um dia for, entra por decisao, nao por
        // acidente de prefixo.
        var (passou, chamadas) = await Enviar(HttpMethod.Post,
            "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/ReverseGoodsIssue?DeliveryDocument='80001997'");
        Assert.False(passou);
        Assert.Equal(0, chamadas);
    }
}

/// <summary>
/// O que o SAP respondeu ao picking da remessa 80003869, em 02/11/2026: HTTP 200 com o relatorio
/// VAZIO. Nos gravamos CONFIRMADO, o VL03N mostrou separacao zerada, e a saida foi recusada
/// depois com VU/010 por documento incompleto.
///
/// Estes testes fixam a leitura do relatorio. Quem decide se separou e a lista, nao o codigo HTTP.
/// </summary>
public sealed class PickingReportSdTests
{
    private const string Vazio =
        """{"d":{"__metadata":{"type":"Collection(API_OUTBOUND_DELIVERY_SRV.PickingReport)"},"results":[]}}""";

    private const string ComUmItem =
        """{"d":{"results":[{"DeliveryDocument":"80003869","DeliveryDocumentItem":"10"}]}}""";

    [Fact]
    public void Relatorio_vazio_nao_e_separacao()
        => Assert.Equal(0, ExpedicaoSdSapClient.LerPickingReport(Vazio));

    [Fact]
    public void Relatorio_com_item_conta_o_item()
        => Assert.Equal(1, ExpedicaoSdSapClient.LerPickingReport(ComUmItem));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("isto nao e json")]
    [InlineData("""{"d":{}}""")]
    [InlineData("""{"d":{"results":{}}}""")]
    [InlineData("""{"results":[{"x":1}]}""")]
    public void Corpo_que_nao_e_o_relatorio_conta_como_zero(string? corpo)
        // Silencio nao e sucesso: o que nao se consegue ler nao autoriza gravar CONFIRMADO.
        => Assert.Equal(0, ExpedicaoSdSapClient.LerPickingReport(corpo));
}
