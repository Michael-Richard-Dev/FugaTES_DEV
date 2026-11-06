using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.Integracao.SAP.BusinessDocuments;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// Passo 5: texto ZNFE. O PATCH substitui o texto inteiro, entao o que estes testes protegem e
/// simples de dizer: nada do que veio da ordem de venda pode sumir ou mudar.
/// </summary>
public sealed class TextoNfeSdTests
{
    /// <summary>Texto real de uma remessa, como o documento da Fuga o mostra (80003849).</summary>
    private const string TextoDaOrdem =
        "VENDEDOR: BIOFUGA BANCO: ABC BANCO\n"
        + "PEDIDO(S):01.1112.25\n"
        + "Contrato Nro: 01.1112.25 Agendamento:\n"
        + "Temperatura do CT:  C Temperatura:  C Densidade:  g/cm\n"
        + "Densidade a 20 C:  Fator Correcao:  LAUDO DE CERTIFICACAO:\n"
        + "Lacres:  N dos envelopes de seguranca das amostrastestemunhas: 65593,65592,65591"
        + " Mot.: JAILSON SOARES CPF: 802.285.930-34\n"
        + "Placas:";

    private static readonly ComplementoTextoNfeSd DaBalanca =
        new(Lacre: "77881", Placa: "COF7595", Motorista: "MARCIO FERENZ", Cpf: "030.641.489-90");

    [Fact]
    public void Preenche_lacres_e_placas_que_vieram_vazios()
    {
        var final = MapeadorTextoNfeSd.Compor(TextoDaOrdem, DaBalanca);
        Assert.Contains("Lacres: 77881 N dos envelopes", final, StringComparison.Ordinal);
        Assert.EndsWith("Placas: COF7595", final, StringComparison.Ordinal);
    }

    /// <summary>
    /// O texto final, inteiro e literal. Este teste existe para que a mudanca de uma virgula na
    /// composicao apareca aqui antes de aparecer numa nota fiscal.
    /// </summary>
    [Fact]
    public void Texto_final_e_exatamente_este()
        => Assert.Equal(
            "VENDEDOR: BIOFUGA BANCO: ABC BANCO\n"
            + "PEDIDO(S):01.1112.25\n"
            + "Contrato Nro: 01.1112.25 Agendamento:\n"
            + "Temperatura do CT:  C Temperatura:  C Densidade:  g/cm\n"
            + "Densidade a 20 C:  Fator Correcao:  LAUDO DE CERTIFICACAO:\n"
            + "Lacres: 77881 N dos envelopes de seguranca das amostrastestemunhas: 65593,65592,65591"
            + " Mot.: JAILSON SOARES CPF: 802.285.930-34\n"
            + "Placas: COF7595",
            MapeadorTextoNfeSd.Compor(TextoDaOrdem, DaBalanca));

    [Fact]
    public void Nao_reescreve_o_que_a_ordem_ja_preencheu()
    {
        // Motorista e CPF ja vieram da ordem; o que veio do negocio vale mais que o da balanca.
        var final = MapeadorTextoNfeSd.Compor(TextoDaOrdem, DaBalanca);
        Assert.Contains("Mot.: JAILSON SOARES CPF: 802.285.930-34", final, StringComparison.Ordinal);
        Assert.DoesNotContain("MARCIO FERENZ", final, StringComparison.Ordinal);
        Assert.DoesNotContain("030.641.489-90", final, StringComparison.Ordinal);
    }

    [Fact]
    public void Nenhuma_linha_da_ordem_se_perde()
    {
        var final = MapeadorTextoNfeSd.Compor(TextoDaOrdem, DaBalanca);
        foreach (var pedaco in new[]
        {
            "VENDEDOR: BIOFUGA", "BANCO: ABC BANCO", "PEDIDO(S):01.1112.25",
            "Contrato Nro: 01.1112.25", "LAUDO DE CERTIFICACAO:",
            "65593,65592,65591"
        })
            Assert.Contains(pedaco, final, StringComparison.Ordinal);

        Assert.Equal(TextoDaOrdem.Split('\n').Length, final.Split('\n').Length);
    }

    [Fact]
    public void O_texto_so_cresce()
    {
        var final = MapeadorTextoNfeSd.Compor(TextoDaOrdem, DaBalanca);
        Assert.True(final.Length > TextoDaOrdem.Length);
    }

    [Fact]
    public void Compor_duas_vezes_nao_duplica_o_que_ja_entrou()
    {
        // Segunda tentativa depois de uma falha de rede: o espaco ja esta preenchido.
        var uma = MapeadorTextoNfeSd.Compor(TextoDaOrdem, DaBalanca);
        Assert.Equal(uma, MapeadorTextoNfeSd.Compor(uma, DaBalanca));
    }

    [Fact]
    public void Rotulo_ausente_vira_linha_ao_fim_em_vez_de_sumir()
    {
        var final = MapeadorTextoNfeSd.Compor("Contrato Nro: 01.1112.25", new(Lacre: "77881"));
        Assert.Equal("Contrato Nro: 01.1112.25\nLacres: 77881", final);
    }

    [Fact]
    public void Sem_texto_na_remessa_escreve_so_o_da_balanca()
        => Assert.Equal("Lacres: 77881\nPlacas: COF7595",
            MapeadorTextoNfeSd.Compor(null, new(Lacre: "77881", Placa: "COF7595")));

    [Fact]
    public void Sem_nada_a_acrescentar_o_texto_nao_e_tocado()
        => Assert.Equal("SD_TEXTO_SEM_COMPLEMENTO", Assert.Throws<RemessaSdBloqueadaException>(
            () => MapeadorTextoNfeSd.Compor(TextoDaOrdem, new())).Codigo);

    [Fact]
    public void Valor_em_branco_nao_conta_como_valor()
        => Assert.Equal("SD_TEXTO_SEM_COMPLEMENTO", Assert.Throws<RemessaSdBloqueadaException>(
            () => MapeadorTextoNfeSd.Compor(TextoDaOrdem, new(Lacre: "   ", Placa: ""))).Codigo);

    // --- Leitura do texto atual --------------------------------------------------------------

    private const string RespostaTexto = """
    {"d":{"results":[{"__metadata":{"etag":"W/\"'0003'\""},"DeliveryDocument":"80003849",
      "TextElement":"ZNFE","Language":"PT","TextElementText":"Placas:"}]}}
    """;

    [Fact]
    public void Le_o_znfe_em_portugues_com_etag()
    {
        var atual = MapeadorTextoNfeSd.LerTextoAtual(RespostaTexto);
        Assert.True(atual.Existe);
        Assert.Equal("Placas:", atual.Texto);
        Assert.Equal("W/\"'0003'\"", atual.ETag);
    }

    [Theory]
    // Outro elemento vai para outro lugar da NF-e: nao serve.
    [InlineData("""{"d":{"results":[{"TextElement":"Z001","Language":"PT","TextElementText":"x"}]}}""")]
    [InlineData("""{"d":{"results":[]}}""")]
    [InlineData("")]
    [InlineData("nao e json")]
    public void Sem_znfe_o_texto_nao_existe(string corpo)
        => Assert.False(MapeadorTextoNfeSd.LerTextoAtual(corpo).Existe);

    [Fact]
    public void Texto_vazio_existe_e_leva_a_patch_nao_a_post()
    {
        // "" e diferente de ausente: a entidade ja esta la, e POST devolveria duplicidade.
        var atual = MapeadorTextoNfeSd.LerTextoAtual(
            """{"d":{"results":[{"TextElement":"ZNFE","Language":"PT","TextElementText":""}]}}""");
        Assert.True(atual.Existe);
        Assert.Equal("", atual.Texto);
    }

    // --- Complemento vindo do documento do Guardian -------------------------------------------

    [Fact]
    public void Complemento_sai_do_documento_recebido()
    {
        // O lacre vem da "Observacao" desde 05/11/2026; o 1003 segue sendo a ordem, por numero.
        var complemento = MapeadorTextoNfeSd.LerComplemento("""
        {"Placa":"COF7595","NomeMotorista":"MARCIO FERENZ","CpfMotorista":"03064148990",
         "Observacao":"77881",
         "camposAdicionais":[{"numero":1003,"valor":"2985"}]}
        """);
        Assert.Equal("77881", complemento.Lacre);
        Assert.Equal("COF7595", complemento.Placa);
        Assert.Equal("MARCIO FERENZ", complemento.Motorista);
        Assert.Equal("03064148990", complemento.Cpf);
    }

    [Fact]
    public void Lacre_tambem_e_aceito_por_nome()
        => Assert.Equal("77881", MapeadorTextoNfeSd.LerComplemento(
            """{"camposAdicionais":[{"nome":"Lacre","valor":"77881"}]}""").Lacre);

    [Fact]
    public void Documento_sem_lacre_nao_inventa_lacre()
        => Assert.Null(MapeadorTextoNfeSd.LerComplemento(
            """{"Placa":"COF7595","camposAdicionais":[{"numero":1003,"valor":"2985"}]}""").Lacre);

    [Theory]
    [InlineData("")]
    [InlineData("nao e json")]
    [InlineData("[]")]
    public void Documento_ilegivel_nao_vira_complemento(string payload)
        => Assert.False(MapeadorTextoNfeSd.LerComplemento(payload).TemAlgo);

    // --- Rotas ---------------------------------------------------------------------------------

    [Fact]
    public void Rota_da_entidade_tem_elemento_e_idioma()
        => Assert.EndsWith(
            "/A_OutbDeliveryHeaderText(DeliveryDocument='80003849',TextElement='ZNFE',Language='PT')",
            TextoNfeSdSapClient.RotaEntidade("80003849"), StringComparison.Ordinal);

    [Fact]
    public void Rota_da_navegacao_parte_do_cabecalho()
        => Assert.EndsWith("/A_OutbDeliveryHeader('80003849')/to_DeliveryDocumentText",
            TextoNfeSdSapClient.RotaNavegacao("80003849"), StringComparison.Ordinal);

    [Theory]
    [InlineData("80003849')/x?y=('")]
    [InlineData("abc")]
    public void Chave_que_nao_e_numero_nao_entra_na_rota(string numero)
        => Assert.Throws<ArgumentException>(() => TextoNfeSdSapClient.RotaEntidade(numero));

    // --- Guarda HTTP ---------------------------------------------------------------------------

    private sealed class Contador : HttpMessageHandler
    {
        public int Chamadas { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Chamadas++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        }
    }

    private const string Origem = "https://vhfufds4ci.sap.fugacouros.com.br:44300";

    private static async Task<bool> Passa(HttpMethod metodo, string caminho)
    {
        using var client = new HttpClient(new FugaTES.WinForms.Sd.RemessaSdHttpGuard(new Contador(), FugaTES.Aplicacao.Configuracao.AmbientesSap.Dev110.Origem));
        using var pedido = new HttpRequestMessage(metodo, Origem + caminho);
        try { using var _ = await client.SendAsync(pedido, CancellationToken.None); return true; }
        catch (InvalidOperationException) { return false; }
        catch (HttpRequestException erro) when (erro.InnerException is InvalidOperationException)
        { return false; }
    }

    [Fact]
    public async Task Get_e_post_na_navegacao_do_texto_sao_liberados()
    {
        Assert.True(await Passa(HttpMethod.Get, TextoNfeSdSapClient.RotaNavegacao("80003849")));
        Assert.True(await Passa(HttpMethod.Post, TextoNfeSdSapClient.RotaNavegacao("80003849")));
    }

    [Fact]
    public async Task Patch_na_entidade_do_texto_e_liberado()
        => Assert.True(await Passa(HttpMethod.Patch, TextoNfeSdSapClient.RotaEntidade("80003849")));

    [Theory]
    // Apagar um texto nao faz parte de nenhum passo.
    [InlineData("DELETE", "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryHeaderText(DeliveryDocument='80003849',TextElement='ZNFE',Language='PT')")]
    // PATCH na colecao de textos nao tem alvo definido.
    [InlineData("PATCH", "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryHeaderText")]
    // Navegacao de texto so a partir de UMA remessa, nunca da colecao.
    [InlineData("POST", "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryHeader/to_DeliveryDocumentText")]
    public async Task Rota_de_texto_fora_do_passo_5_nao_sai(string metodo, string caminho)
        => Assert.False(await Passa(new HttpMethod(metodo), caminho));
}

/// <summary>
/// A placa da CARRETA no texto da NF-e, pedida pelo operador em 04/11/2026. Vai junto com a do
/// veiculo, no mesmo rotulo "Placas:" - que ja nasce no plural no texto da ordem de venda.
/// </summary>
public sealed class PlacaDaCarretaNoTextoSdTests
{
    [Fact]
    public void As_duas_placas_vao_juntas()
        => Assert.Equal("ABC1234 / XYZ5678",
            new ComplementoTextoNfeSd(Placa: "ABC1234", PlacaCarreta: "XYZ5678").Placas);

    [Fact]
    public void So_o_veiculo_vai_sozinho()
        => Assert.Equal("ABC1234", new ComplementoTextoNfeSd(Placa: "ABC1234").Placas);

    [Fact]
    public void So_a_carreta_tambem_vai_sozinha()
        // Perder o dado seria pior do que mostra-lo incompleto.
        => Assert.Equal("XYZ5678", new ComplementoTextoNfeSd(PlacaCarreta: "XYZ5678").Placas);

    [Fact]
    public void Placa_repetida_nao_vira_duas()
        => Assert.Equal("ABC1234",
            new ComplementoTextoNfeSd(Placa: "ABC1234", PlacaCarreta: "abc1234").Placas);

    [Fact]
    public void Sem_placa_nenhuma_nao_ha_o_que_escrever()
        => Assert.Null(new ComplementoTextoNfeSd(Motorista: "FULANO").Placas);

    [Fact]
    public void A_carreta_sozinha_ja_e_motivo_para_compor()
        => Assert.True(new ComplementoTextoNfeSd(PlacaCarreta: "XYZ5678").TemAlgo);

    [Fact]
    public void O_texto_final_traz_as_duas()
    {
        var texto = MapeadorTextoNfeSd.Compor(null,
            new ComplementoTextoNfeSd(Placa: "ABC1234", PlacaCarreta: "XYZ5678"));
        Assert.Contains("Placas: ABC1234 / XYZ5678", texto, StringComparison.Ordinal);
    }

    [Fact]
    public void O_espaco_vazio_da_ordem_recebe_as_duas()
    {
        // Regra 1: nada do texto existente e removido. As placas entram no espaco que a ordem
        // deixou em branco.
        const string daOrdem = "VENDEDOR: BIOFUGA BANCO: ABC\nLacres: 123 Mot.: JOAO CPF: 111\nPlacas:";
        var texto = MapeadorTextoNfeSd.Compor(daOrdem,
            new ComplementoTextoNfeSd(Placa: "ABC1234", PlacaCarreta: "XYZ5678"));
        Assert.Contains("ABC1234 / XYZ5678", texto, StringComparison.Ordinal);
        Assert.Contains("VENDEDOR: BIOFUGA", texto, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"Placa":"ABC1234","PlacaCarreta":"XYZ5678"}""")]
    [InlineData("""{"Placa":"ABC1234","PlacaReboque":"XYZ5678"}""")]
    [InlineData("""{"Placa":"ABC1234","camposAdicionais":[{"nome":"Placa da Carreta","valor":"XYZ5678"}]}""")]
    public void A_leitura_aceita_as_formas_plausiveis(string payload)
        // O nome do campo na devolutiva ainda nao foi visto em payload real - ver PlacaCarreta.
        => Assert.Equal("XYZ5678", MapeadorTextoNfeSd.LerComplemento(payload).PlacaCarreta);

    [Fact]
    public void Sem_a_carreta_o_texto_sai_como_antes()
    {
        // Nao encontrar o campo nao pode quebrar o passo 5: sai so a placa do veiculo.
        var complemento = MapeadorTextoNfeSd.LerComplemento("""{"Placa":"ABC1234"}""");
        Assert.Null(complemento.PlacaCarreta);
        Assert.Equal("ABC1234", complemento.Placas);
    }
}
