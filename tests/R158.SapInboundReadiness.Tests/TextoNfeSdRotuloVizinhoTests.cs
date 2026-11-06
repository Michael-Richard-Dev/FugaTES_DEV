using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// ROTULO VIZINHO QUE CONTEM OUTRO ROTULO. A remessa 80003934, em 06/11/2026.
///
/// O texto daquela ordem tem, na mesma linha:
///
///     Mot.:  CPF:  Placa:  Placa Transportadora:
///
/// e saiu com "Placa:" VAZIO e a transportadora escrita dentro de "Placa Transportadora:" - um
/// campo de PLACA. Dois defeitos, com a mesma raiz: "Placa Transportadora:" nao estava na lista
/// de rotulos conhecidos.
///
///   1. o espaco de "Placa:" se estendia ate o proximo rotulo CONHECIDO. Como "Placa
///      Transportadora:" nao era um deles, o espaco engolia a palavra "Placa" dela, parecia
///      PREENCHIDO, e a regra 2 - nao tocar no que a ordem ja trouxe - o deixava em branco;
///
///   2. "Transportadora:" casa DENTRO de "Placa Transportadora:". O rotulo da transportadora
///      encontrou ali o seu lugar e escreveu "14300001 - Transporte Jabuti" num campo que espera
///      placa.
///
/// E a mesma familia do defeito de 05/11 com "amostras testemunhas": vizinho desconhecido faz o
/// espaco do campo anterior passar por cima dele. A diferenca e que agora o vizinho CONTEM um
/// rotulo nosso, e por isso nao basta conhece-lo - a comparacao tambem precisa recusar o
/// casamento parcial.
/// </summary>
public sealed class TextoNfeSdRotuloVizinhoTests
{
    /// <summary>O texto como vem da ordem 4308, com os espacos ainda em branco.</summary>
    private const string DaOrdem =
        "PEDIDO(S):49580\n"
        + "Contrato Nro: SPOT 09.0304.26 Agendamento: 46643051\n"
        + "Lacres:  " + TextoNfeSd.RotuloTestemunha + "\n"
        + "Mot.:  CPF:  Placa:  Placa Transportadora: ";

    private static ComplementoTextoNfeSd Complemento() => new(
        Lacre: "148594,148595", Testemunha: "59187,59188",
        Placa: "MLH4G82", PlacaCarreta: "JAO0G12",
        Motorista: "DERLI SCARIOT PORTO", Cpf: "00412322080",
        TransportadoraCodigo: "14300001", TransportadoraNome: "Transporte Jabuti");

    private static string Texto() => MapeadorTextoNfeSd.Compor(DaOrdem, Complemento());

    [Fact]
    public void As_placas_entram_no_campo_placa()
        => Assert.Contains("Placa: MLH4G82 / JAO0G12", Texto(), StringComparison.Ordinal);

    /// <summary>
    /// "Placa Transportadora:" espera PLACA. A transportadora nao pode ir parar ali so porque o
    /// rotulo dela aparece dentro do nome desse campo.
    /// </summary>
    [Fact]
    public void A_transportadora_nao_entra_no_campo_de_placa_da_transportadora()
        => Assert.DoesNotContain("Placa Transportadora: 14300001", Texto(), StringComparison.Ordinal);

    /// <summary>
    /// Sem rotulo proprio no texto da ordem, a transportadora nao se perde: vai numa linha ao
    /// fim. E a regra 3, e e melhor do que escreve-la no campo errado.
    /// </summary>
    [Fact]
    public void A_transportadora_vai_para_o_fim_quando_nao_ha_rotulo_proprio()
        => Assert.Contains("Transportadora: 14300001 - Transporte Jabuti",
            Texto().Split('\n')[^1], StringComparison.Ordinal);

    /// <summary>
    /// Onde HA rotulo proprio, ele continua sendo usado - liberar o vizinho nao pode ter
    /// quebrado o caso normal.
    /// </summary>
    [Fact]
    public void Com_rotulo_proprio_a_transportadora_entra_nele()
    {
        var texto = MapeadorTextoNfeSd.Compor(
            "Mot.:  CPF:  Placas:  Transportadora: ", Complemento());

        Assert.Contains("Transportadora: 14300001 - Transporte Jabuti", texto, StringComparison.Ordinal);
        Assert.Contains("Placas: MLH4G82 / JAO0G12", texto, StringComparison.Ordinal);
    }

    /// <summary>A variante do outro modelo: "Placa Transportador:", no masculino.</summary>
    [Fact]
    public void A_variante_no_masculino_tambem_delimita()
    {
        var texto = MapeadorTextoNfeSd.Compor("Placas:  Placa Transportador: ", Complemento());

        Assert.Contains("Placas: MLH4G82 / JAO0G12", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("Placa Transportador: 14300001", texto, StringComparison.Ordinal);
    }

    /// <summary>
    /// O que veio da ordem continua intocado, inclusive o campo vizinho que ficou em branco -
    /// nao e nosso para preencher.
    /// </summary>
    [Fact]
    public void O_que_veio_da_ordem_nao_e_reescrito()
    {
        var texto = Texto();

        Assert.Contains("PEDIDO(S):49580", texto, StringComparison.Ordinal);
        Assert.Contains("Contrato Nro: SPOT 09.0304.26", texto, StringComparison.Ordinal);
        Assert.Contains("Agendamento: 46643051", texto, StringComparison.Ordinal);
    }
}
