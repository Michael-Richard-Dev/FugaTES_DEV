using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;

namespace R158.SapInboundReadiness.Tests;

// SD: o protocolo de pre-cadastro montado a partir de UMA linha de ordem de venda. Estes testes
// fixam o mapeamento campo a campo, a normalizacao dos codigos para a forma do Guardian e os
// motivos de bloqueio - nada aqui contata o Guardian.
public sealed class ProtocoloPreCadastroSdTests
{
    private const string Retorno = "https://webhook.site/717543aa-e052-4a2d-95b1-53983c881c1e";

    private static OrdemVendaSdLeitura Linha(
        string ordem = "0000005000001", string linha = "000010",
        string material = "000000000007000002", string? emissor = "0020000610",
        string centro = "3011", string? recebedor = "0020000733")
        => new(centro, ordem, material, linha, "A", "ZBAL", "3000", emissor, "01", recebedor, "{}");

    private static JsonElement Json(ProtocoloPreCadastroSdRequest pedido)
        => JsonDocument.Parse(MapeadorProtocoloSd.Serializar(pedido)).RootElement;

    private static string? Campo(JsonElement raiz, int numero)
        => raiz.GetProperty("dadosProtocoloPreCadastro").GetProperty("camposAdicionais")
            .EnumerateArray().Single(c => c.GetProperty("numero").GetInt32() == numero)
            .GetProperty("valor").GetString();

    // O exemplo do operador: ordem 1, tres linhas, mesmo item e mesmo emissor - so 1009 muda.
    [Fact]
    public void Cada_linha_da_mesma_ordem_vira_um_protocolo_distinto()
    {
        var protocolos = new[] { "000010", "000020", "000030" }
            .Select(l => Json(MapeadorProtocoloSd.Montar(Linha(linha: l), Retorno))).ToArray();

        Assert.All(protocolos, p => Assert.Equal("5000001", Campo(p, CamposProtocoloSd.OrdemVenda)));
        // Mesmo material nas tres linhas: o item NAO distingue os protocolos.
        Assert.All(protocolos, p => Assert.Equal("7000002",
            p.GetProperty("dadosProtocoloPreCadastro").GetProperty("codigoItem").GetString()));
        Assert.Equal(["10", "20", "30"],
            protocolos.Select(p => Campo(p, CamposProtocoloSd.LinhaDaOrdem)));
    }

    [Fact]
    public void Mapeamento_campo_a_campo_segue_o_combinado()
    {
        var raiz = Json(MapeadorProtocoloSd.Montar(Linha(), Retorno));
        var dados = raiz.GetProperty("dadosProtocoloPreCadastro");

        Assert.Equal(Retorno, raiz.GetProperty("configuracao").GetProperty("urlParaRetorno").GetString());
        Assert.Equal(0, dados.GetProperty("id").GetInt32());
        Assert.Equal("7000002", dados.GetProperty("codigoItem").GetString());
        // O valor e o RECEBEDOR da mercadoria, embora o campo do Guardian se chame codigoEmissor.
        Assert.Equal("20000733", dados.GetProperty("codigoEmissor").GetString());

        Assert.Equal("S", Campo(raiz, CamposProtocoloSd.TipoDocumento));
        Assert.Equal("3011", Campo(raiz, CamposProtocoloSd.Centro));
        Assert.Equal("0", Campo(raiz, CamposProtocoloSd.Operacao));
        Assert.Equal("5000001", Campo(raiz, CamposProtocoloSd.OrdemVenda));
        Assert.Equal("0", Campo(raiz, CamposProtocoloSd.Remessa));
        Assert.Equal("0", Campo(raiz, CamposProtocoloSd.PedidoCompra));
        Assert.Equal("0", Campo(raiz, CamposProtocoloSd.Lacre));
        Assert.Equal("0", Campo(raiz, CamposProtocoloSd.Silo));
        Assert.Equal("0", Campo(raiz, CamposProtocoloSd.Quadrante));
        Assert.Equal("10", Campo(raiz, CamposProtocoloSd.LinhaDaOrdem));
    }

    // Dez campos: nem um a mais (o Guardian recusaria), nem um a menos.
    [Fact]
    public void Sao_exatamente_os_dez_campos_do_fluxo()
    {
        var raiz = Json(MapeadorProtocoloSd.Montar(Linha(), Retorno));
        var numeros = raiz.GetProperty("dadosProtocoloPreCadastro").GetProperty("camposAdicionais")
            .EnumerateArray().Select(c => c.GetProperty("numero").GetInt32()).ToArray();
        Assert.Equal(CamposProtocoloSd.Todos, numeros);
        Assert.Equal(10, numeros.Length);
    }

    // Os campos nao usados vao como TEXTO "0": o campo valor do Guardian e string.
    [Fact]
    public void Campos_nao_usados_vao_como_texto()
    {
        var json = MapeadorProtocoloSd.Serializar(MapeadorProtocoloSd.Montar(Linha(), Retorno));
        Assert.Contains("\"valor\":\"0\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"valor\":0", json, StringComparison.Ordinal);
    }

    // O protocolo nao carrega nada alem do combinado - contrato, fluxo, transportadora e agenda
    // ficam de fora, e o fluxo o usuario informa manualmente no Guardian.
    //
    // "numeroReferencia" saiu desta lista em 04/11/2026: passou a ser pedido, com o formato
    // OV<ordem>-<linha>-<pedido do cliente>. Ver ReferenciaProtocoloSdTests.
    [Fact]
    public void Nao_envia_campo_que_nao_foi_pedido()
    {
        var json = MapeadorProtocoloSd.Serializar(MapeadorProtocoloSd.Montar(Linha(), Retorno));
        foreach (var ausente in new[]
                 { "codigoFluxo", "codigoContrato", "codigoTransportadora", "codigoMotorista",
                   "idAgenda", "placaVeiculo", "documentos", "dataAgendamento" })
            Assert.DoesNotContain(ausente, json, StringComparison.Ordinal);
    }

    // Normalizacao conferida contra o cadastro do proprio Guardian (ITM_CODIGO / EMS_CODIGO).
    [Theory]
    [InlineData("000000000007000002", "7000002")]
    [InlineData("0007000000", "7000000")]
    [InlineData("0020000610", "20000610")]
    [InlineData("20000051", "20000051")]
    [InlineData("0000000001", "1")]
    [InlineData("54", "54")]
    public void Zeros_a_esquerda_viram_o_codigo_do_guardian(string sap, string esperado)
        => Assert.Equal(esperado, MapeadorProtocoloSd.Normalizar(sap));

    // Encurtar um codigo alfanumerico seria inventar outro codigo.
    [Theory]
    [InlineData("0ABC", "0ABC")]
    [InlineData("00A1", "00A1")]
    public void Codigo_nao_numerico_passa_inteiro(string sap, string esperado)
        => Assert.Equal(esperado, MapeadorProtocoloSd.Normalizar(sap));

    [Fact]
    public void Codigo_so_de_zeros_continua_zero()
        => Assert.Equal("0", MapeadorProtocoloSd.Normalizar("0000"));

    // A chave de duplicidade usa a forma JA normalizada: foi comparar "1160" com "0000001160" que
    // deixou a guarda do ACM sem efeito.
    [Fact]
    public void Chave_natural_nao_depende_do_preenchimento_com_zeros()
        => Assert.Equal(
            MapeadorProtocoloSd.ChaveNatural(Linha(ordem: "5000001", linha: "10")),
            MapeadorProtocoloSd.ChaveNatural(Linha(ordem: "0000005000001", linha: "000010")));

    [Fact]
    public void Chave_natural_separa_linhas_da_mesma_ordem()
        => Assert.NotEqual(
            MapeadorProtocoloSd.ChaveNatural(Linha(linha: "000010")),
            MapeadorProtocoloSd.ChaveNatural(Linha(linha: "000020")));

    private static readonly CadastroGuardianConhecido Cadastro =
        new(Emissores: ["20000610", "20000051", "20000184", "20000733"],
            Itens: ["7000000", "7000002", "60035", "7772"]);

    [Fact]
    public void Linha_com_codigos_cadastrados_passa()
        => Assert.NotNull(MapeadorProtocoloSd.Montar(Linha(), Retorno, Cadastro));

    [Fact]
    public void Item_fora_do_cadastro_do_guardian_bloqueia_a_linha()
    {
        var erro = Assert.Throws<ProtocoloSdBloqueadoException>(
            () => MapeadorProtocoloSd.Montar(Linha(material: "0009999999"), Retorno, Cadastro));
        Assert.Equal("SD_ITEM_NAO_CADASTRADO", erro.Codigo);
        Assert.Contains("9999999", erro.Detalhe, StringComparison.Ordinal);
    }

    [Fact]
    public void Recebedor_fora_do_cadastro_do_guardian_bloqueia_a_linha()
        => Assert.Equal("SD_RECEBEDOR_NAO_CADASTRADO", Assert.Throws<ProtocoloSdBloqueadoException>(
            () => MapeadorProtocoloSd.Montar(Linha(recebedor: "0020009999"), Retorno, Cadastro)).Codigo);

    // O emissor deixou de participar: nao vai no protocolo e nao e conferido. Um emissor fora do
    // cadastro do Guardian nao bloqueia mais nada - quem precisa existir la e o recebedor.
    [Fact]
    public void Emissor_fora_do_cadastro_nao_bloqueia_mais()
        => Assert.NotNull(MapeadorProtocoloSd.Montar(Linha(emissor: "0020009999"), Retorno, Cadastro));

    // Lista vazia significa "nao sei", e nao "pode tudo": sem cadastro conhecido a conferencia
    // simplesmente nao acontece e quem decide e o Guardian, no POST.
    [Fact]
    public void Sem_cadastro_conhecido_a_conferencia_nao_acontece()
        => Assert.NotNull(MapeadorProtocoloSd.Montar(Linha(material: "0009999999"), Retorno));

    [Theory]
    [InlineData(null, null, "SD_ITEM_AUSENTE")]
    [InlineData("", null, "SD_ITEM_AUSENTE")]
    public void Linha_sem_material_nao_vira_protocolo(string? material, string? _, string codigo)
        => Assert.Equal(codigo, Assert.Throws<ProtocoloSdBloqueadoException>(
            () => MapeadorProtocoloSd.Montar(Linha() with { Material = material }, Retorno)).Codigo);

    [Fact]
    public void Linha_sem_recebedor_nao_vira_protocolo()
        => Assert.Equal("SD_RECEBEDOR_AUSENTE", Assert.Throws<ProtocoloSdBloqueadoException>(
            () => MapeadorProtocoloSd.Montar(Linha(recebedor: null), Retorno)).Codigo);

    [Fact]
    public void Linha_sem_emissor_continua_virando_protocolo()
        // O emissor saiu do protocolo em 04/11/2026; faltar nao bloqueia mais.
        => Assert.NotNull(MapeadorProtocoloSd.Montar(Linha(emissor: null), Retorno));

    /// <summary>
    /// O caso que motivou a troca: a ordem 4274, em que emissor e recebedor sao PESSOAS
    /// DIFERENTES. Ate 04/11/2026 viajava o emissor, e o protocolo nascia com a pessoa errada.
    /// Nas ordens em que os dois coincidem - a maioria - o erro nao aparecia.
    /// </summary>
    [Fact]
    public void Quando_emissor_e_recebedor_diferem_vai_o_recebedor()
    {
        var raiz = Json(MapeadorProtocoloSd.Montar(
            Linha(emissor: "0020000610", recebedor: "0020000733"), Retorno));
        var codigo = raiz.GetProperty("dadosProtocoloPreCadastro")
            .GetProperty("codigoEmissor").GetString();
        Assert.Equal("20000733", codigo);
        Assert.NotEqual("20000610", codigo);
    }

    [Fact]
    public void Centro_fora_do_fluxo_nao_vira_protocolo()
        => Assert.Equal("SD_CENTRO_FORA_DO_FLUXO", Assert.Throws<ProtocoloSdBloqueadoException>(
            () => MapeadorProtocoloSd.Montar(Linha(centro: "9999"), Retorno)).Codigo);

    [Fact]
    public void Centro_3008_tambem_e_aceito()
        => Assert.Equal("3008", Campo(Json(MapeadorProtocoloSd.Montar(Linha(centro: "3008"), Retorno)),
            CamposProtocoloSd.Centro));

    // A url de retorno recebe dados de pesagem: sem TLS e sem credencial embutida.
    [Theory]
    [InlineData("http://webhook.site/717543aa")]
    [InlineData("https://user:senha@webhook.site/717543aa")]
    [InlineData("nao-e-url")]
    [InlineData("")]
    public void Url_de_retorno_insegura_bloqueia_o_protocolo(string url)
        => Assert.Equal("SD_URL_RETORNO_INVALIDA", Assert.Throws<ProtocoloSdBloqueadoException>(
            () => MapeadorProtocoloSd.Montar(Linha(), url)).Codigo);
}

/// <summary>
/// A referencia do protocolo, no formato combinado com o operador em 04/11/2026:
/// "OV" + ordem + "-" + linha + "-" + pedido do cliente. Exemplo dado por ele: OV4274-10-XXXXXXX.
///
/// E o que identifica o protocolo para quem esta na balanca, onde nao ha a tela do SAP ao lado.
/// </summary>
public sealed class ReferenciaProtocoloSdTests
{
    private const string Retorno = "https://webhook.site/717543aa-e052-4a2d-95b1-53983c881c1e";

    private static OrdemVendaSdLeitura Linha(string? pedido) =>
        new("3011", "0000004274", "000000000007000002", "000010", "A", "ZVSP", "3000",
            "0020000610", "01", "0020000733", "{}")
        { PedidoDoCliente = pedido };

    private static string Referencia(string? pedido)
        => MapeadorProtocoloSd.Montar(Linha(pedido), Retorno)
            .DadosProtocoloPreCadastro.NumeroReferencia;

    [Fact]
    public void O_formato_e_o_do_exemplo_do_operador()
        => Assert.Equal("OV4274-10-49580", Referencia("49580"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sem_pedido_do_cliente_a_referencia_para_na_linha(string? pedido)
        // Melhor "OV4274-10" do que "OV4274-10-": hifen solto na balanca parece dado perdido.
        => Assert.Equal("OV4274-10", Referencia(pedido));

    [Fact]
    public void Ordem_e_linha_vao_sem_os_zeros_do_SAP()
        // O operador le "4274" e "10" na tela do SAP; a referencia mostra o mesmo, e nao
        // "OV0000004274-000010".
        => Assert.DoesNotContain("0000004274", Referencia("49580"), StringComparison.Ordinal);

    [Fact]
    public void A_referencia_vai_no_json_enviado_ao_guardian()
    {
        var json = MapeadorProtocoloSd.Serializar(MapeadorProtocoloSd.Montar(Linha("49580"), Retorno));
        Assert.Contains("\"numeroReferencia\":\"OV4274-10-49580\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Montagem_direta_segue_a_mesma_regra()
    {
        Assert.Equal("OV4274-10-49580", MapeadorProtocoloSd.Referencia("4274", "10", "49580"));
        Assert.Equal("OV4274-10", MapeadorProtocoloSd.Referencia("4274", "10", null));
    }
}
