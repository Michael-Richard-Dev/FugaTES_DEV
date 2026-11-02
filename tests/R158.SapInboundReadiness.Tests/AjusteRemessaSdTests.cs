using FugaTES.Aplicacao.Contratos.Sd;
using System.Net;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.WinForms.Sd;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// Passo 4, conforme "APIS SD para FUGATES": PATCH no cabecalho sempre, pesos e itens zerados so
/// no biodiesel.
/// </summary>
public sealed class AjusteRemessaSdTests
{
    private const decimal Bruto = 27_400m;
    private const decimal Liquido = 25_400m;

    [Fact]
    public void Biodiesel_leva_pesos_e_transporte_ao_cabecalho()
    {
        var pedido = MapeadorAjusteRemessaSd.MontarCabecalho(
            SetorAtividadeSd.Biodiesel, Bruto, Liquido, "ABC1024");
        Assert.Equal("27400.000", pedido.PesoBruto);
        Assert.Equal("25400.000", pedido.PesoLiquido);
        Assert.Equal("ABC1024", pedido.MeioDeTransporte);
        Assert.Equal("YN04", pedido.TipoDeTransporte);
    }

    [Theory]
    [InlineData(SetorAtividadeSd.Graos)]
    [InlineData(SetorAtividadeSd.Casca)]
    [InlineData(SetorAtividadeSd.Retrovenda)]
    public void Fora_do_biodiesel_o_peso_nao_vai_ao_cabecalho(string setor)
    {
        // O item ja esta em KG; mandar peso sobrescreveria o que o SAP derivou dele.
        var json = MapeadorAjusteRemessaSd.Serializar(
            MapeadorAjusteRemessaSd.MontarCabecalho(setor, Bruto, Liquido, "ABC1024"));
        Assert.DoesNotContain("HeaderGrossWeight", json, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderNetWeight", json, StringComparison.Ordinal);
        Assert.Contains("\"MeansOfTransport\":\"ABC1024\"", json, StringComparison.Ordinal);
        Assert.Contains("\"MeansOfTransportType\":\"YN04\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Placa_vai_como_veio_da_balanca_sem_uf_inventada()
    {
        // Combinado em 01/11/2026: a UF ("ABC1024/RS" no documento) fica para depois. Compor um
        // estado aqui seria inventar um dado que termina na NF-e.
        var pedido = MapeadorAjusteRemessaSd.MontarCabecalho(
            SetorAtividadeSd.Graos, Bruto, Liquido, "  COF7595  ");
        Assert.Equal("COF7595", pedido.MeioDeTransporte);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sem_placa_o_ajuste_e_bloqueado(string? placa)
        => Assert.Equal("SD_AJUSTE_PLACA_AUSENTE", Assert.Throws<RemessaSdBloqueadaException>(
            () => MapeadorAjusteRemessaSd.MontarCabecalho(
                SetorAtividadeSd.Graos, Bruto, Liquido, placa)).Codigo);

    [Theory]
    [InlineData(null, "SD_AJUSTE_SETOR_AUSENTE")]
    [InlineData("99", "SD_AJUSTE_SETOR_DESCONHECIDO")]
    public void Setor_ausente_ou_fora_da_lista_bloqueia(string? setor, string codigo)
        => Assert.Equal(codigo, Assert.Throws<RemessaSdBloqueadaException>(
            () => MapeadorAjusteRemessaSd.MontarCabecalho(setor, Bruto, Liquido, "ABC1024")).Codigo);

    [Theory]
    [InlineData(0, 0, "SD_AJUSTE_PESO_INVALIDO")]
    [InlineData(-1, 10, "SD_AJUSTE_PESO_INVALIDO")]
    // Liquido maior que bruto e dado impossivel; se chegar assim, nao vai para a NF-e.
    [InlineData(100, 200, "SD_AJUSTE_PESO_INCOERENTE")]
    public void Peso_improvavel_nao_vai_ao_cabecalho_do_biodiesel(
        decimal bruto, decimal liquido, string codigo)
        => Assert.Equal(codigo, Assert.Throws<RemessaSdBloqueadaException>(
            () => MapeadorAjusteRemessaSd.MontarCabecalho(
                SetorAtividadeSd.Biodiesel, bruto, liquido, "ABC1024")).Codigo);

    [Fact]
    public void Peso_improvavel_nao_bloqueia_quem_nao_usa_peso()
        => Assert.Null(MapeadorAjusteRemessaSd
            .MontarCabecalho(SetorAtividadeSd.Graos, 0, 0, "ABC1024").PesoBruto);

    [Fact]
    public void Item_zerado_tem_o_corpo_do_documento()
        => Assert.Equal("""{"ItemGrossWeight":"0","ItemNetWeight":"0"}""",
            MapeadorAjusteRemessaSd.Serializar(MapeadorAjusteRemessaSd.MontarItemZerado()));

    [Theory]
    [InlineData(SetorAtividadeSd.Biodiesel, true)]
    [InlineData(SetorAtividadeSd.Graos, false)]
    [InlineData(SetorAtividadeSd.Casca, false)]
    public void So_o_biodiesel_zera_itens(string setor, bool esperado)
        => Assert.Equal(esperado, AjusteRemessaSd.AjustaPeso(setor));

    // --- Leitura da remessa: as chaves vem do SAP ------------------------------------------

    private const string RespostaReal = """
    {"d":{"__metadata":{"etag":"W/\"'0001'\""},"DeliveryDocument":"80003858",
     "to_DeliveryDocumentItem":{"results":[
       {"__metadata":{"etag":"W/\"'0002'\""},"DeliveryDocument":"80003858","DeliveryDocumentItem":"000010"}]}}}
    """;

    [Fact]
    public void Chave_do_item_vem_do_sap_com_os_zeros_que_ele_devolveu()
    {
        // O documento escreve DeliveryDocumentItem='10'; o SAP devolve "000010". Usar o que ele
        // deu evita escolher entre os dois no escuro.
        var lida = MapeadorAjusteRemessaSd.LerRemessa(RespostaReal, etagHttp: null);
        Assert.NotNull(lida);
        Assert.Equal("80003858", lida!.DeliveryDocument);
        var item = Assert.Single(lida.Itens);
        Assert.Equal("000010", item.DeliveryDocumentItem);
        Assert.Equal("W/\"'0002'\"", item.ETag);
    }

    [Fact]
    public void Etag_do_cabecalho_http_tem_precedencia_sobre_o_do_corpo()
        => Assert.Equal("W/\"'0007'\"",
            MapeadorAjusteRemessaSd.LerRemessa(RespostaReal, "W/\"'0007'\"")!.ETag);

    [Fact]
    public void Etag_do_corpo_serve_quando_o_cabecalho_http_nao_veio()
        => Assert.Equal("W/\"'0001'\"", MapeadorAjusteRemessaSd.LerRemessa(RespostaReal, null)!.ETag);

    [Theory]
    [InlineData("")]
    [InlineData("nao e json")]
    [InlineData("""{"d":{"to_DeliveryDocumentItem":{"results":[]}}}""")]
    public void Resposta_sem_numero_de_remessa_nao_vira_leitura(string corpo)
        => Assert.Null(MapeadorAjusteRemessaSd.LerRemessa(corpo, null));

    // --- Chaves de URL ----------------------------------------------------------------------

    [Fact]
    public void Chave_do_cabecalho_usa_aspas_simples_do_odata()
        => Assert.EndsWith("/A_OutbDeliveryHeader('80003858')",
            AjusteRemessaSdSapClient.ChaveCabecalho("80003858"), StringComparison.Ordinal);

    [Fact]
    public void Chave_do_item_leva_remessa_e_item()
        => Assert.EndsWith("/A_OutbDeliveryItem(DeliveryDocument='80003858',DeliveryDocumentItem='000010')",
            AjusteRemessaSdSapClient.ChaveItem("80003858", "000010"), StringComparison.Ordinal);

    [Theory]
    [InlineData("80003858')/to_DeliveryDocumentText?x=('")]
    [InlineData("8000 3858")]
    [InlineData("abc")]
    [InlineData("12345678901")]
    public void Chave_que_nao_e_numero_nao_entra_na_url(string numero)
        => Assert.Throws<ArgumentException>(() => AjusteRemessaSdSapClient.ChaveCabecalho(numero));

    // --- Guarda HTTP -------------------------------------------------------------------------

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

    private static async Task<(bool Passou, int Chamadas)> Enviar(HttpMethod metodo, string url)
    {
        var contador = new Contador();
        using var client = new HttpClient(new RemessaSdHttpGuard(contador));
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

    [Theory]
    [InlineData("/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryHeader('80003858')")]
    [InlineData("/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryItem(DeliveryDocument='80003858',DeliveryDocumentItem='000010')")]
    public async Task Patch_na_entidade_da_remessa_e_liberado(string caminho)
        => Assert.True((await Enviar(HttpMethod.Patch, Origem + caminho)).Passou);

    [Theory]
    // PATCH na COLECAO nao tem alvo definido: alteraria o que o servico escolher.
    [InlineData("PATCH", "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryHeader")]
    [InlineData("PATCH", "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryItem")]
    // Picking e saida de mercadoria sao acoes, nao entidades: PATCH nelas nao existe.
    // (Os POST liberados nessas duas rotas estao em ExpedicaoSdTests.)
    [InlineData("PATCH", "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/PostGoodsIssue")]
    [InlineData("PATCH", "/sap/opu/odata/sap/API_SALES_ORDER_SRV/A_SalesOrder('2985')")]
    [InlineData("DELETE", "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryHeader('80003858')")]
    public async Task Metodo_ou_rota_fora_do_passo_4_nao_sai(string metodo, string caminho)
    {
        var (passou, chamadas) = await Enviar(new HttpMethod(metodo), Origem + caminho);
        Assert.False(passou);
        Assert.Equal(0, chamadas);
    }

    [Fact]
    public async Task Get_na_entidade_da_remessa_e_liberado_para_ler_etag_e_itens()
        => Assert.True((await Enviar(HttpMethod.Get, Origem
            + "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryHeader('80003858')"
            + "?$expand=to_DeliveryDocumentItem")).Passou);

    // --- ETag depois do cabecalho ---------------------------------------------------------
    //
    // Em 02/11/2026, na remessa 80003859: o cabecalho gravou (204) e o item recusou com 412,
    // "State of the resource (entity) was already changed (If-Match)". O PATCH do cabecalho muda
    // a versao do documento inteiro, e com ela o ETag de cada item - o que foi lido antes do
    // cabecalho ja nao vale depois dele.

    [Fact]
    public void Itens_lidos_antes_do_cabecalho_tem_etag_proprio()
    {
        // O ETag do item e diferente do ETag do cabecalho; e por isso que ele precisa ser relido
        // depois da gravacao, e nao reaproveitado.
        var lida = MapeadorAjusteRemessaSd.LerRemessa(RespostaReal, etagHttp: null);
        var item = Assert.Single(lida!.Itens);
        Assert.NotEqual(lida.ETag, item.ETag);
    }

    // --- Nova tentativa depois de recusa ----------------------------------------------------

    [Fact]
    public void Ajuste_recusado_permite_tentar_de_novo()
        => Assert.True(EstadoAjusteRemessaSd.PermiteNovaTentativa(EstadoAjusteRemessaSd.Recusada));

    [Theory]
    [InlineData(EstadoAjusteRemessaSd.Ajustada)]
    [InlineData(EstadoAjusteRemessaSd.DesfechoDesconhecido)]
    [InlineData(null)]
    public void Ajuste_concluido_ou_sem_desfecho_continua_bloqueando(string? estado)
        => Assert.False(EstadoAjusteRemessaSd.PermiteNovaTentativa(estado));

    [Fact]
    public void Texto_recusado_permite_tentar_de_novo()
        => Assert.True(EstadoTextoNfeSd.PermiteNovaTentativa(EstadoTextoNfeSd.Recusado));

    [Fact]
    public void Picking_recusado_permite_tentar_de_novo()
        => Assert.True(EstadoPickingSd.PermiteNovaTentativa(EstadoPickingSd.Recusado));

    [Fact]
    public void Saida_recusada_permite_tentar_de_novo()
        => Assert.True(EstadoSaidaSd.PermiteNovaTentativa(EstadoSaidaSd.Recusada));

    [Fact]
    public void Saida_com_desfecho_desconhecido_nunca_libera()
        // A saida pode ter acontecido. Repetir duplicaria o movimento de estoque - e estoque
        // movimentado nao volta por API neste fluxo.
        => Assert.False(EstadoSaidaSd.PermiteNovaTentativa(EstadoSaidaSd.DesfechoDesconhecido));
}
