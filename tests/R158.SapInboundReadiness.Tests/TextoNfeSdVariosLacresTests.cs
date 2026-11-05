using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// Varios lacres numa pesagem. O operador ampliou o JSON do Guardian em 05/11/2026: alem do 1006,
/// os campos 1010 a 1013 tambem trazem lacre.
///
/// Os numeros 1010 e 1011 chegaram a ser reservados para transportadora e placa da carreta, antes
/// de se descobrir que o Guardian manda esses dois na RAIZ. Se a reserva tivesse ficado, o nome da
/// transportadora entraria na nota como se fosse lacre - e o teste do fim deste arquivo guarda
/// exatamente isso.
/// </summary>
public sealed class TextoNfeSdVariosLacresTests
{
    private static string Payload(params (int Numero, string Valor)[] campos)
        => "{\"CamposAdicionais\":["
           + string.Join(",", campos.Select(c =>
               "{\"numero\":" + c.Numero + ",\"valor\":\"" + c.Valor + "\"}"))
           + "]}";

    private static ComplementoTextoNfeSd Ler(string json) => MapeadorTextoNfeSd.LerComplemento(json);

    [Fact]
    public void Os_cinco_campos_de_lacre_entram_na_ordem()
        => Assert.Equal("A, B, C, D, E", Ler(Payload(
            (1006, "A"), (1010, "B"), (1011, "C"), (1012, "D"), (1013, "E"))).Lacre);

    // O caso normal: a carga tem menos lacres do que campos, e o resto vem vazio ou zerado.
    [Fact]
    public void Campo_vazio_ou_zerado_some()
        => Assert.Equal("0150059, 0150060", Ler(Payload(
            (1006, "0"), (1010, "0150059"), (1011, "0150060"), (1012, ""), (1013, "0"))).Lacre);

    [Fact]
    public void Todos_zerados_nao_viram_lacre_nenhum()
        => Assert.Null(Ler(Payload(
            (1006, "0"), (1010, ""), (1011, "0"), (1012, "0"), (1013, ""))).Lacre);

    // Zero a esquerda continua sendo lacre - so o valor inteiramente zerado e descartado.
    [Fact]
    public void Lacre_com_zero_a_esquerda_fica()
        => Assert.Equal("0150059", Ler(Payload((1010, "0150059"))).Lacre);

    [Fact]
    public void Lacre_repetido_entra_uma_vez_so()
        => Assert.Equal("0150059", Ler(Payload((1006, "0150059"), (1011, "0150059"))).Lacre);

    // Um campo pode trazer varios lacres separados por virgula, como ja acontecia no 1006.
    [Fact]
    public void Campo_com_varios_lacres_convive_com_os_outros()
        => Assert.Equal("15265,12658", Ler(Payload((1006, "15265,12658"))).Lacre);

    [Fact]
    public void Varios_campos_com_varios_lacres_cada()
        => Assert.Equal("15265,12658, 13654", Ler(Payload(
            (1006, "15265,12658"), (1012, "13654"))).Lacre);

    /// <summary>
    /// A armadilha da renumeracao: 1010 e 1011 agora sao lacre, e a transportadora e a carreta
    /// continuam vindo da raiz. Se as duas leituras dividissem o mesmo numero, o nome da
    /// transportadora iria para a nota como lacre.
    /// </summary>
    [Fact]
    public void O_1010_e_lacre_e_a_transportadora_continua_vindo_da_raiz()
    {
        var c = Ler("""
            {
              "IdTransportadora": "14300001",
              "NomeTransportadora": "Transporte Jabuti",
              "Placa Carreta": "ADV9874",
              "CamposAdicionais": [
                { "numero": 1010, "valor": "0150059" },
                { "numero": 1011, "valor": "0150060" }
              ]
            }
            """);

        Assert.Equal("0150059, 0150060", c.Lacre);
        Assert.Equal("14300001 - Transporte Jabuti", c.Transportadora);
        Assert.Equal("ADV9874", c.PlacaCarreta);
        Assert.DoesNotContain("Jabuti", c.Lacre!);
        Assert.DoesNotContain("ADV9874", c.Lacre!);
    }

    [Fact]
    public void No_texto_os_lacres_saem_todos_no_rotulo_deles()
        => Assert.Contains("Lacres: 0150059, 0150060",
            MapeadorTextoNfeSd.Compor(null, Ler(Payload((1010, "0150059"), (1011, "0150060")))));
}
