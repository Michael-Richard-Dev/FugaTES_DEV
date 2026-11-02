using System.Text.Json.Serialization;

namespace FugaTES.Aplicacao.Modelos.Sd;

/// <summary>
/// Setores de atividade do SD, conforme a planilha de fluxo do operador (01/11/2026). O setor e
/// o que escolhe COMO a remessa e criada - nao o material, nem o tipo de documento.
/// </summary>
public static class SetorAtividadeSd
{
    public const string Graos = "60";
    public const string OleoDeSoja = "61";

    /// <summary>
    /// Casca. A planilha chamava o 62 de "farelo de soja", mas o cadastro do SAP mostra o material
    /// de casca neste setor (conferido em 02/11/2026). O operador confirmou que o nome da planilha
    /// e que estava errado.
    /// </summary>
    public const string Casca = "62";

    public const string Biodiesel = "63";
    public const string AcidoGraxo = "64";
    public const string Glicerina = "65";

    /// <summary>Fibra. Mesma correcao do 62: o cadastro traz "FIBRA VEGETAL" neste setor.</summary>
    public const string Fibra = "66";

    public const string Retrovenda = "67";

    public static readonly string[] Conhecidos =
        [Graos, OleoDeSoja, Casca, Biodiesel, AcidoGraxo, Glicerina, Fibra, Retrovenda];

    /// <summary>
    /// Setores em que a remessa passa por SEPARACAO antes da saida de mercadoria. Decisao do
    /// operador em 02/11/2026: picking e so de casca e fibra.
    ///
    /// Nos demais o botao de picking fica desabilitado, e nao apenas "desaconselhado": um clique
    /// que nao cabe no fluxo e barato de dar e caro de desfazer.
    /// </summary>
    public static readonly string[] ComPicking = [Casca, Fibra];

    public static bool ExigePicking(string? setorAtividade)
        => setorAtividade is not null
           && ComPicking.Contains(setorAtividade.Trim(), StringComparer.Ordinal);
}

/// <summary>Como a quantidade da remessa e determinada. Dois comportamentos, nao tres.</summary>
public sealed record FluxoRemessaSd(string Quantidade, string Unidade, bool AjustaPesoNoCabecalho)
{
    /// <summary>
    /// Biodiesel: a remessa nasce com UM LITRO, e o peso real vai depois no cabecalho, com os
    /// itens zerados. Demais setores: a quantidade e o proprio peso liquido da balanca, em quilo,
    /// e o cabecalho nao precisa de ajuste porque o item ja e controlado em KG.
    /// </summary>
    public static FluxoRemessaSd Escolher(string? setorAtividade, decimal pesoLiquidoKg)
    {
        if (string.IsNullOrWhiteSpace(setorAtividade))
            throw new RemessaSdBloqueadaException("SD_REMESSA_SETOR_AUSENTE",
                "Sem o setor de atividade não dá para escolher entre litro e quilo.");
        var setor = setorAtividade.Trim();
        if (!SetorAtividadeSd.Conhecidos.Contains(setor, StringComparer.Ordinal))
            throw new RemessaSdBloqueadaException("SD_REMESSA_SETOR_DESCONHECIDO",
                $"O setor {setor} não consta na lista do fluxo SD.");

        if (setor == SetorAtividadeSd.Biodiesel)
            return new("1", "L", AjustaPesoNoCabecalho: true);

        if (pesoLiquidoKg <= 0)
            throw new RemessaSdBloqueadaException("SD_REMESSA_PESO_INVALIDO",
                "Peso líquido precisa ser maior que zero fora do fluxo de biodiesel.");
        // Tres casas, como o SAP devolve nas quantidades.
        return new(pesoLiquidoKg.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            "KG", AjustaPesoNoCabecalho: false);
    }
}

// --- Corpo da chamada ao SAP -------------------------------------------------------------
// Forma minima comprovada em 01/11/2026 na remessa 80003858: so a navegacao de itens. O SAP
// deriva local de expedicao, tipo de documento, emissor, recebedor e datas da propria ordem.
// Mandar ShippingPoint a mais nao ajudou e so acrescentou uma hipotese errada ao diagnostico.

public sealed record ItemRemessaSdDto(
    [property: JsonPropertyName("ReferenceSDDocument")] string ReferenceSDDocument,
    [property: JsonPropertyName("ReferenceSDDocumentItem")] string ReferenceSDDocumentItem,
    [property: JsonPropertyName("ActualDeliveryQuantity")] string ActualDeliveryQuantity,
    [property: JsonPropertyName("DeliveryQuantityUnit")] string DeliveryQuantityUnit);

public sealed record ItensRemessaSdDto(
    [property: JsonPropertyName("results")] IReadOnlyList<ItemRemessaSdDto> Results);

public sealed record RemessaSdRequest(
    [property: JsonPropertyName("to_DeliveryDocumentItem")] ItensRemessaSdDto Itens);

/// <summary>Por que uma remessa nao pode ser criada. Nenhuma linha vai ao SAP "no escuro".</summary>
public sealed class RemessaSdBloqueadaException(string codigo, string detalhe)
    : Exception(codigo + ": " + detalhe)
{
    public string Codigo { get; } = codigo;
    public string Detalhe { get; } = detalhe;
}
