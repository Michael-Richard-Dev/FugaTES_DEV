namespace FugaTES.Aplicacao.Modelos.Sd;

/// <summary>
/// Uma linha da ordem de venda como o SAP a devolve em A_SalesOrderItem, com o minimo que a
/// decisao precisa: qual linha e, de que material, e em que situacao esta.
/// </summary>
public sealed record LinhaOrdemSd(string LinhaItem, string? Material, string? SituacaoItem);

/// <summary>
/// Quais linhas da ordem entram na remessa, alem da que foi pesada.
///
/// Pedido do operador em 05/11/2026, so no fluxo de BIODIESEL: a ordem pode ganhar linhas novas
/// depois que a primeira ja foi expedida - 10 com 50000, depois 20 com mais 50000, depois 30 - e a
/// remessa precisa nascer com TODAS as que ainda estao em aberto, cada uma com 1 litro. O operador
/// acerta as quantidades reais a mao, na remessa ja criada.
///
/// Duas decisoes que o operador fechou e que estao aqui de proposito:
///
///   - A LINHA PESADA ENTRA SEMPRE, sem olhar situacao. Ela e a razao de a remessa existir; se
///     fosse preciso aprova-la, uma corrida entre a pesagem e o SAP poderia produzir remessa
///     vazia. "Na primeira que e a 10 nao precisa verificar nada" - palavras dele.
///   - A verificacao e SO para as demais.
///
/// E sobre o material: ele disse que nunca varia dentro da ordem. Filtrar por ele, entao, nao muda
/// nada no mundo real - e no mundo que ele considera impossivel, faz o sistema DEIXAR DE INCLUIR
/// em vez de mandar material errado para dentro de uma remessa que vira NF-e. Omissao e visivel na
/// conferencia; material trocado numa nota nao e.
/// </summary>
public static class LinhasRemessaSd
{
    /// <summary>
    /// Situacoes de ITEM que contam como em aberto (A_SalesOrderItem.SDProcessStatus): "A" nao
    /// processada e "B" parcialmente processada. "C" e concluida e fica de fora.
    ///
    /// Vazio tambem fica de fora: o SAP devolve "" em item que ainda nao entrou em processamento
    /// de remessa, e nao ha decisao de negocio registrada para esse caso. Incluir no escuro uma
    /// linha cuja situacao o proprio SAP nao afirmou seria adivinhar.
    /// </summary>
    public static readonly string[] SituacoesAbertas = ["A", "B"];

    public static bool EstaAberta(string? situacao) =>
        situacao is not null && SituacoesAbertas.Contains(situacao.Trim(), StringComparer.Ordinal);

    /// <summary>
    /// O que a escolha produziu: as linhas a acrescentar e as que ficaram de fora por material
    /// diferente - estas ultimas existem so para o journal, porque exclusao silenciosa e o tipo de
    /// coisa que ninguem descobre ate a conferencia do estoque.
    /// </summary>
    public sealed record Escolha(
        IReadOnlyList<string> Adicionais,
        IReadOnlyList<string> IgnoradasPorMaterial);

    /// <summary>
    /// Linhas a acrescentar a remessa, alem de <paramref name="linhaPesada"/>.
    ///
    /// O material de referencia e o da PROPRIA linha pesada, lido da mesma resposta do SAP - e nao
    /// um valor trazido de outro lugar: comparar com um material de outra leitura abriria espaco
    /// para divergir do que o SAP acabou de afirmar. Quando a linha pesada nao aparece na resposta,
    /// nao ha referencia para comparar e o filtro de material nao se aplica.
    ///
    /// A ordem e crescente pelo numero da linha, como o operador le a ordem no SAP.
    /// </summary>
    public static Escolha Escolher(string? linhaPesada, IEnumerable<LinhaOrdemSd>? linhas)
    {
        var pesada = (linhaPesada ?? "").Trim();
        var todas = (linhas ?? []).Where(l => !string.IsNullOrWhiteSpace(l.LinhaItem)).ToArray();

        var referencia = todas.FirstOrDefault(l => MesmaLinha(l.LinhaItem, pesada))?.Material?.Trim();

        var adicionais = new List<string>();
        var ignoradas = new List<string>();

        foreach (var linha in todas)
        {
            var numero = linha.LinhaItem.Trim();
            if (MesmaLinha(numero, pesada)) continue;
            if (!EstaAberta(linha.SituacaoItem)) continue;

            if (!string.IsNullOrEmpty(referencia) &&
                !string.Equals(linha.Material?.Trim(), referencia, StringComparison.Ordinal))
            { ignoradas.Add(numero); continue; }

            adicionais.Add(numero);
        }

        return new(Ordenar(adicionais), Ordenar(ignoradas));
    }

    /// <summary>
    /// "10" e "000010" sao a mesma linha. O SAP devolve a forma curta em A_SalesOrderItem, mas a
    /// devolutiva do Guardian nem sempre - e comparar texto cru faria a linha pesada entrar DUAS
    /// vezes na remessa, uma pela correlacao e outra como "adicional".
    /// </summary>
    private static bool MesmaLinha(string? a, string? b)
    {
        var x = (a ?? "").Trim().TrimStart('0');
        var y = (b ?? "").Trim().TrimStart('0');
        return x.Length > 0 && x == y;
    }

    // Numerica quando da, textual quando nao da: linha nao numerica nao deveria existir, e cair
    // para ordem textual e melhor do que explodir na montagem da remessa.
    private static IReadOnlyList<string> Ordenar(List<string> numeros)
    {
        numeros.Sort((a, b) =>
            long.TryParse(a, out var x) && long.TryParse(b, out var y)
                ? x.CompareTo(y)
                : string.CompareOrdinal(a, b));
        return numeros;
    }
}
