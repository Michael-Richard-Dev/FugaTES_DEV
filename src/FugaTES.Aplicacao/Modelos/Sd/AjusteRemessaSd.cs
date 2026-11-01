using System.Text.Json.Serialization;

namespace FugaTES.Aplicacao.Modelos.Sd;

/// <summary>
/// Passo 4: o que muda na remessa depois de criada, conforme "APIS SD para FUGATES".
///
/// Os TRES processos editam o cabecalho - era onde eu estava errado antes de ler o documento. O
/// que difere e o conteudo:
///
///   biodiesel (setor 63) : pesos da balanca + transporte no cabecalho, e os itens ZERADOS
///   demais setores       : so o transporte; o item ja saiu em KG no passo 3
///
/// Zerar o item nao e detalhe: o documento diz que o peso do cabecalho e o que prevalece, e item
/// com peso atrapalha quando o usuario edita depois a quantidade real em litros.
///
/// "Lote de qualidade" e "demais materiais em KG" sao processos distintos no documento, mas com
/// criacao e cabecalho IDENTICOS - a diferenca, se existir, aparece adiante. Alem disso casca e
/// fibra, que os separam, sao MATERIAL e nao setor (a remessa 80003858 e casca de soja e esta no
/// setor 62, o mesmo de materiais que nao sao casca). Por isso nao ha regra aqui: inventar uma
/// baseada em setor erraria silenciosamente.
/// </summary>
public static class AjusteRemessaSd
{
    /// <summary>Tipo de veiculo, fixo conforme o documento.</summary>
    public const string TipoVeiculo = "YN04";

    /// <summary>
    /// Se o peso da balanca vai para o cabecalho e os itens precisam ser zerados. Só o biodiesel.
    /// </summary>
    public static bool AjustaPeso(string? setorAtividade)
    {
        if (string.IsNullOrWhiteSpace(setorAtividade))
            throw new RemessaSdBloqueadaException("SD_AJUSTE_SETOR_AUSENTE",
                "Sem o setor de atividade não dá para saber se o peso vai no cabeçalho.");
        var setor = setorAtividade.Trim();
        if (!SetorAtividadeSd.Conhecidos.Contains(setor, StringComparer.Ordinal))
            throw new RemessaSdBloqueadaException("SD_AJUSTE_SETOR_DESCONHECIDO",
                $"O setor {setor} não consta na lista do fluxo SD.");
        return setor == SetorAtividadeSd.Biodiesel;
    }
}

/// <summary>
/// Corpo do PATCH no cabecalho. Os pesos saem do JSON quando nao se aplicam: mandar peso num
/// fluxo que nao o quer sobrescreveria o que o SAP derivou do item em KG.
/// </summary>
public sealed record AjusteCabecalhoRemessaSdRequest(
    [property: JsonPropertyName("MeansOfTransport")] string MeioDeTransporte,
    [property: JsonPropertyName("MeansOfTransportType")] string TipoDeTransporte)
{
    [JsonPropertyName("HeaderGrossWeight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PesoBruto { get; init; }

    [JsonPropertyName("HeaderNetWeight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PesoLiquido { get; init; }
}

/// <summary>
/// Corpo do PATCH de cada item no fluxo de biodiesel. Zero em texto, como o documento escreve.
/// </summary>
public sealed record AjusteItemRemessaSdRequest
{
    [JsonPropertyName("ItemGrossWeight")] public string PesoBruto { get; init; } = "0";
    [JsonPropertyName("ItemNetWeight")] public string PesoLiquido { get; init; } = "0";
}

/// <summary>Um item da remessa como o SAP o devolve. A CHAVE vem dele, nao e formatada por nos.</summary>
public sealed record ItemRemessaSdLido(string DeliveryDocument, string DeliveryDocumentItem, string? ETag);

/// <summary>A remessa lida antes do ajuste: ETag do cabecalho e os itens com as chaves de verdade.</summary>
public sealed record RemessaSdLida(
    string DeliveryDocument, string? ETag, IReadOnlyList<ItemRemessaSdLido> Itens);
