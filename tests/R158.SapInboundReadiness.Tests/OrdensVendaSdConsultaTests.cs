using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.Integracao.SAP.Http;

namespace R158.SapInboundReadiness.Tests;

// SD: a varredura de ordens de venda e consulta de COLECAO em API_SALES_ORDER_SRV (OData V2).
// Estes testes fixam o que da para verificar sem o SAP: o recorte obrigatorio, a query montada e
// o mapeamento da resposta - inclusive o caso em que a resposta vem paginada.
public sealed class OrdensVendaSdConsultaTests
{
    private static OrdensVendaSdFiltro Filtro(
        string org = "3000", string[]? centros = null, string[]? tipos = null, int top = 200)
        => new(org, centros ?? ["3011", "3008"], tipos ?? ["ZVSP"], top);

    // Lista de tipos vazia significa OS CINCO DO FLUXO, nunca "todos" - lista fechada pelo
    // operador em 04/11/2026. Antes a clausula sumia do $filter quando a lista vinha vazia, e a
    // varredura alcancava a base de vendas inteira do SAP.
    [Fact]
    public void Sem_tipo_de_documento_a_varredura_traz_os_cinco_do_fluxo()
    {
        Assert.True(Filtro(tipos: []).EhValido());
        var query = OrdensVendaSdConsultaAdapter.MontarQuery(Filtro(tipos: []));
        Assert.Equal("$filter=SalesOrganization eq '3000'"
            + " and (OverallSDProcessStatus eq 'A' or OverallSDProcessStatus eq 'B')"
            + " and (SalesOrderType eq 'ZVSP' or SalesOrderType eq 'ZVBI'"
            + " or SalesOrderType eq 'ZVSC' or SalesOrderType eq 'ZRMS'"
            + " or SalesOrderType eq 'ZVNE')"
            + "&$expand=to_Item,to_Partner&$orderby=SalesOrder desc&$top=200", query);
    }

    [Fact]
    public void A_lista_do_fluxo_e_a_que_o_operador_fechou()
        => Assert.Equal(["ZVSP", "ZVBI", "ZVSC", "ZRMS", "ZVNE"], OrdensVendaSdFiltro.TiposPermitidos);

    [Theory]
    [InlineData("ZBAL")]   // o tipo que a tela usava antes de a lista existir
    [InlineData("TA")]     // tipo padrao do SAP, fora do fluxo da balanca
    [InlineData("zvsp")]   // caixa diferente: o SAP compara exato, e nos tambem
    public void Tipo_fora_da_lista_reprova_o_filtro(string tipo)
        // Reprovar, e nao ignorar: uma ordem de outro tipo nao vai a balanca, e varrer o que nao
        // vai a balanca gasta varredura e enche a lista do operador.
        => Assert.False(Filtro(tipos: [tipo]).EhValido());

    [Fact]
    public void Tipo_repetido_reprova_o_filtro()
        => Assert.False(Filtro(tipos: ["ZVSP", "ZVSP"]).EhValido());

    /// <summary>
    /// Corte por data de criacao: e o que impede o motor automatico de varrer o acervo antigo.
    /// Vai no $filter para o SAP nao mandar o que seria descartado.
    /// </summary>
    [Fact]
    public void Corte_por_data_entra_no_filtro()
        => Assert.Contains("and CreationDate ge datetime'2026-11-01T00:00:00'",
            OrdensVendaSdConsultaAdapter.MontarQuery(
                Filtro() with { DataCriacaoMinima = new DateOnly(2026, 11, 1) }),
            StringComparison.Ordinal);

    [Fact]
    public void Sem_corte_a_query_nao_menciona_data()
        => Assert.DoesNotContain("CreationDate", OrdensVendaSdConsultaAdapter.MontarQuery(Filtro()),
            StringComparison.Ordinal);

    [Theory]
    [InlineData("/Date(1793491200000)/", 2026, 11, 1)]
    // Com deslocamento de fuso o instante continua sendo o numero antes do sinal, ja em UTC.
    [InlineData("/Date(1793491200000+0200)/", 2026, 11, 1)]
    public void Data_do_odata_v2_e_lida(string valor, int ano, int mes, int dia)
    {
        var data = OrdensVendaSdConsultaAdapter.DataOData(valor);
        Assert.NotNull(data);
        Assert.Equal(new DateOnly(ano, mes, dia), DateOnly.FromDateTime(data!.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-11-01")]
    [InlineData("/Date(abc)/")]
    [InlineData("/Date()/")]
    public void Data_fora_do_formato_vira_nulo(string? valor)
        => Assert.Null(OrdensVendaSdConsultaAdapter.DataOData(valor));

    [Fact]
    public void Filtro_sem_centro_e_invalido()
        => Assert.False(Filtro(centros: []).EhValido());

    // Organizacao e fixa: nao basta ser alfanumerica, tem de ser a da Fuga.
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("30 00")]
    [InlineData("3000'")]
    [InlineData("3001")]
    [InlineData("1000")]
    public void Organizacao_diferente_da_fixa_reprova_o_filtro(string org)
        => Assert.False(Filtro(org: org).EhValido());

    // A regra dos centros mora no filtro, nao so na lista da tela: montado em codigo, um centro
    // fora do fluxo tambem tem de reprovar.
    [Theory]
    [InlineData("9999")]
    [InlineData("3012")]
    [InlineData("")]
    [InlineData("3011 ")]
    public void Centro_fora_do_permitido_reprova_o_filtro(string centro)
        => Assert.False(Filtro(centros: [centro]).EhValido());

    [Fact]
    public void Centro_repetido_reprova_o_filtro()
        => Assert.False(Filtro(centros: ["3011", "3011"]).EhValido());

    [Theory]
    [InlineData("3011")]
    [InlineData("3008")]
    [InlineData("3011,3008")]
    public void Centros_permitidos_sao_aceitos(string centros)
        => Assert.True(Filtro(centros: centros.Split(',')).EhValido());

    // O que a tela oferece e exatamente o que o filtro aceita - sem terceira fonte de verdade.
    [Fact]
    public void A_tela_usa_os_mesmos_valores_fixos_do_filtro()
    {
        Assert.Equal("3000", OrdensVendaSdFiltro.OrganizacaoFixa);
        Assert.Equal(["3011", "3008"], OrdensVendaSdFiltro.CentrosPermitidos);
        Assert.Equal(OrdensVendaSdFiltro.OrganizacaoFixa, FugaTES.WinForms.FrmOrdensVendaSd.OrganizacaoInicial);
    }

    [Fact]
    public void Filtro_completo_e_valido()
        => Assert.True(Filtro().EhValido());

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void Limite_por_varredura_fora_da_faixa_reprova(int top)
        => Assert.False(Filtro(top: top).EhValido());

    // A query e o contrato real com o SAP: organizacao e tipo no $filter, itens e parceiros no
    // $expand. O centro NAO entra no $filter porque e campo de item.
    [Fact]
    public void Query_filtra_por_organizacao_e_tipos_e_expande_itens_e_parceiros()
    {
        var query = OrdensVendaSdConsultaAdapter.MontarQuery(Filtro(tipos: ["ZVSP", "ZVBI"]));
        Assert.Contains("$filter=SalesOrganization eq '3000'", query, StringComparison.Ordinal);
        Assert.Contains("SalesOrderType eq 'ZVSP' or SalesOrderType eq 'ZVBI'", query, StringComparison.Ordinal);
        Assert.Contains("$expand=to_Item,to_Partner", query, StringComparison.Ordinal);
        Assert.Contains("$top=200", query, StringComparison.Ordinal);
        Assert.DoesNotContain("ShippingPoint", query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sem ordenacao explicita o SAP devolve pela chave, crescente, e o $top vira uma janela
    /// sobre as ordens MAIS ANTIGAS. Em 01/11/2026 havia 268 ordens elegiveis: a 4232, criada no
    /// dia, ficava fora das 200 trazidas e nao aparecia na tela.
    /// </summary>
    [Fact]
    public void Varredura_pede_as_ordens_mais_recentes_primeiro()
    {
        var query = OrdensVendaSdConsultaAdapter.MontarQuery(Filtro());
        Assert.Contains("$orderby=SalesOrder desc", query, StringComparison.Ordinal);
        // A ordenacao tem de vir antes do $top para descrever a janela, nao o contrario.
        Assert.True(query.IndexOf("$orderby", StringComparison.Ordinal)
                  < query.IndexOf("$top", StringComparison.Ordinal));
    }

    // Apostrofo em literal OData V2 se escapa dobrando; sem isso o filtro mudaria de significado.
    [Fact]
    public void Apostrofo_no_tipo_e_escapado()
        => Assert.Contains("SalesOrderType eq 'Z''B'", OrdensVendaSdConsultaAdapter.MontarQuery(
            new OrdensVendaSdFiltro("3000", ["3011"], ["Z'B"])), StringComparison.Ordinal);

    // Parceiros deliberadamente fora de ordem e com um terceiro no meio: a selecao e por FUNCAO,
    // nunca por posicao na lista.
    private const string RespostaV2 = """
    {"d":{"results":[
      {"SalesOrder":"5000001","SalesOrderType":"ZBAL","SalesOrganization":"3000",
       "OverallSDProcessStatus":"A","OverallDeliveryStatus":"A",
       "to_Item":{"results":[
         {"SalesOrderItem":"10","Material":"7000000","ShippingPoint":"3011","OrganizationDivision":"01"},
         {"SalesOrderItem":"20","Material":"7000002","ShippingPoint":"3008","OrganizationDivision":"01"},
         {"SalesOrderItem":"30","Material":"7000009","ShippingPoint":"9999","OrganizationDivision":"01"}]},
       "to_Partner":{"results":[
         {"Customer":"99999999","PartnerFunctionInternalCode":"RE"},
         {"Customer":"20000777","PartnerFunctionInternalCode":"WE"},
         {"Customer":"20000051","PartnerFunctionInternalCode":"AG"}]}}
    ]}}
    """;

    // Mesmo caminho de producao: parse e mapeamento pelo proprio adapter, nao uma copia.
    private static IReadOnlyList<OrdemVendaSdLeitura> Mapear(string corpo, OrdensVendaSdFiltro filtro)
        => OrdensVendaSdConsultaAdapter.LerResposta(corpo, filtro);

    // O centro e campo de ITEM: a linha de um centro fora do fluxo nao pode aparecer, nem quando
    // o filtro traz os dois centros permitidos.
    [Fact]
    public void Itens_de_centro_fora_do_fluxo_sao_descartados()
    {
        var linhas = Mapear(RespostaV2, Filtro());
        Assert.Equal(["3011", "3008"], linhas.Select(l => l.Centro));
        Assert.DoesNotContain(linhas, l => l.LinhaItem == "30");
    }

    // Escolhendo um centro, so ele vem - e o caso normal da tela.
    [Fact]
    public void Um_centro_escolhido_traz_so_as_linhas_dele()
    {
        var linha = Assert.Single(Mapear(RespostaV2, Filtro(centros: ["3008"])));
        Assert.Equal("3008", linha.Centro);
        Assert.Equal("20", linha.LinhaItem);
        Assert.Equal("7000002", linha.Material);
    }

    // Uma ordem com varios itens rende varias linhas: a granularidade da tela e o item.
    [Fact]
    public void Uma_ordem_com_dois_itens_aprovados_rende_duas_linhas()
    {
        var linhas = Mapear(RespostaV2, Filtro());
        Assert.Equal(2, linhas.Count);
        Assert.All(linhas, l => Assert.Equal("5000001", l.CodigoOrdemVenda));
        Assert.Equal(["10", "20"], linhas.Select(l => l.LinhaItem));
    }

    [Fact]
    public void Campos_de_cabecalho_e_parceiro_chegam_na_linha()
    {
        var linha = Mapear(RespostaV2, Filtro(centros: ["3011"])).Single();
        Assert.Equal("A", linha.SituacaoOrdem);
        Assert.Equal("ZBAL", linha.TipoDocumentoVendas);
        Assert.Equal("3000", linha.EmpresaOrdem);
        Assert.Equal("A", linha.SituacaoOrdem);
        Assert.Equal("01", linha.SetorAtividade);
        Assert.Equal("20000051", linha.Emissor);            // funcao AG
        Assert.Equal("20000777", linha.RecebedorMercadoria); // funcao WE
        Assert.Contains("\"SalesOrder\":\"5000001\"", linha.PayloadJson, StringComparison.Ordinal);
    }

    // O parceiro sai pela funcao, nao pela posicao: trocar a ordem da lista nao muda o resultado.
    [Fact]
    public void Parceiro_e_escolhido_pela_funcao_e_nao_pela_posicao()
    {
        var invertido = RespostaV2
            .Replace("\"Customer\":\"20000051\",\"PartnerFunctionInternalCode\":\"AG\"", "\"Customer\":\"20000051\",\"PartnerFunctionInternalCode\":\"AG\" ", StringComparison.Ordinal);
        var linha = Mapear(invertido, Filtro(centros: ["3011"])).Single();
        Assert.Equal("20000051", linha.Emissor);
        Assert.Equal("20000777", linha.RecebedorMercadoria);
        // O primeiro parceiro da lista (RE) nao pode vazar para nenhum dos dois campos.
        Assert.NotEqual("99999999", linha.Emissor);
        Assert.NotEqual("99999999", linha.RecebedorMercadoria);
    }

    // Funcoes configuraveis: a planilha nomeia o campo, nao o valor da funcao.
    [Fact]
    public void Funcoes_de_parceiro_sao_configuraveis()
    {
        var filtro = Filtro(centros: ["3011"]) with { FuncaoEmissor = "RE", FuncaoRecebedor = "AG" };
        var linha = Mapear(RespostaV2, filtro).Single();
        Assert.Equal("99999999", linha.Emissor);
        Assert.Equal("20000051", linha.RecebedorMercadoria);
    }

    // Ordem sem parceiro na funcao esperada nao inventa valor.
    [Fact]
    public void Funcao_ausente_deixa_o_campo_nulo()
    {
        var linha = Mapear(RespostaV2, Filtro(centros: ["3011"]) with { FuncaoRecebedor = "ZZ" }).Single();
        Assert.Equal("20000051", linha.Emissor);
        Assert.Null(linha.RecebedorMercadoria);
    }

    // Resposta paginada entregue como se fosse o conjunto completo faria a varredura ignorar
    // ordens em silencio. Tem de falhar, nao truncar.
    [Theory]
    [InlineData("""{"d":{"__next":"http://x","results":[]}}""")]
    [InlineData("""{"@odata.nextLink":"http://x","value":[]}""")]
    public void Resposta_paginada_e_recusada(string corpo)
        => Assert.Throws<JsonException>(() => Mapear(corpo, Filtro()));

    [Fact]
    public void Colecao_vazia_e_resultado_valido_e_nao_erro()
    {
        var linhas = Mapear("""{"d":{"results":[]}}""", Filtro());
        Assert.Empty(linhas);
    }

    // V4 devolve "value" em vez de "d.results": o leitor aceita os dois.
    [Fact]
    public void Formato_v4_tambem_e_lido()
    {
        var linhas = Mapear("""
        {"value":[{"SalesOrder":"5000002","SalesOrderType":"ZBAL","SalesOrganization":"3000",
          "OverallSDProcessStatus":"A",
          "_Item":[{"SalesOrderItem":"10","Material":"7000000","ShippingPoint":"3011"}],
          "_Partner":[{"Customer":"20000184","PartnerFunctionInternalCode":"AG"}]}]}
        """, Filtro());
        var linha = Assert.Single(linhas);
        Assert.Equal("5000002", linha.CodigoOrdemVenda);
        Assert.Equal("20000184", linha.Emissor);
    }

    // Ordem sem codigo nao identifica nada: e descartada em vez de virar linha anonima.
    [Fact]
    public void Ordem_sem_codigo_e_descartada()
    {
        var linhas = Mapear("""
        {"d":{"results":[{"SalesOrderType":"ZBAL","to_Item":{"results":[{"ShippingPoint":"3011"}]}}]}}
        """, Filtro());
        Assert.Empty(linhas);
    }

    // A situacao da ordem e regra, nao conveniencia: vai no $filter para o SAP nao mandar o que
    // seria descartado. Desde 04/11/2026 sao duas - "A" e "B" -, entre parenteses, senao o "or"
    // se misturaria com os "and" vizinhos.
    [Fact]
    public void Situacao_da_ordem_entra_na_query()
        => Assert.Contains("and (OverallSDProcessStatus eq 'A' or OverallSDProcessStatus eq 'B')",
            OrdensVendaSdConsultaAdapter.MontarQuery(Filtro()), StringComparison.Ordinal);

    /// <summary>
    /// "B" e ordem PARCIALMENTE processada, e passou a entrar em 04/11/2026. O caso real: a ordem
    /// 4274 teve a linha 10 processada, o cabecalho virou "C" e a ordem inteira sumiu da
    /// varredura - junto com uma linha nova que nunca tinha ido a balanca.
    /// </summary>
    [Fact]
    public void Ordem_parcialmente_processada_entra()
        => Assert.NotEmpty(Mapear(
            RespostaV2.Replace("\"OverallSDProcessStatus\":\"A\"", "\"OverallSDProcessStatus\":\"B\"",
                StringComparison.Ordinal), Filtro()));

    // "C" e concluida: fica de fora, e e isso que distingue "tem linha pendente" de "acabou".
    [Theory]
    [InlineData("\"OverallSDProcessStatus\":\"C\"")]
    [InlineData("\"OverallSDProcessStatus\":\"\"")]
    public void Ordem_concluida_ou_sem_situacao_e_descartada(string situacao)
        => Assert.Empty(Mapear(
            RespostaV2.Replace("\"OverallSDProcessStatus\":\"A\"", situacao, StringComparison.Ordinal), Filtro()));

    // Fecha por ausencia: sem o campo, nao da para afirmar que a ordem esta em "A".
    [Fact]
    public void Ordem_sem_a_situacao_e_descartada()
        => Assert.Empty(Mapear(
            RespostaV2.Replace("\"OverallSDProcessStatus\":\"A\",", "", StringComparison.Ordinal), Filtro()));

    // O JSON da linha e projecao, nao a resposta inteira: o que nao serve a conferencia sai.
    [Fact]
    public void Json_da_linha_traz_so_os_campos_da_planilha()
    {
        var linha = Mapear(RespostaV2, Filtro(centros: ["3011"])).Single();
        using var json = JsonDocument.Parse(linha.PayloadJson);
        Assert.Equal(
            ["SalesOrder", "SalesOrderItem", "ShippingPoint", "Material",
             // PurchaseOrderByCustomer entrou em 04/11/2026, a pedido do operador.
             "PurchaseOrderByCustomer",
             // SDProcessStatus e a situacao do ITEM: e ela que diz se a linha ja foi.
             "OverallSDProcessStatus", "SDProcessStatus",
             "SalesOrderType", "SalesOrganization", "OrganizationDivision",
             "Emissor", "RecebedorMercadoria"],
            json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("20000051", json.RootElement.GetProperty("Emissor").GetProperty("Customer").GetString());
        Assert.Equal("AG", json.RootElement.GetProperty("Emissor").GetProperty("PartnerFunction").GetString());
        Assert.Equal("20000777", json.RootElement.GetProperty("RecebedorMercadoria").GetProperty("Customer").GetString());
        // A resposta traz OverallDeliveryStatus, que nao e a regra nem esta na planilha: fica
        // fora do JSON em vez de confundir a conferencia com um campo que nao se usa.
        // Navegacoes, outros itens, outros parceiros e metadados do servico tambem nao entram.
        foreach (var ruido in new[]
                 { "OverallDeliveryStatus", "to_Item", "to_Partner", "__metadata", "7000002", "99999999" })
            Assert.DoesNotContain(ruido, linha.PayloadJson, StringComparison.Ordinal);
    }

    // Cada linha carrega o SEU item: o JSON de uma nao pode descrever a outra.
    [Fact]
    public void Cada_linha_tem_o_json_do_proprio_item()
    {
        var linhas = Mapear(RespostaV2, Filtro());
        Assert.Contains("\"SalesOrderItem\":\"10\"", linhas[0].PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"Material\":\"7000000\"", linhas[0].PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"SalesOrderItem\":\"20\"", linhas[1].PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"Material\":\"7000002\"", linhas[1].PayloadJson, StringComparison.Ordinal);
    }

    // A tela monta o filtro a partir de texto livre; recorte incompleto nao pode virar busca.
    [Theory]
    [InlineData("3000", "3011,3008", "", true)]
    [InlineData("3000", "", "ZVSP", false)]
    [InlineData("", "3011", "ZVSP", false)]
    [InlineData("3000", "3011, 3008", "ZVSP; ZVBI", true)]
    // Tipo fora da lista fechada do fluxo: recusado, nao ignorado.
    [InlineData("3000", "3011", "ZBAL", false)]
    [InlineData("3000", "3011", "ZVSP; TA", false)]
    [InlineData("3000", "3008", "", true)]
    [InlineData("3001", "3011", "", false)]
    [InlineData("3000", "9999", "", false)]
    public void Tela_so_monta_filtro_quando_o_recorte_fecha(string org, string centros, string tipos, bool esperado)
        => Assert.Equal(esperado, FugaTES.WinForms.FrmOrdensVendaSd.Montar(org, centros, tipos) is not null);
}

/// <summary>
/// O recorte que a TELA usa. Em 04/11/2026 o operador pediu que o acervo antigo saisse da vista:
/// eram 268 ordens em situacao A, a maioria de anos atras, escondendo as poucas que vao a
/// balanca. A tela passou a usar o mesmo corte do motor.
/// </summary>
public sealed class RecorteDaTelaOrdensVendaSdTests
{
    private static OrdensVendaSdFiltro Tela()
        => FugaTES.WinForms.FrmOrdensVendaSd.Montar("3000", "3011", "")!;

    [Fact]
    public void A_tela_usa_o_corte_do_motor()
        => Assert.Equal(OrdensVendaSdFiltro.CorteAutomatico, Tela().DataCriacaoMinima);

    [Fact]
    public void O_corte_vai_para_o_SAP_e_nao_e_conferido_so_aqui()
    {
        // No $filter, e nao numa filtragem local: o que nao interessa nem sai do SAP, e o $top
        // deixa de ser gasto com ordem que ninguem vai olhar.
        var query = OrdensVendaSdConsultaAdapter.MontarQuery(Tela());
        Assert.Contains("CreationDate ge datetime'2026-11-01T00:00:00'", query, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tela_tambem_pede_so_os_tipos_do_fluxo()
    {
        var query = OrdensVendaSdConsultaAdapter.MontarQuery(Tela());
        foreach (var tipo in OrdensVendaSdFiltro.TiposPermitidos)
            Assert.Contains($"SalesOrderType eq '{tipo}'", query, StringComparison.Ordinal);
    }
}

/// <summary>
/// O numero do pedido NO CLIENTE (A_SalesOrderItem.PurchaseOrderByCustomer). Pedido do operador
/// em 04/11/2026. E campo de ITEM: dois itens da mesma ordem podem trazer pedidos diferentes, e
/// por isso ele viaja na linha.
/// </summary>
public sealed class PedidoDoClienteSdTests
{
    private const string Resposta = """
    {"d":{"results":[
      {"SalesOrder":"5000001","SalesOrderType":"ZVSP","SalesOrganization":"3000",
       "OverallSDProcessStatus":"A",
       "to_Item":{"results":[
         {"SalesOrderItem":"10","Material":"7000000","ShippingPoint":"3011",
          "PurchaseOrderByCustomer":"49580"},
         {"SalesOrderItem":"20","Material":"7000002","ShippingPoint":"3011",
          "PurchaseOrderByCustomer":"49581"},
         {"SalesOrderItem":"30","Material":"7000009","ShippingPoint":"3011"}]},
       "to_Partner":{"results":[
         {"Customer":"20000777","PartnerFunctionInternalCode":"WE"},
         {"Customer":"20000051","PartnerFunctionInternalCode":"AG"}]}}
    ]}}
    """;

    private static readonly OrdensVendaSdFiltro Filtro =
        new("3000", ["3011"], ["ZVSP"]);

    [Fact]
    public void Cada_item_leva_o_seu_pedido_do_cliente()
    {
        var linhas = OrdensVendaSdConsultaAdapter.LerResposta(Resposta, Filtro);
        Assert.Equal(["49580", "49581", null], linhas.Select(l => l.PedidoDoCliente));
    }

    [Fact]
    public void Item_sem_pedido_do_cliente_nao_bloqueia_a_linha()
    {
        // Nem toda ordem tem: e informacao do cliente. Ausente vira nulo e a linha segue.
        var linhas = OrdensVendaSdConsultaAdapter.LerResposta(Resposta, Filtro);
        var semPedido = Assert.Single(linhas, l => l.LinhaItem == "30");
        Assert.Null(semPedido.PedidoDoCliente);
        Assert.Equal("7000009", semPedido.Material);
    }

    [Fact]
    public void O_pedido_do_cliente_vai_para_o_json_da_linha()
    {
        // A projecao e o que o operador abre na tela para conferir a origem de cada valor.
        var linha = OrdensVendaSdConsultaAdapter.LerResposta(Resposta, Filtro)[0];
        Assert.Contains("\"PurchaseOrderByCustomer\":\"49580\"", linha.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Quando_so_o_cabecalho_traz_o_pedido_ele_vale_para_os_itens()
    {
        const string soNoCabecalho = """
        {"d":{"results":[
          {"SalesOrder":"5000002","SalesOrderType":"ZVSP","SalesOrganization":"3000",
           "OverallSDProcessStatus":"A","PurchaseOrderByCustomer":"77777",
           "to_Item":{"results":[{"SalesOrderItem":"10","Material":"7000000","ShippingPoint":"3011"}]},
           "to_Partner":{"results":[{"Customer":"20000777","PartnerFunctionInternalCode":"WE"}]}}
        ]}}
        """;
        var linha = Assert.Single(OrdensVendaSdConsultaAdapter.LerResposta(soNoCabecalho, Filtro));
        Assert.Equal("77777", linha.PedidoDoCliente);
    }
}

/// <summary>
/// O caso real da ordem 4274, em 04/11/2026: o fluxo foi feito na linha 10, o usuario acrescentou
/// a linha 20 depois, e o FugaTES nao a trouxe.
///
/// Eram duas coisas, e as duas de recorte. O cabecalho virou "C" e a varredura so aceitava "A",
/// entao a ordem INTEIRA sumiu - a linha nova junto. E, com a ordem de volta, faltava distinguir
/// a linha que ja foi da que esta pendente: o campo do cabecalho nao serve para isso, porque a
/// nossa granularidade e a linha.
/// </summary>
public sealed class OrdemComLinhaNovaDepoisDeConcluidaTests
{
    // Cabecalho em "B" - parcialmente processada, que e como o SAP deixa a ordem quando uma linha
    // foi entregue e outra nao. Item 10 concluido, item 20 pendente.
    private const string Resposta = """
    {"d":{"results":[
      {"SalesOrder":"4274","SalesOrderType":"ZVBI","SalesOrganization":"3000",
       "OverallSDProcessStatus":"B","OrganizationDivision":"63",
       "to_Item":{"results":[
         {"SalesOrderItem":"10","Material":"4000561","ShippingPoint":"3008",
          "SDProcessStatus":"C","DeliveryStatus":"C","PurchaseOrderByCustomer":"49580"},
         {"SalesOrderItem":"20","Material":"4000561","ShippingPoint":"3008",
          "SDProcessStatus":"A","DeliveryStatus":"A","PurchaseOrderByCustomer":"49580"}]},
       "to_Partner":{"results":[
         {"Customer":"20000777","PartnerFunctionInternalCode":"WE"},
         {"Customer":"20000217","PartnerFunctionInternalCode":"AG"}]}}
    ]}}
    """;

    private static readonly OrdensVendaSdFiltro Filtro = new("3000", ["3008"], ["ZVBI"]);

    [Fact]
    public void A_linha_nova_aparece()
    {
        var linha = Assert.Single(OrdensVendaSdConsultaAdapter.LerResposta(Resposta, Filtro));
        Assert.Equal("20", linha.LinhaItem);
        Assert.Equal("4274", linha.CodigoOrdemVenda);
    }

    [Fact]
    public void A_linha_ja_concluida_nao_volta()
        // Sem isto, a ordem reaberta traria de novo a linha que ja virou remessa. Quem segurava
        // era so o registro de protocolos, um controle nosso; agora o SAP tambem diz.
        => Assert.DoesNotContain(OrdensVendaSdConsultaAdapter.LerResposta(Resposta, Filtro),
            l => l.LinhaItem == "10");

    [Fact]
    public void Com_todas_as_linhas_concluidas_nao_sobra_nada()
    {
        var tudoFeito = Resposta.Replace("\"SDProcessStatus\":\"A\"", "\"SDProcessStatus\":\"C\"",
            StringComparison.Ordinal);
        Assert.Empty(OrdensVendaSdConsultaAdapter.LerResposta(tudoFeito, Filtro));
    }

    [Fact]
    public void Linha_sem_situacao_nao_e_descartada()
    {
        // Fecha para o lado de MOSTRAR: ausencia do campo nao prova que a linha foi concluida, e
        // esconde-la deixaria a pesagem sem destino. O operador ve e decide.
        var semCampo = Resposta.Replace(",\"SDProcessStatus\":\"A\",\"DeliveryStatus\":\"A\"", "",
            StringComparison.Ordinal);
        Assert.Contains(OrdensVendaSdConsultaAdapter.LerResposta(semCampo, Filtro),
            l => l.LinhaItem == "20");
    }
}
