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
        => new(org, centros ?? ["3011", "3008"], tipos ?? ["ZBAL"], top);

    // Lista de tipos vazia significa TODOS, enquanto o consultor SAP nao confirma quais sao "pra
    // balanca". O volume fica contido pela organizacao e pelo maximo por varredura.
    [Fact]
    public void Sem_tipo_de_documento_a_varredura_traz_todos()
    {
        Assert.True(Filtro(tipos: []).EhValido());
        var query = OrdensVendaSdConsultaAdapter.MontarQuery(Filtro(tipos: []));
        Assert.Equal("$filter=SalesOrganization eq '3000' and OverallSDProcessStatus eq 'A'"
            + "&$expand=to_Item,to_Partner&$orderby=SalesOrder desc&$top=200", query);
        Assert.DoesNotContain("SalesOrderType", query, StringComparison.Ordinal);
    }

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
        var query = OrdensVendaSdConsultaAdapter.MontarQuery(Filtro(tipos: ["ZBAL", "TA"]));
        Assert.Contains("$filter=SalesOrganization eq '3000'", query, StringComparison.Ordinal);
        Assert.Contains("SalesOrderType eq 'ZBAL' or SalesOrderType eq 'TA'", query, StringComparison.Ordinal);
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
    // seria descartado.
    [Fact]
    public void Situacao_da_ordem_entra_na_query()
        => Assert.Contains("and OverallSDProcessStatus eq 'A'",
            OrdensVendaSdConsultaAdapter.MontarQuery(Filtro()), StringComparison.Ordinal);

    // Se o servico ignorar a clausula e mandar outra situacao, a linha ainda nao pode aparecer.
    [Theory]
    [InlineData("\"OverallSDProcessStatus\":\"B\"")]
    [InlineData("\"OverallSDProcessStatus\":\"C\"")]
    [InlineData("\"OverallSDProcessStatus\":\"\"")]
    public void Ordem_com_outra_situacao_e_descartada(string situacao)
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
             "OverallSDProcessStatus", "SalesOrderType", "SalesOrganization", "OrganizationDivision",
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
    [InlineData("3000", "", "ZBAL", false)]
    [InlineData("", "3011", "ZBAL", false)]
    [InlineData("3000", "3011, 3008", "ZBAL; TA", true)]
    [InlineData("3000", "3008", "", true)]
    [InlineData("3001", "3011", "", false)]
    [InlineData("3000", "9999", "", false)]
    public void Tela_so_monta_filtro_quando_o_recorte_fecha(string org, string centros, string tipos, bool esperado)
        => Assert.Equal(esperado, FugaTES.WinForms.FrmOrdensVendaSd.Montar(org, centros, tipos) is not null);
}
