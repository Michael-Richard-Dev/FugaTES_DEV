using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;

namespace R158.SapInboundReadiness.Tests;

// SD: leitura do POST que o Guardian envia depois da pesagem. O formato do corpo e o mesmo ja
// recebido no fluxo de recebimento; o que muda e a correlacao com a ordem de venda.
public sealed class DevolutivaSdTests
{
    // Mesmo corpo do exemplo real do Guardian, com TipoPesagem S (venda) e os campos adicionais
    // que o fluxo SD traz.
    private static string Corpo(string tipo = "S", string extras = """
        ,"camposAdicionais":[{"numero":1003,"valor":"0000000782"},{"numero":1009,"valor":"000010"}]
        """) => $$"""
    {
      "SistemaBalanca": "Toledo",
      "DataPesagem": "10/31/2026 11:40:18 AM",
      "TipoPesagem": "{{tipo}}",
      "IdPesagem": "0020327",
      "Centro": "3011",
      "PesoBruto": "20000.0",
      "PesoTara": "10000.0",
      "PesoAntesDesconto": "10000.0",
      "PesoAposDesconto": "9870.5",
      "NomeMotorista": "MARCIO FERENZ",
      "Placa": "COF7595",
      "to_Pesagem": [{"Indice":1,"NrIndiceTotal":1,"IdParceiroEntrega":"20000453"}]
      {{extras}}
    }
    """;

    [Fact]
    public void Devolutiva_de_venda_e_lida_campo_a_campo()
    {
        var d = LeitorDevolutivaSd.Ler(Corpo());
        Assert.Equal("0020327", d.IdPesagem);
        Assert.Equal("3011", d.Centro);
        Assert.Equal("S", d.TipoPesagem);
        Assert.Equal(new DateTime(2026, 10, 31, 11, 40, 18), d.DataPesagem);
        Assert.Equal(DateTimeKind.Unspecified, d.DataPesagem.Kind);
        Assert.Equal(20000.0m, d.PesoBruto);
        Assert.Equal(10000.0m, d.PesoTara);
        Assert.Equal(9870.5m, d.PesoLiquido);
        Assert.Equal("COF7595", d.Placa);
        Assert.Equal("MARCIO FERENZ", d.NomeMotorista);
    }

    // O liquido vem pronto da balanca, com o desconto de qualidade ja aplicado. Recalcular
    // bruto menos tara daria 10000 e perderia o desconto.
    [Fact]
    public void Peso_liquido_e_o_devolvido_e_nao_bruto_menos_tara()
    {
        var d = LeitorDevolutivaSd.Ler(Corpo());
        Assert.Equal(9870.5m, d.PesoLiquido);
        Assert.NotEqual(d.PesoBruto - d.PesoTara, d.PesoLiquido);
    }

    // A chave de correlacao sai normalizada, no mesmo formato do registro de protocolos.
    [Fact]
    public void Correlacao_sai_pela_chave_natural_normalizada()
    {
        var d = LeitorDevolutivaSd.Ler(Corpo());
        Assert.True(d.TemCorrelacao);
        Assert.Equal("782", d.OrdemVenda);
        Assert.Equal("10", d.LinhaOrdem);
        Assert.Equal("782/10", d.ChaveNatural);
    }

    // Ate termos um retorno real do SD, a busca aceita as formas plausiveis.
    [Theory]
    // por numero de campo, como o protocolo grava
    [InlineData(""","camposAdicionais":[{"numero":"1003","valor":"782"},{"numero":"1009","valor":"10"}]""")]
    // por nome, como a tela do Guardian mostra
    [InlineData(""","camposAdicionais":[{"nome":"OrdemVendas","valor":"782"},{"nome":"SD_ItDocV","valor":"10"}]""")]
    // dentro do desdobro, que e onde o Guardian agrupa o que e de cada item
    [InlineData(""","to_Pesagem":[{"camposAdicionais":[{"numero":1003,"valor":"782"},{"numero":1009,"valor":"10"}]}]""")]
    // soltos na raiz
    [InlineData(""","OrdemVendas":"782","SD_ItDocV":"10" """)]
    public void Chave_e_encontrada_nas_formas_plausiveis(string extras)
        => Assert.Equal("782/10", LeitorDevolutivaSd.Ler(Corpo(extras: extras)).ChaveNatural);

    // Sem a chave a devolutiva NAO e recusada: e guardada sem correlacao, para o operador ver o
    // documento. Correlacionar por aproximacao erraria quando houvesse duas ordens do mesmo dia.
    [Fact]
    public void Sem_os_campos_adicionais_a_devolutiva_entra_sem_correlacao()
    {
        var d = LeitorDevolutivaSd.Ler(Corpo(extras: ""));
        Assert.False(d.TemCorrelacao);
        Assert.Null(d.ChaveNatural);
        Assert.Equal("0020327", d.IdPesagem);
    }

    // Recebimento nao pertence a este fluxo: aceitar "E" faria uma entrada virar remessa de venda.
    [Theory]
    [InlineData("E")]
    [InlineData("X")]
    public void Pesagem_que_nao_e_saida_e_recusada(string tipo)
        => Assert.Equal("SD_DEVOLUTIVA_TIPO_NAO_E_SAIDA",
            Assert.Throws<DevolutivaSdInvalidaException>(() => LeitorDevolutivaSd.Ler(Corpo(tipo))).Codigo);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Corpo_vazio_e_recusado(string corpo)
        => Assert.Equal("SD_DEVOLUTIVA_VAZIA",
            Assert.Throws<DevolutivaSdInvalidaException>(() => LeitorDevolutivaSd.Ler(corpo)).Codigo);

    [Theory]
    [InlineData("nao e json")]
    [InlineData("[1,2,3]")]
    public void Corpo_fora_do_contrato_e_recusado(string corpo)
        => Assert.Contains("SD_DEVOLUTIVA_JSON",
            Assert.Throws<DevolutivaSdInvalidaException>(() => LeitorDevolutivaSd.Ler(corpo)).Codigo,
            StringComparison.Ordinal);

    // TipoPesagem saiu desta lista em 06/11/2026: ausente, ele deixou de ser "documento
    // incompleto" e passou a ser "documento que nao e nosso". Ver o teste logo abaixo.
    [Theory]
    [InlineData("IdPesagem")]
    [InlineData("Centro")]
    public void Campo_de_identificacao_ausente_e_recusado(string campo)
    {
        var corpo = Corpo().Replace($"\"{campo}\"", "\"Ignorado_" + campo + "\"", StringComparison.Ordinal);
        var erro = Assert.Throws<DevolutivaSdInvalidaException>(() => LeitorDevolutivaSd.Ler(corpo));
        Assert.Equal("SD_DEVOLUTIVA_CAMPO_AUSENTE", erro.Codigo);
        Assert.Contains(campo, erro.Detalhe, StringComparison.Ordinal);
    }

    /// <summary>
    /// TipoPesagem ausente ou vazio nao e documento incompleto: e documento ALHEIO. Nos sempre
    /// gravamos "S" no campo adicional 1000, entao sem tipo a pesagem nao nasceu de pre-cadastro
    /// do SD - e uma avulsa da balanca. Quem recebe responde 200 e nao grava, porque 400 faria o
    /// Guardian reenviar para sempre um documento que esta certo.
    /// </summary>
    [Theory]
    [InlineData(true)]   // o campo nem existe no documento
    [InlineData(false)]  // o campo existe e veio vazio - o marcador #CAT_VALOR_1000# sem valor
    public void Tipo_de_pesagem_ausente_ou_vazio_nao_e_nosso(bool semOCampo)
    {
        var corpo = semOCampo
            ? Corpo().Replace("\"TipoPesagem\"", "\"Ignorado_TipoPesagem\"", StringComparison.Ordinal)
            : Corpo(tipo: "");

        var erro = Assert.Throws<DevolutivaSdInvalidaException>(() => LeitorDevolutivaSd.Ler(corpo));

        Assert.Equal(DevolutivaSdCodigos.TipoAusente, erro.Codigo);
        Assert.True(DevolutivaSdCodigos.NaoEhDoSd(erro.Codigo));
        Assert.Equal("0020327", erro.IdPesagem);
    }

    [Fact]
    public void Peso_nao_numerico_e_recusado()
        => Assert.Equal("SD_DEVOLUTIVA_PESO_INVALIDO", Assert.Throws<DevolutivaSdInvalidaException>(
            () => LeitorDevolutivaSd.Ler(Corpo().Replace("\"9870.5\"", "\"abc\"", StringComparison.Ordinal))).Codigo);

    [Fact]
    public void Peso_negativo_e_recusado()
        => Assert.Equal("SD_DEVOLUTIVA_PESO_NEGATIVO", Assert.Throws<DevolutivaSdInvalidaException>(
            () => LeitorDevolutivaSd.Ler(Corpo().Replace("\"9870.5\"", "\"-1\"", StringComparison.Ordinal))).Codigo);

    [Theory]
    [InlineData("10/31/2026 11:40:18 AM")]
    [InlineData("31/10/2026 11:40:18")]
    [InlineData("2026-10-31T11:40:18")]
    public void Formatos_de_data_conhecidos_sao_aceitos(string data)
    {
        var corpo = Corpo().Replace("10/31/2026 11:40:18 AM", data, StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 10, 31, 11, 40, 18), LeitorDevolutivaSd.Ler(corpo).DataPesagem);
    }

    [Fact]
    public void Data_fora_dos_formatos_conhecidos_e_recusada()
        => Assert.Equal("SD_DEVOLUTIVA_DATA_INVALIDA", Assert.Throws<DevolutivaSdInvalidaException>(
            () => LeitorDevolutivaSd.Ler(Corpo().Replace("10/31/2026 11:40:18 AM", "ontem", StringComparison.Ordinal))).Codigo);

    // O documento original e a evidencia do que chegou: a projecao nao o substitui.
    [Fact]
    public void Documento_original_e_preservado_inteiro()
    {
        var corpo = Corpo();
        Assert.Equal(corpo, LeitorDevolutivaSd.Ler(corpo).PayloadJson);
    }

    // --- Separador decimal: o pior defeito encontrado em 01/11/2026 ------------------------
    //
    // A balanca Toledo manda "15000,0". Com decimal.TryParse em InvariantCulture, que era o que
    // havia aqui, a virgula e separador de MILHAR: o valor nao era recusado, era aceito como
    // 150000. Dez vezes o peso, calado, indo para a remessa e para a NF-e.

    [Theory]
    [InlineData("15000,0", 15000.0)]
    [InlineData("5000,0", 5000.0)]
    [InlineData("0,0", 0.0)]
    [InlineData("1,5", 1.5)]
    [InlineData("31240,500", 31240.5)]
    public void Virgula_e_separador_decimal_e_nao_de_milhar(string texto, double esperado)
    {
        Assert.True(LeitorDevolutivaSd.TentarNumero(texto, out var numero));
        Assert.Equal((decimal)esperado, numero);
    }

    [Theory]
    [InlineData("15000.0", 15000.0)]
    [InlineData("32500.0", 32500.0)]
    public void Ponto_continua_funcionando(string texto, double esperado)
    {
        Assert.True(LeitorDevolutivaSd.TentarNumero(texto, out var numero));
        Assert.Equal((decimal)esperado, numero);
    }

    // Mesma convencao ja usada no caminho do ACM com esta balanca: virgula e separador decimal,
    // nunca de milhar, e separadores misturados sao RECUSADOS em vez de interpretados. Adivinhar
    // a intencao de um peso e pior do que exigir que ele venha sem ambiguidade.
    [Theory]
    [InlineData("1.234,56")]
    [InlineData("1,234.56")]
    public void Separadores_misturados_sao_recusados(string texto)
        => Assert.False(LeitorDevolutivaSd.TentarNumero(texto, out _));

    [Theory]
    // Ambiguo demais para adivinhar: falhar e melhor do que escolher um sentido.
    [InlineData("1.234.567")]
    [InlineData("1,234,567")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData(null)]
    public void Numero_ambiguo_ou_invalido_e_recusado(string? texto)
        => Assert.False(LeitorDevolutivaSd.TentarNumero(texto, out _));

    [Fact]
    public void Peso_com_virgula_no_documento_inteiro_chega_certo()
    {
        // Mesma forma do payload real da balanca, com virgula em todos os pesos.
        var corpo = """
        {"SistemaBalanca":"Toledo","DataPesagem":"01/11/2026 11:02:22","TipoPesagem":"S",
         "IdPesagem":"0020335","Centro":"3011","PesoBruto":"15000,0","PesoTara":"10000,0",
         "PesoAntesDesconto":"5000,0","PesoAposDesconto":"5000,0","Placa":"TESTEVENDA1C",
         "camposAdicionais":[{"numero":1003,"valor":"2985"},{"numero":1009,"valor":"10"}]}
        """;
        var lida = LeitorDevolutivaSd.Ler(corpo);
        Assert.Equal(15000m, lida.PesoBruto);
        Assert.Equal(10000m, lida.PesoTara);
        Assert.Equal(5000m, lida.PesoLiquido);
    }

    // --- Os dois formatos de data do Guardian -----------------------------------------------
    //
    // O caminho AUTOMATICO manda "11/2/2026 1:03:39 PM" (mes/dia, sem zero a esquerda, AM/PM);
    // o MANUAL manda "02/11/2026 09:12:39" (dia/mes, com zero, 24 horas). Sao a mesma data.
    // Em 02/11/2026 a pesagem 0020340 foi recusada porque os formatos exigiam dois digitos.

    private static string ComData(string data)
        => $$"""
        {"SistemaBalanca":"Toledo","DataPesagem":"{{data}}","TipoPesagem":"S","IdPesagem":"X1",
         "Centro":"3008","PesoBruto":"5000.0","PesoTara":"2000.0","PesoAposDesconto":"3000.0"}
        """;

    [Theory]
    // Automatico: mes/dia com AM/PM, com e sem zero a esquerda.
    [InlineData("11/2/2026 1:03:39 PM", 2026, 11, 2, 13, 3, 39)]
    [InlineData("11/02/2026 01:03:39 PM", 2026, 11, 2, 13, 3, 39)]
    [InlineData("10/31/2026 11:40:18 AM", 2026, 10, 31, 11, 40, 18)]
    // Manual: dia/mes em 24 horas.
    [InlineData("02/11/2026 09:12:39", 2026, 11, 2, 9, 12, 39)]
    [InlineData("2/11/2026 9:12:39", 2026, 11, 2, 9, 12, 39)]
    // ISO, que o contrato tambem promete.
    [InlineData("2026-11-02T13:03:39", 2026, 11, 2, 13, 3, 39)]
    public void Os_formatos_dos_dois_caminhos_sao_lidos(
        string data, int ano, int mes, int dia, int hora, int minuto, int segundo)
        => Assert.Equal(new DateTime(ano, mes, dia, hora, minuto, segundo),
            LeitorDevolutivaSd.Ler(ComData(data)).DataPesagem);

    [Fact]
    public void O_relogio_e_que_decide_a_ordem_entre_mes_e_dia()
    {
        // "03/04/2026" e ambiguo sozinho. Com AM/PM vale mes/dia (3 de abril); em 24 horas vale
        // dia/mes (4 de marco). Sem esse criterio, a ordem da lista de formatos decidiria em
        // silencio - e uma pesagem entraria com a data trocada sem ninguem perceber.
        Assert.Equal(new DateTime(2026, 3, 4, 10, 0, 0),
            LeitorDevolutivaSd.Ler(ComData("03/04/2026 10:00:00 AM")).DataPesagem);
        Assert.Equal(new DateTime(2026, 4, 3, 10, 0, 0),
            LeitorDevolutivaSd.Ler(ComData("03/04/2026 10:00:00")).DataPesagem);
    }

    // --- Correlacao pela metade --------------------------------------------------------------
    //
    // Em 02/11/2026 a pesagem 0020341 chegou com o campo 1009 vazio: ordem "4233", linha "". O
    // leitor devolvia ordem sem linha, e gravar isso violava a regra da tabela - que exige
    // correlacao tudo-ou-nada. A excecao escapava e virava HTTP 500 nu, sem log e sem causa.

    [Theory]
    // Linha vazia, so espaco, ou ausente: em todos, nao ha chave a montar.
    [InlineData("\"valor\": \"4233\"", "\"valor\": \"\"")]
    [InlineData("\"valor\": \"4233\"", "\"valor\": \"   \"")]
    // E o inverso: linha sem ordem tambem nao da chave.
    [InlineData("\"valor\": \"\"", "\"valor\": \"10\"")]
    public void Meia_correlacao_nao_e_correlacao(string ordem, string linha)
    {
        var corpo = $$"""
        {"SistemaBalanca":"Toledo","DataPesagem":"11/2/2026 2:17:37 PM","TipoPesagem":"S",
         "IdPesagem":"0020341","Centro":"3011","PesoBruto":"1200.0","PesoTara":"500.0",
         "PesoAposDesconto":"700.0",
         "CamposAdicionais":[{"numero":1003,{{ordem}}},{"numero":1009,{{linha}}}]}
        """;
        var lida = LeitorDevolutivaSd.Ler(corpo);
        Assert.False(lida.TemCorrelacao);
        Assert.Null(lida.OrdemVenda);
        Assert.Null(lida.LinhaOrdem);
        // Nada se perde: o documento vai inteiro para o banco, exatamente como chegou.
        Assert.Equal(corpo, lida.PayloadJson);
    }

    [Fact]
    public void Correlacao_completa_continua_valendo()
    {
        var corpo = """
        {"SistemaBalanca":"Toledo","DataPesagem":"11/2/2026 2:17:37 PM","TipoPesagem":"S",
         "IdPesagem":"X","Centro":"3011","PesoBruto":"1200.0","PesoTara":"500.0",
         "PesoAposDesconto":"700.0",
         "CamposAdicionais":[{"numero":1003,"valor":"4233"},{"numero":1009,"valor":"10"}]}
        """;
        var lida = LeitorDevolutivaSd.Ler(corpo);
        Assert.True(lida.TemCorrelacao);
        Assert.Equal("4233/10", lida.ChaveNatural);
    }
}

/// <summary>
/// A quem a pesagem pertence. O Guardian manda a MESMA finalizacao para a rota do ACM e para a
/// nossa; o contrato e o que separa as duas.
///
/// Veio de um caso real: em 03/11/2026 a pesagem 0020344, de um contrato do ACM, virou uma linha
/// de venda em branco - sem ordem, sem linha, sem nada a fazer com ela.
/// </summary>
public sealed class ContratoAcmNaDevolutivaSdTests
{
    private static string Corpo(string? contrato) => $$"""
    {
      "SistemaBalanca": "Toledo",
      "DataPesagem": "03/11/2026 08:30:44",
      "TipoPesagem": "S",
      "IdPesagem": "0020344",
      "Centro": "3011",
      "PesoBruto": "5000,0",
      "PesoTara": "2000,0",
      "PesoAposDesconto": "3000,0",
      "to_Pesagem": [{"Indice":1,"NrIndiceTotal":1{{(contrato is null ? "" : $",\"ContratoACM\":\"{contrato}\"")}}}]
    }
    """;

    [Fact]
    public void Pesagem_com_contrato_e_do_acm()
    {
        var d = LeitorDevolutivaSd.Ler(Corpo("0000001175"));
        Assert.True(d.EhDoAcm);
        Assert.Equal("0000001175", d.ContratoAcm);
    }

    [Theory]
    [InlineData(null)]        // o campo nem vem - e o caso das vendas que ja rodaram
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("0000000000")]
    public void Sem_contrato_preenchido_a_pesagem_e_do_sd(string? contrato)
    {
        // Zeros sao como o Guardian escreve "sem contrato". Le-los como preenchido devolveria
        // TODA venda ao ACM, que e a falha oposta e silenciosa.
        var d = LeitorDevolutivaSd.Ler(Corpo(contrato));
        Assert.False(d.EhDoAcm);
        Assert.Null(d.ContratoAcm);
    }

    [Fact]
    public void Basta_um_desdobro_com_contrato()
    {
        var corpo = """
        {
          "SistemaBalanca": "Toledo", "DataPesagem": "03/11/2026 08:30:44", "TipoPesagem": "S",
          "IdPesagem": "0020344", "Centro": "3011",
          "PesoBruto": "5000,0", "PesoTara": "2000,0", "PesoAposDesconto": "3000,0",
          "to_Pesagem": [
            {"Indice":1,"NrIndiceTotal":2,"ContratoACM":""},
            {"Indice":2,"NrIndiceTotal":2,"ContratoACM":"0000001175"}
          ]
        }
        """;
        // A pesagem mista e a duvidosa: exigir contrato em todos os desdobros a deixaria entrar.
        Assert.Equal("0000001175", LeitorDevolutivaSd.Ler(corpo).ContratoAcm);
    }

    [Fact]
    public void Contrato_do_acm_nao_impede_a_leitura_do_resto()
    {
        // Continua sendo um documento valido: ele e DESVIADO, nao recusado. Quem decide o que
        // fazer com ele e o receptor, e para isso precisa dos campos lidos.
        var d = LeitorDevolutivaSd.Ler(Corpo("0000001175"));
        Assert.Equal("0020344", d.IdPesagem);
        Assert.Equal(5000.0m, d.PesoBruto);
        Assert.Equal(3000.0m, d.PesoLiquido);
        Assert.Equal(new DateTime(2026, 11, 3, 8, 30, 44), d.DataPesagem);
    }
}

/// <summary>
/// Zero nao e ordem de venda.
///
/// O Guardian preenche 1003 e 1009 com zeros quando a pesagem nao pertence a uma ordem - toda
/// pesagem do ACM chega assim. Em 03/11/2026 as pesagens 0020344 e 0020345 foram gravadas como
/// CORRELACIONADA com a chave "0/0": uma ordem que nao existe, numa tabela que a tela le.
/// </summary>
public sealed class CorrelacaoZeradaSdTests
{
    private static string Corpo(string ordem, string linha) => $$"""
    {
      "SistemaBalanca": "Toledo", "DataPesagem": "11/3/2026 9:23:54 AM", "TipoPesagem": "S",
      "IdPesagem": "0020345", "Centro": "3011",
      "PesoBruto": "5000.0", "PesoTara": "2000.0", "PesoAposDesconto": "3000.0",
      "camposAdicionais": [{"numero":1003,"valor":"{{ordem}}"},{"numero":1009,"valor":"{{linha}}"}]
    }
    """;

    [Theory]
    [InlineData("0", "0")]
    [InlineData("000000", "000000")]
    [InlineData("0000000000", "000010")]   // so a ordem zerada ja invalida o par
    [InlineData("0000000782", "0")]        // so a linha zerada tambem
    public void Zeros_nao_correlacionam(string ordem, string linha)
    {
        var d = LeitorDevolutivaSd.Ler(Corpo(ordem, linha));
        Assert.False(d.TemCorrelacao);
        Assert.Null(d.ChaveNatural);
    }

    [Fact]
    public void Ordem_de_verdade_continua_correlacionando()
    {
        // O zero a esquerda do SAP segue sendo removido: o que muda e so o valor TODO zerado.
        var d = LeitorDevolutivaSd.Ler(Corpo("0000000782", "000010"));
        Assert.Equal("782", d.OrdemVenda);
        Assert.Equal("10", d.LinhaOrdem);
        Assert.Equal("782/10", d.ChaveNatural);
    }
}
