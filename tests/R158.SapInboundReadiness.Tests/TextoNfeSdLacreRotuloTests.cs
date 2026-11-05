using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// O lacre chegou e nao entrou no texto - remessa 80003915, pesagem 0030375, 05/11/2026.
/// </summary>
public sealed class TextoNfeSdLacreRotuloTests
{
    // O texto da ordem como o SAP o devolveu, com "amostras testemunhas" SEPARADO por espaco.
    private const string TextoDaOrdem =
        "PEDIDO(S): 020702 Contrato Nro: TESTE MICHAEL 3 Agendamento:  Temperatura do CT:  C "
        + "Temperatura: C Densidade:  g/cm Densidade a 20 C:  g/cm Fator Correcao:  g/cm "
        + "LAUDO DE CERTIFICACAO:  Lacres:  N dos envelopes de seguranca das amostras testemunhas:  "
        + "Mot.:  CPF:  Placas: ";

    private static ComplementoTextoNfeSd Complemento() => new(
        Lacre: "0150059,0150060", Placa: "MLH4G82", Motorista: "MARCOS RIBEIRO DA SILVA",
        Cpf: "03204564983", PlacaCarreta: "MLH4G85",
        TransportadoraCodigo: "14300001", TransportadoraNome: "Transporte Jabuti");

    [Fact]
    public void O_lacre_entra_no_espaco_dele()
    {
        var texto = MapeadorTextoNfeSd.Compor(TextoDaOrdem, Complemento());
        Assert.Contains("Lacres: 0150059,0150060", texto);
    }

    [Fact]
    public void O_rotulo_seguinte_nao_e_comido()
    {
        var texto = MapeadorTextoNfeSd.Compor(TextoDaOrdem, Complemento());
        Assert.Contains("N dos envelopes de seguranca das amostras testemunhas:", texto);
    }

    // A forma GRUDADA, que pode existir em ordens antigas, tambem tem de ser reconhecida -
    // senao o conserto so troca de lado o mesmo defeito.
    [Fact]
    public void A_forma_grudada_do_rotulo_tambem_delimita()
    {
        var grudado = TextoDaOrdem.Replace("amostras testemunhas", "amostrastestemunhas");
        var texto = MapeadorTextoNfeSd.Compor(grudado, Complemento());
        Assert.Contains("Lacres: 0150059,0150060", texto);
        Assert.Contains("amostrastestemunhas:", texto);
    }

    // Regra 2 continua valendo: lacre que a ORDEM ja preencheu nao e sobrescrito.
    [Fact]
    public void Lacre_ja_preenchido_pela_ordem_nao_e_tocado()
    {
        var comLacre = TextoDaOrdem.Replace("Lacres:  N dos", "Lacres: 999999 N dos");
        var texto = MapeadorTextoNfeSd.Compor(comLacre, Complemento());
        Assert.Contains("Lacres: 999999", texto);
        Assert.DoesNotContain("0150059", texto);
    }

    [Fact]
    public void O_resto_continua_entrando()
    {
        var texto = MapeadorTextoNfeSd.Compor(TextoDaOrdem, Complemento());
        Assert.Contains("Mot.: MARCOS RIBEIRO DA SILVA", texto);
        Assert.Contains("Placas: MLH4G82 / MLH4G85", texto);
        Assert.Contains("Transportadora: 14300001 - Transporte Jabuti", texto);
    }
}
