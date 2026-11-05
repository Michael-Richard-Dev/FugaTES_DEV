using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// Quais linhas da ordem entram na remessa de biodiesel, alem da pesada.
///
/// Pedido do operador em 05/11/2026: a ordem ganha linhas novas depois que a primeira ja foi
/// expedida, e a remessa precisa nascer com todas as que ainda estao em aberto, 1 litro cada.
/// </summary>
public sealed class LinhasRemessaSdTests
{
    private const string Material = "4000561";

    private static LinhaOrdemSd Linha(string numero, string situacao, string? material = Material)
        => new(numero, material, situacao);

    // As duas linhas da ordem 4285 como o SAP as devolveu em 05/11/2026. A 10 esta CONCLUIDA e a
    // 20 em aberto - o inverso do exemplo que o operador narrou de cabeca, e por isso vale fixar:
    // a regra decide pelo dado, nao pela lembranca.
    private static readonly LinhaOrdemSd[] Ordem4285 =
    [
        Linha("20", "B"),
        Linha("10", "C")
    ];

    [Fact]
    public void Ordem_4285_pesando_a_linha_20_nao_acrescenta_a_10_porque_ela_esta_concluida()
        => Assert.Empty(LinhasRemessaSd.Escolher("20", Ordem4285).Adicionais);

    [Fact]
    public void Ordem_4285_pesando_a_linha_10_acrescenta_a_20_que_esta_em_aberto()
        => Assert.Equal(["20"], LinhasRemessaSd.Escolher("10", Ordem4285).Adicionais);

    // A linha pesada entra na remessa pela correlacao, nunca como "adicional" - caso contrario
    // o mesmo item da ordem apareceria duas vezes.
    [Theory]
    [InlineData("10")]
    [InlineData("000010")]
    [InlineData(" 10 ")]
    public void A_linha_pesada_nunca_se_acrescenta_a_si_mesma(string pesada)
        => Assert.DoesNotContain("10",
            LinhasRemessaSd.Escolher(pesada, [Linha("10", "A"), Linha("20", "A")]).Adicionais);

    [Fact]
    public void A_linha_pesada_entra_mesmo_concluida_porque_nao_e_analisada()
    {
        // Ela nao aparece entre as adicionais - e nao precisa: quem a poe na remessa e a
        // correlacao da pesagem. O que este teste fixa e que a situacao dela NAO e consultada
        // para decidir nada.
        var escolha = LinhasRemessaSd.Escolher("10", [Linha("10", "C"), Linha("20", "B")]);
        Assert.Equal(["20"], escolha.Adicionais);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public void Situacao_em_aberto_entra(string situacao)
        => Assert.Equal(["20"], LinhasRemessaSd.Escolher("10",
            [Linha("10", "A"), Linha("20", situacao)]).Adicionais);

    // "C" e concluida. Vazio e nulo ficam de fora porque nao ha decisao de negocio para eles:
    // incluir uma linha cuja situacao o proprio SAP nao afirmou seria adivinhar.
    [Theory]
    [InlineData("C")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("a")]
    public void Situacao_que_nao_e_A_nem_B_fica_de_fora(string? situacao)
        => Assert.Empty(LinhasRemessaSd.Escolher("10",
            [Linha("10", "A"), Linha("20", situacao!)]).Adicionais);

    [Fact]
    public void Varias_linhas_em_aberto_entram_em_ordem_crescente()
    {
        var escolha = LinhasRemessaSd.Escolher("10",
            [Linha("30", "B"), Linha("10", "B"), Linha("20", "A"), Linha("40", "C")]);
        Assert.Equal(["20", "30"], escolha.Adicionais);
    }

    // O operador afirmou que o material nao varia dentro da ordem. Se variar, o sistema deixa de
    // incluir em vez de mandar material errado para dentro de uma remessa que vira NF-e - e
    // registra o que ficou de fora, porque exclusao silenciosa ninguem descobre a tempo.
    [Fact]
    public void Linha_de_outro_material_fica_de_fora_e_e_relatada()
    {
        var escolha = LinhasRemessaSd.Escolher("10",
            [Linha("10", "A"), Linha("20", "A", "9999999"), Linha("30", "A")]);
        Assert.Equal(["30"], escolha.Adicionais);
        Assert.Equal(["20"], escolha.IgnoradasPorMaterial);
    }

    // Sem a linha pesada na resposta nao ha material de referencia; comparar com um material vindo
    // de outra leitura seria divergir do que o SAP acabou de afirmar.
    [Fact]
    public void Sem_a_linha_pesada_na_resposta_o_filtro_de_material_nao_se_aplica()
    {
        var escolha = LinhasRemessaSd.Escolher("99",
            [Linha("10", "A"), Linha("20", "A", "9999999")]);
        Assert.Equal(["10", "20"], escolha.Adicionais);
        Assert.Empty(escolha.IgnoradasPorMaterial);
    }

    [Fact]
    public void Resposta_vazia_nao_acrescenta_nada()
        => Assert.Empty(LinhasRemessaSd.Escolher("10", []).Adicionais);

    [Fact]
    public void Resposta_nula_nao_acrescenta_nada()
        => Assert.Empty(LinhasRemessaSd.Escolher("10", null).Adicionais);

    // --- a remessa montada ------------------------------------------------------------------

    [Fact]
    public void A_remessa_de_biodiesel_leva_a_pesada_e_as_adicionais_com_1_litro_cada()
    {
        var pedido = MapeadorRemessaSd.Montar("4285", "10", 0m, SetorAtividadeSd.Biodiesel, ["20", "30"]);
        var itens = pedido.Itens.Results;

        Assert.Equal(3, itens.Count);
        Assert.Equal(["10", "20", "30"], itens.Select(i => i.ReferenceSDDocumentItem));
        Assert.All(itens, i =>
        {
            Assert.Equal("4285", i.ReferenceSDDocument);
            Assert.Equal("1", i.ActualDeliveryQuantity);
            Assert.Equal("L", i.DeliveryQuantityUnit);
        });
    }

    [Fact]
    public void A_pesada_vem_primeiro()
        => Assert.Equal("10", MapeadorRemessaSd
            .Montar("4285", "10", 0m, SetorAtividadeSd.Biodiesel, ["30", "20"])
            .Itens.Results[0].ReferenceSDDocumentItem);

    // Sem adicionais a remessa e exatamente a de antes - uma linha so. A funcionalidade nova nao
    // pode mudar o que ja funcionava.
    [Theory]
    [InlineData(null)]
    public void Sem_adicionais_a_remessa_continua_de_uma_linha(IReadOnlyList<string>? adicionais)
        => Assert.Single(MapeadorRemessaSd
            .Montar("4285", "10", 0m, SetorAtividadeSd.Biodiesel, adicionais).Itens.Results);

    [Fact]
    public void Lista_vazia_tambem_deixa_a_remessa_de_uma_linha()
        => Assert.Single(MapeadorRemessaSd
            .Montar("4285", "10", 0m, SetorAtividadeSd.Biodiesel, []).Itens.Results);

    // Dupla protecao: mesmo que a escolha falhasse e devolvesse a propria linha pesada, a
    // montagem nao a repete. Duas linhas para o mesmo item seriam duas expedicoes do mesmo saldo.
    [Theory]
    [InlineData("10")]
    [InlineData("000010")]
    public void A_montagem_nao_repete_a_linha_pesada(string repetida)
        => Assert.Single(MapeadorRemessaSd
            .Montar("4285", "10", 0m, SetorAtividadeSd.Biodiesel, [repetida]).Itens.Results);

    [Fact]
    public void A_montagem_nao_repete_uma_adicional_duplicada()
        => Assert.Equal(2, MapeadorRemessaSd
            .Montar("4285", "10", 0m, SetorAtividadeSd.Biodiesel, ["20", "20", "000020"])
            .Itens.Results.Count);

    [Fact]
    public void Adicional_em_branco_e_ignorada()
        => Assert.Single(MapeadorRemessaSd
            .Montar("4285", "10", 0m, SetorAtividadeSd.Biodiesel, ["", "   "]).Itens.Results);
}
