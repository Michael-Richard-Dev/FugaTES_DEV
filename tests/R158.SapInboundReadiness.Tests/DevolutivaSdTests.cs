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

    [Theory]
    [InlineData("IdPesagem")]
    [InlineData("Centro")]
    [InlineData("TipoPesagem")]
    public void Campo_de_identificacao_ausente_e_recusado(string campo)
    {
        var corpo = Corpo().Replace($"\"{campo}\"", "\"Ignorado_" + campo + "\"", StringComparison.Ordinal);
        var erro = Assert.Throws<DevolutivaSdInvalidaException>(() => LeitorDevolutivaSd.Ler(corpo));
        Assert.Equal("SD_DEVOLUTIVA_CAMPO_AUSENTE", erro.Codigo);
        Assert.Contains(campo, erro.Detalhe, StringComparison.Ordinal);
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
}
