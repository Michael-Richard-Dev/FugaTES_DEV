using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// O que a balanca acrescenta ao texto da NF-e, lido da devolutiva do Guardian.
///
/// Em 05/11/2026 o operador relatou que lacre, transportadora e carreta nao chegavam ao SAP. A
/// investigacao no banco mostrou DUAS causas diferentes, e so uma era defeito nosso:
///
///   1. a lista de campos adicionais chama-se "CamposAdicionais", com C maiusculo, e o codigo
///      procurava "camposAdicionais". TryGetProperty diferencia maiusculas, entao a lista nunca
///      era encontrada - o lacre nunca entraria no texto nem quando viesse;
///   2. a devolutiva trazia APENAS os campos 1003 e 1009, em todas as pesagens, com "Observacao"
///      vazia. Os tres dados simplesmente nao eram enviados.
///
/// Estes testes fixam o contrato de leitura para quando o Guardian passar a envia-los.
/// </summary>
public sealed class TextoNfeSdComplementoTests
{
    /// <summary>
    /// A devolutiva EXATAMENTE como o Guardian a enviava em 05/11/2026 - chaves da raiz em
    /// PascalCase, chaves internas dos campos adicionais em minuscula, e so dois campos.
    /// </summary>
    private const string PayloadReal = """
        {
          "IdPesagem": "0030369",
          "Centro": "3008",
          "Placa": "ABC1D23",
          "NomeMotorista": "FULANO DE TAL",
          "CpfMotorista": "12345678909",
          "Observacao": "",
          "CamposAdicionais": [
            { "valor": "4291", "numero": 1003 },
            { "valor": "10", "numero": 1009 }
          ]
        }
        """;

    private static ComplementoTextoNfeSd Ler(string json) => MapeadorTextoNfeSd.LerComplemento(json);

    // O que ja funcionava continua funcionando: estas tres chaves estao na raiz com a grafia que o
    // codigo sempre esperou, e por isso nunca falharam.
    [Fact]
    public void Placa_motorista_e_cpf_continuam_sendo_lidos_da_raiz()
    {
        var c = Ler(PayloadReal);
        Assert.Equal("ABC1D23", c.Placa);
        Assert.Equal("FULANO DE TAL", c.Motorista);
        Assert.Equal("12345678909", c.Cpf);
    }

    // O payload real nao traz os tres - e o leitor nao pode inventar.
    [Fact]
    public void No_payload_de_hoje_os_tres_campos_novos_vem_vazios()
    {
        var c = Ler(PayloadReal);
        Assert.Null(c.Lacre);
        Assert.Null(c.Transportadora);
        Assert.Null(c.PlacaCarreta);
    }

    // --- o contrato, para quando o Guardian passar a enviar ---------------------------------

    private const string PayloadComOsTres = """
        {
          "Placa": "ABC1D23",
          "CamposAdicionais": [
            { "valor": "4291", "numero": 1003 },
            { "valor": "10", "numero": 1009 },
            { "valor": "65593 / 65594 / 65595", "numero": 1006 },
            { "valor": "TRANSPORTES SAO JORGE LTDA", "numero": 1010 },
            { "valor": "XYZ9W87", "numero": 1011 }
          ]
        }
        """;

    [Fact]
    public void Pelos_numeros_reservados_os_tres_sao_lidos()
    {
        var c = Ler(PayloadComOsTres);
        Assert.Equal("65593 / 65594 / 65595", c.Lacre);
        Assert.Equal("TRANSPORTES SAO JORGE LTDA", c.Transportadora);
        Assert.Equal("XYZ9W87", c.PlacaCarreta);
    }

    [Theory]
    [InlineData("Lacre")]
    [InlineData("Lacres")]
    [InlineData("NumeroLacre")]
    public void O_lacre_tambem_e_lido_pelo_nome(string nome)
        => Assert.Equal("65593", Ler($$"""
            { "CamposAdicionais": [ { "nome": "{{nome}}", "valor": "65593" } ] }
            """).Lacre);

    [Theory]
    [InlineData("Transportadora")]
    [InlineData("NomeTransportadora")]
    [InlineData("Transportador")]
    [InlineData("RazaoSocialTransportadora")]
    public void A_transportadora_tambem_e_lida_pelo_nome(string nome)
        => Assert.Equal("SAO JORGE", Ler($$"""
            { "CamposAdicionais": [ { "nome": "{{nome}}", "valor": "SAO JORGE" } ] }
            """).Transportadora);

    [Theory]
    [InlineData("PlacaCarreta")]
    [InlineData("PlacaReboque")]
    [InlineData("Placa da Carreta")]
    public void A_carreta_tambem_e_lida_pelo_nome(string nome)
        => Assert.Equal("XYZ9W87", Ler($$"""
            { "CamposAdicionais": [ { "nome": "{{nome}}", "valor": "XYZ9W87" } ] }
            """).PlacaCarreta);

    // Terceira forma do contrato: chave solta na raiz, como Placa e NomeMotorista ja chegam.
    [Fact]
    public void Os_tres_tambem_sao_lidos_de_chave_solta_na_raiz()
    {
        var c = Ler("""
            { "Lacre": "65593", "Transportadora": "SAO JORGE", "PlacaCarreta": "XYZ9W87" }
            """);
        Assert.Equal("65593", c.Lacre);
        Assert.Equal("SAO JORGE", c.Transportadora);
        Assert.Equal("XYZ9W87", c.PlacaCarreta);
    }

    // O defeito que estava escondido: a lista com C maiusculo nao era encontrada. As duas grafias
    // passam a valer, porque o protocolo que ENVIAMOS usa a minuscula.
    [Theory]
    [InlineData("CamposAdicionais")]
    [InlineData("camposAdicionais")]
    public void As_duas_grafias_da_lista_sao_aceitas(string chave)
        => Assert.Equal("65593", Ler($$"""
            { "{{chave}}": [ { "valor": "65593", "numero": 1006 } ] }
            """).Lacre);

    // O numero pode chegar como numero JSON ou como texto; os dois sao o campo 1006.
    [Theory]
    [InlineData("1006")]
    [InlineData("\"1006\"")]
    public void O_numero_do_campo_vale_como_numero_ou_como_texto(string numero)
        => Assert.Equal("65593", Ler($$"""
            { "CamposAdicionais": [ { "valor": "65593", "numero": {{numero}} } ] }
            """).Lacre);

    // "nao precisa limitar a quantidade de caracteres" - operador, 05/11/2026. O valor vai inteiro.
    [Fact]
    public void O_valor_nao_e_truncado()
    {
        var longo = string.Join(" / ", Enumerable.Range(1, 60).Select(n => $"LACRE{n:D6}"));
        var c = Ler($$"""
            { "CamposAdicionais": [ { "valor": "{{longo}}", "numero": 1006 } ] }
            """);
        Assert.Equal(longo, c.Lacre);
        Assert.Contains(longo, MapeadorTextoNfeSd.Compor(null, c));
    }

    // O Guardian de 05/11/2026 devolve o campo 1006 como "#CAT_VALOR_1006#" - o valor que NOS
    // enviamos no protocolo, que e "0" (NaoSeAplica), quando ninguem digita o lacre na balanca.
    // Sem este corte a NF-e sairia com "Lacres: 0" impresso.
    [Theory]
    [InlineData("0")]
    [InlineData("00")]
    [InlineData("000000")]
    [InlineData(" 0 ")]
    [InlineData("")]
    public void Zero_nao_e_lacre(string valor)
        => Assert.Null(Ler($$"""
            { "CamposAdicionais": [ { "valor": "{{valor}}", "numero": 1006 } ] }
            """).Lacre);

    [Fact]
    public void Zero_nao_e_transportadora_nem_carreta()
    {
        var c = Ler("""
            { "IdTransportadora": "0", "Placa Carreta": "0" }
            """);
        Assert.Null(c.Transportadora);
        Assert.Null(c.PlacaCarreta);
    }

    // Um lacre que COMECA com zero e lacre. O corte e so para o valor inteiramente zerado.
    [Fact]
    public void Lacre_com_zero_a_esquerda_continua_valendo()
        => Assert.Equal("065593", Ler("""
            { "CamposAdicionais": [ { "valor": "065593", "numero": 1006 } ] }
            """).Lacre);

    // Como o operador configurou o Guardian em 05/11/2026: carreta como chave solta com espaco no
    // nome, e transportadora como "IdTransportadora".
    [Fact]
    public void O_json_configurado_no_guardian_e_lido()
    {
        var c = Ler("""
            {
              "Placa": "ABC1D23",
              "IdTransportadora": "4821",
              "Placa Carreta": "XYZ9W87",
              "CamposAdicionais": [ { "valor": "65593", "numero": 1006 } ]
            }
            """);
        Assert.Equal("ABC1D23", c.Placa);
        Assert.Equal("4821", c.Transportadora);
        Assert.Equal("4821", c.TransportadoraCodigo);
        Assert.Null(c.TransportadoraNome);
        Assert.Equal("XYZ9W87", c.PlacaCarreta);
        Assert.Equal("65593", c.Lacre);
    }

    // --- transportadora: codigo E nome ------------------------------------------------------

    /// <summary>
    /// A devolutiva como o Guardian passou a envia-la em 05/11/2026, com os dois campos da
    /// transportadora. E a pesagem 0030370, com os valores reais.
    /// </summary>
    private const string PayloadComTransportadora = """
        {
          "TipoPesagem": "E",
          "IdPesagem": "0030370",
          "NomeMotorista": "ODIRLEI BRUFATTI",
          "CpfMotorista": "01211998002",
          "IdTransportadora": "14300001",
          "NomeTransportadora": "Transporte Jabuti",
          "Placa": "GVD3489",
          "Placa Carreta": "ADV9874",
          "Observacao": "",
          "CamposAdicionais": [
            { "numero": 1003, "valor": "0" },
            { "numero": 1006, "valor": "0" },
            { "numero": 1009, "valor": "10" }
          ]
        }
        """;

    [Fact]
    public void A_transportadora_vai_com_codigo_e_nome()
    {
        var c = Ler(PayloadComTransportadora);
        Assert.Equal("14300001", c.TransportadoraCodigo);
        Assert.Equal("Transporte Jabuti", c.TransportadoraNome);
        Assert.Equal("14300001 - Transporte Jabuti", c.Transportadora);
    }

    [Fact]
    public void Do_payload_real_saem_as_placas_e_a_transportadora_mas_nao_o_lacre()
    {
        var c = Ler(PayloadComTransportadora);
        Assert.Equal("GVD3489 / ADV9874", c.Placas);
        Assert.Equal("14300001 - Transporte Jabuti", c.Transportadora);
        // 1006 chegou "0", que e o que NOS enviamos no protocolo. Nao e lacre.
        Assert.Null(c.Lacre);
    }

    [Fact]
    public void So_o_codigo_vai_sozinho()
        => Assert.Equal("14300001", Ler("""
            { "IdTransportadora": "14300001" }
            """).Transportadora);

    [Fact]
    public void So_o_nome_vai_sozinho()
        => Assert.Equal("Transporte Jabuti", Ler("""
            { "NomeTransportadora": "Transporte Jabuti" }
            """).Transportadora);

    // Mesma regra das duas placas: valores iguais nao viram "X - X". Quando so a caixa difere,
    // prevalece o NOME - e ele que uma pessoa le na nota.
    [Fact]
    public void Codigo_igual_ao_nome_nao_se_repete()
        => Assert.Equal("jabuti", Ler("""
            { "IdTransportadora": "JABUTI", "NomeTransportadora": "jabuti" }
            """).Transportadora);

    [Fact]
    public void Codigo_zerado_deixa_so_o_nome()
        => Assert.Equal("Transporte Jabuti", Ler("""
            { "IdTransportadora": "0", "NomeTransportadora": "Transporte Jabuti" }
            """).Transportadora);

    // --- varios lacres numa pesagem ---------------------------------------------------------

    /// <summary>
    /// Pergunta do operador em 05/11/2026: e se a balanca digitar varios lacres separados por
    /// virgula? A resposta tem de ser "vai inteiro", e por isso o valor dele esta aqui literal.
    ///
    /// A virgula nao tem significado nenhum para o leitor: o lacre nunca e convertido em numero,
    /// nem separado em lista, nem truncado. Sai do Guardian e entra no texto como texto.
    /// </summary>
    private const string QuatroLacres = "15265,12658,12698,13654";

    [Fact]
    public void Varios_lacres_separados_por_virgula_chegam_inteiros()
        => Assert.Equal(QuatroLacres, Ler($$"""
            { "CamposAdicionais": [ { "valor": "{{QuatroLacres}}", "numero": 1006 } ] }
            """).Lacre);

    [Fact]
    public void Varios_lacres_entram_inteiros_no_texto_novo()
        => Assert.Contains($"Lacres: {QuatroLacres}",
            MapeadorTextoNfeSd.Compor(null, new ComplementoTextoNfeSd(Lacre: QuatroLacres)));

    /// <summary>
    /// O caso que importa de verdade: o texto JA existe, vindo da ordem, com o rotulo "Lacres:"
    /// seguido de outro rotulo na MESMA linha. O valor entra no espaco em branco sem comer o
    /// rotulo seguinte - que e o unico jeito de nao estragar o texto que o negocio escreveu.
    /// </summary>
    [Fact]
    public void Varios_lacres_cabem_no_espaco_do_texto_que_veio_da_ordem()
    {
        const string daOrdem =
            "VENDEDOR: JOSE\nLacres:  N dos envelopes de seguranca das amostrastestemunhas: 65593\n"
            + "Mot.:  CPF:  Placas: ";

        var texto = MapeadorTextoNfeSd.Compor(daOrdem, new ComplementoTextoNfeSd(
            Lacre: QuatroLacres, Placa: "GVD3489", PlacaCarreta: "ADV9874",
            Motorista: "ODIRLEI BRUFATTI", Cpf: "01211998002",
            TransportadoraCodigo: "14300001", TransportadoraNome: "Transporte Jabuti"));

        Assert.Contains($"Lacres: {QuatroLacres} N dos envelopes", texto);
        Assert.Contains("65593", texto);
        Assert.Contains("VENDEDOR: JOSE", texto);
        Assert.Contains("Placas: GVD3489 / ADV9874", texto);
        Assert.EndsWith("Transportadora: 14300001 - Transporte Jabuti", texto);
    }

    // Separador nao importa: ponto e virgula, barra ou espaco passam iguais.
    [Theory]
    [InlineData("15265,12658,12698,13654")]
    [InlineData("15265; 12658; 12698")]
    [InlineData("15265 / 12658 / 12698")]
    [InlineData("15265 12658")]
    public void O_separador_dos_lacres_nao_e_interpretado(string lacres)
        => Assert.Equal(lacres, Ler($$"""
            { "CamposAdicionais": [ { "valor": "{{lacres}}", "numero": 1006 } ] }
            """).Lacre);

    // --- o texto montado --------------------------------------------------------------------

    [Fact]
    public void Os_tres_entram_no_texto_quando_nao_ha_texto_anterior()
    {
        var texto = MapeadorTextoNfeSd.Compor(null, new ComplementoTextoNfeSd(
            Lacre: "65593", Placa: "ABC1D23", Motorista: "FULANO", Cpf: "12345678909",
            PlacaCarreta: "XYZ9W87", TransportadoraNome: "SAO JORGE"));

        Assert.Contains("Lacres: 65593", texto);
        Assert.Contains("Placas: ABC1D23 / XYZ9W87", texto);
        Assert.Contains("Transportadora: SAO JORGE", texto);
    }

    // A transportadora pode nao ter espaco no texto que vem da ordem. Nesse caso ela vai numa
    // linha ao fim - a regra 3 da composicao - em vez de se perder.
    [Fact]
    public void Sem_rotulo_no_texto_da_ordem_a_transportadora_vai_ao_fim()
    {
        var atual = "VENDEDOR: JOSE   BANCO: 001\nLacres:  Mot.:  CPF:  Placas: ";
        var texto = MapeadorTextoNfeSd.Compor(atual,
            new ComplementoTextoNfeSd(TransportadoraNome: "SAO JORGE"));

        Assert.StartsWith("VENDEDOR: JOSE", texto);
        Assert.EndsWith("Transportadora: SAO JORGE", texto);
    }

    // Regra 2 da composicao, que vale para o campo novo como para os antigos: o que a ordem ja
    // preencheu nao e tocado.
    [Fact]
    public void Transportadora_ja_preenchida_pela_ordem_nao_e_sobrescrita()
    {
        var atual = "Transportadora: JA ESTAVA AQUI\n";
        Assert.Contains("JA ESTAVA AQUI", MapeadorTextoNfeSd.Compor(atual,
            new ComplementoTextoNfeSd(TransportadoraNome: "SAO JORGE")));
        Assert.DoesNotContain("SAO JORGE", MapeadorTextoNfeSd.Compor(atual,
            new ComplementoTextoNfeSd(TransportadoraNome: "SAO JORGE")));
    }
}
