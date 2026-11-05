using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// Existem DOIS modelos de texto no cadastro, confirmados pelo operador em 05/11/2026.
///
/// Na remessa 80003917 - ordem 4292, setor 65 - nenhum rotulo nosso casou e todos os valores foram
/// para o fim como linhas pendentes: a nota ficava com "PLACA:" vazio em cima e "Placas: JAK3F79"
/// embaixo. O dado nao se perdia (regra 3), mas o texto saia errado.
/// </summary>
public sealed class TextoNfeSdDoisModelosTests
{
    // Modelo 2, da remessa 80003917: maiusculas, cada rotulo na sua linha.
    private const string Modelo2 =
        "VENDEDOR: BIOFUGA BANCO: ITAU - MARAU\nPEDIDO(S): TESTE INTEGRACAO\n"
        + "MOTORISTA: \nPLACA: \nLACRES: ";

    // Modelo 1, das remessas anteriores: tudo numa linha so.
    private const string Modelo1 =
        "LAUDO DE CERTIFICACAO:  Lacres:  N dos envelopes de seguranca das amostras testemunhas:  "
        + "Mot.:  CPF:  Placas: ";

    private static ComplementoTextoNfeSd Complemento() => new(
        Lacre: "0150059,0150060", Placa: "JAK3F79", Motorista: "DERLI SCARIOT PORTO",
        Cpf: "00412322080", PlacaCarreta: "JAK3F79",
        TransportadoraCodigo: "14300001", TransportadoraNome: "Transporte Jabuti");

    [Fact]
    public void No_modelo_de_maiusculas_os_rotulos_sao_preenchidos()
    {
        var texto = MapeadorTextoNfeSd.Compor(Modelo2, Complemento());
        Assert.Contains("LACRES: 0150059,0150060", texto);
        Assert.Contains("PLACA: JAK3F79", texto);
        Assert.Contains("MOTORISTA: DERLI SCARIOT PORTO", texto);
    }

    // O que o defeito produzia: rotulo vazio em cima e o nosso embaixo. Nao pode mais acontecer.
    [Fact]
    public void No_modelo_de_maiusculas_nada_e_duplicado_no_fim()
    {
        var texto = MapeadorTextoNfeSd.Compor(Modelo2, Complemento());
        Assert.DoesNotContain("Lacres: 0150059", texto);
        Assert.DoesNotContain("Placas: JAK3F79", texto);
        Assert.DoesNotContain("Mot.: DERLI", texto);
    }

    // O modelo 1 nao pode ter regredido.
    [Fact]
    public void O_modelo_de_uma_linha_continua_funcionando()
    {
        var texto = MapeadorTextoNfeSd.Compor(Modelo1, Complemento());
        Assert.Contains("Lacres: 0150059,0150060", texto);
        Assert.Contains("Placas: JAK3F79", texto);
        Assert.Contains("Mot.: DERLI SCARIOT PORTO", texto);
        Assert.Contains("CPF: 00412322080", texto);
        Assert.Contains("amostras testemunhas:", texto);
    }

    // O que nao tem rotulo em nenhum dos dois modelos continua indo ao fim - regra 3.
    [Fact]
    public void O_que_nao_tem_rotulo_continua_indo_ao_fim()
    {
        Assert.EndsWith("Transportadora: 14300001 - Transporte Jabuti",
            MapeadorTextoNfeSd.Compor(Modelo2, Complemento()));
        // O modelo 2 nao tem CPF: ele tambem vai ao fim, com o rotulo canonico.
        Assert.Contains("CPF: 00412322080", MapeadorTextoNfeSd.Compor(Modelo2, Complemento()));
    }

    // Regra 2 vale nos dois modelos: o que a ordem preencheu nao e sobrescrito.
    [Fact]
    public void No_modelo_de_maiusculas_o_ja_preenchido_nao_e_tocado()
    {
        var texto = MapeadorTextoNfeSd.Compor(Modelo2.Replace("PLACA: ", "PLACA: XXX1234"), Complemento());
        Assert.Contains("PLACA: XXX1234", texto);
        Assert.DoesNotContain("PLACA: JAK3F79", texto);
    }
}
