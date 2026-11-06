using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// OS DOIS TIPOS DE LACRE, e a quebra de linha que diz qual e qual.
///
/// Testemunha tambem e lacre - muda a nomenclatura, nao a natureza: e o lacre do envelope de
/// seguranca da amostra testemunha, e a NF-e tem um espaco proprio para ele, ao lado do espaco
/// dos lacres de carga.
///
/// O Guardian nao tem campo separado para os dois, entao o operador pede ao usuario da balanca
/// que de um Enter entre eles dentro da "Observacao". PRIMEIRA linha e carga, da SEGUNDA em
/// diante e testemunha. A quebra nao e ruido de digitacao: e a unica informacao que distingue
/// os dois tipos, e foi colocada ali de proposito.
///
/// Em 05/11/2026 esta leitura chegou a JUNTAR as duas linhas num "Lacres:" so, e a nota saia com
/// os lacres das testemunhas no campo dos lacres de carga. Os testes abaixo existem para que
/// isso nao volte.
/// </summary>
public sealed class TextoNfeSdVariosLacresTests
{
    private static string Payload(string observacao)
        => "{\"Observacao\":\"" + observacao + "\"}";

    /// <summary>Linhas, juntadas com a quebra ESCAPADA, como o JSON exige.</summary>
    private static string Linhas(params string[] linhas) => Payload(string.Join("\\r\\n", linhas));

    private static ComplementoTextoNfeSd Ler(string json) => MapeadorTextoNfeSd.LerComplemento(json);

    // --- quem e carga e quem e testemunha ----------------------------------------------------

    [Fact]
    public void A_primeira_linha_e_carga_e_a_segunda_e_testemunha()
    {
        var c = Ler(Linhas("134955,134956,134957,134958", "65593,65592,65591"));

        Assert.Equal("134955,134956,134957,134958", c.Lacre);
        Assert.Equal("65593,65592,65591", c.Testemunha);
    }

    [Fact]
    public void Uma_linha_so_e_carga_e_nao_ha_testemunha()
    {
        var c = Ler(Payload("134955,134956"));

        Assert.Equal("134955,134956", c.Lacre);
        Assert.Null(c.Testemunha);
    }

    /// <summary>
    /// Da terceira linha em diante tudo vai junto com a testemunha. Nao e o formato previsto -
    /// sao duas linhas -, mas descartar perderia lacre em silencio.
    /// </summary>
    [Fact]
    public void Da_terceira_linha_em_diante_tudo_e_testemunha()
    {
        var c = Ler(Linhas("A", "B", "C"));

        Assert.Equal("A", c.Lacre);
        Assert.Equal("B, C", c.Testemunha);
    }

    /// <summary>
    /// Linha em branco ou zerada some ANTES de contar os tipos. Sem isso, um CRLF, uma linha
    /// vazia no meio ou um "0" sobrando empurrariam a testemunha e ela viraria carga - ou pior,
    /// a carga viraria testemunha.
    /// </summary>
    [Theory]
    [InlineData("CARGA", "", "TESTEMUNHA")]
    [InlineData("CARGA", "0", "TESTEMUNHA")]
    [InlineData("", "CARGA", "TESTEMUNHA")]
    [InlineData("CARGA", "TESTEMUNHA", "")]
    [InlineData("CARGA", "TESTEMUNHA", "000")]
    public void Linha_vazia_ou_zerada_nao_desloca_os_tipos(params string[] linhas)
    {
        var c = Ler(Linhas(linhas));

        Assert.Equal("CARGA", c.Lacre);
        Assert.Equal("TESTEMUNHA", c.Testemunha);
    }

    [Fact]
    public void Observacao_toda_zerada_nao_rende_lacre_nenhum()
    {
        var c = Ler(Linhas("0", "", "000"));

        Assert.Null(c.Lacre);
        Assert.Null(c.Testemunha);
    }

    [Theory]
    [InlineData("\\n")]
    [InlineData("\\r")]
    [InlineData("\\r\\n")]
    public void Qualquer_quebra_de_linha_separa_os_tipos(string quebra)
    {
        var c = Ler(Payload("148594" + quebra + "59187"));

        Assert.Equal("148594", c.Lacre);
        Assert.Equal("59187", c.Testemunha);
    }

    // --- o valor dentro de cada linha --------------------------------------------------------

    /// <summary>
    /// So a quebra de linha separa. Virgula, ponto-e-virgula, barra e espaco atravessam intactos:
    /// e o contrato de antes desta mudanca, e trocar isso teria reescrito uma decisao que ninguem
    /// pediu para rever.
    /// </summary>
    [Theory]
    [InlineData("15265,12658,12698,13654")]
    [InlineData("15265; 12658; 12698")]
    [InlineData("15265 / 12658 / 12698")]
    [InlineData("15265 12658")]
    public void Dentro_de_uma_linha_o_separador_nao_e_interpretado(string lacres)
        => Assert.Equal(lacres, Ler(Payload(lacres)).Lacre);

    // Zero a esquerda continua sendo lacre - so o valor inteiramente zerado e descartado.
    [Fact]
    public void Lacre_com_zero_a_esquerda_fica()
        => Assert.Equal("0150059", Ler(Payload("0150059")).Lacre);

    [Fact]
    public void Linha_repetida_dentro_do_tipo_entra_uma_vez_so()
        => Assert.Equal("B", Ler(Linhas("A", "B", "B")).Testemunha);

    /// <summary>
    /// Entre tipos NAO se compara. O mesmo numero nos dois e coincidencia entre coisas
    /// diferentes - um lacre de carga e um de envelope -, e apagar um deles esconderia dado.
    /// </summary>
    [Fact]
    public void O_mesmo_numero_nos_dois_tipos_fica_nos_dois()
    {
        var c = Ler(Linhas("65593", "65593"));

        Assert.Equal("65593", c.Lacre);
        Assert.Equal("65593", c.Testemunha);
    }

    // --- o que NAO e lacre -------------------------------------------------------------------

    [Fact]
    public void A_transportadora_e_a_carreta_nao_viram_lacre()
    {
        var c = Ler("""
            {
              "IdTransportadora": "14300001",
              "NomeTransportadora": "Transporte Jabuti",
              "Placa Carreta": "ADV9874",
              "Observacao": "0150059\r\n0150060"
            }
            """);

        Assert.Equal("0150059", c.Lacre);
        Assert.Equal("0150060", c.Testemunha);
        Assert.Equal("14300001 - Transporte Jabuti", c.Transportadora);
        Assert.Equal("ADV9874", c.PlacaCarreta);
        Assert.DoesNotContain("Jabuti", c.Lacre!);
        Assert.DoesNotContain("ADV9874", c.Lacre!);
    }

    /// <summary>
    /// Os campos adicionais 1006 e 1010 a 1013 NAO sao mais lacre. Se alguem reintroduzir a
    /// leitura por numero, este teste acusa - e e o unico lugar que acusaria, porque o 1006
    /// continua existindo no protocolo que NOS enviamos.
    /// </summary>
    [Theory]
    [InlineData(1006)]
    [InlineData(1010)]
    [InlineData(1011)]
    [InlineData(1012)]
    [InlineData(1013)]
    public void O_numero_do_campo_adicional_nao_e_mais_lacre(int numero)
    {
        var c = Ler("{\"CamposAdicionais\":[{\"numero\":" + numero + ",\"valor\":\"0150059\"}]}");

        Assert.Null(c.Lacre);
        Assert.Null(c.Testemunha);
    }

    /// <summary>
    /// A forma antiga, por NOME, e anterior aos dois tipos: alimenta a carga, nunca a testemunha.
    /// </summary>
    [Theory]
    [InlineData("Lacre")]
    [InlineData("Lacres")]
    [InlineData("NumeroLacre")]
    public void O_lacre_por_nome_e_sempre_de_carga(string nome)
    {
        var c = Ler("{\"" + nome + "\":\"65593\"}");

        Assert.Equal("65593", c.Lacre);
        Assert.Null(c.Testemunha);
    }

    // --- como sai no texto da nota -----------------------------------------------------------

    [Fact]
    public void Cada_tipo_sai_no_rotulo_dele_quando_nao_ha_texto_anterior()
    {
        var texto = MapeadorTextoNfeSd.Compor(null, Ler(Linhas("0150059", "0150060")));

        Assert.Contains("Lacres: 0150059", texto);
        Assert.Contains(TextoNfeSd.RotuloTestemunha + " 0150060", texto);
    }

    /// <summary>
    /// O caso que importa de verdade: o texto JA vem da ordem com os dois rotulos vazios, lado a
    /// lado na mesma linha, e cada tipo entra no SEU espaco sem invadir o do outro.
    /// </summary>
    [Fact]
    public void No_texto_da_ordem_cada_tipo_entra_no_seu_espaco()
    {
        var daOrdem = "Lacres:  " + TextoNfeSd.RotuloTestemunha + "  Mot.:  CPF:  Placas: ";

        var texto = MapeadorTextoNfeSd.Compor(daOrdem, Ler(Linhas("134955,134956", "65593,65592")));

        Assert.Contains("Lacres: 134955,134956 " + TextoNfeSd.RotuloTestemunha, texto);
        Assert.Contains(TextoNfeSd.RotuloTestemunha + " 65593,65592 Mot.:", texto);
        // O lacre de carga nao invadiu o espaco da testemunha.
        Assert.DoesNotContain("Lacres: 134955,134956, 65593", texto);
    }

    /// <summary>
    /// A devolutiva real da pesagem 0030384, de 05/11/2026 - a que motivou a mudanca. Sete
    /// lacres em duas linhas, com um espaco sobrando no fim da primeira, e o 1006 voltando "0"
    /// porque e o valor que nos mesmos enviamos no pre-cadastro.
    /// </summary>
    [Fact]
    public void A_pesagem_0030384_separa_os_quatro_da_carga_dos_tres_de_testemunha()
    {
        var c = Ler("""
            {
              "IdTransportadora": "14300001",
              "NomeTransportadora": "Transporte Jabuti",
              "Placa": "MLH4G82",
              "Placa Carreta": "JAO0G12",
              "Observacao": "148594,148595,148596,148597 \r\n59187,59188,59189",
              "CamposAdicionais": [
                { "numero": 1003, "valor": "4308" },
                { "numero": 1006, "valor": "0" },
                { "numero": 1009, "valor": "10" }
              ]
            }
            """);

        Assert.Equal("148594,148595,148596,148597", c.Lacre);
        Assert.Equal("59187,59188,59189", c.Testemunha);
        Assert.Equal("14300001 - Transporte Jabuti", c.Transportadora);
        Assert.Equal("MLH4G82 / JAO0G12", c.Placas);
        // O "0" do 1006 nao aparece em lugar nenhum do texto.
        Assert.DoesNotContain("Lacres: 0,", MapeadorTextoNfeSd.Compor(null, c));
    }
}
