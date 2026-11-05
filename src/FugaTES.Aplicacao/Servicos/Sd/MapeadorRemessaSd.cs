using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Sd;

namespace FugaTES.Aplicacao.Servicos.Sd;

/// <summary>
/// Monta a criacao de remessa no SAP a partir de UMA devolutiva de pesagem.
///
/// A forma do corpo e a minima comprovada na remessa 80003858 (01/11/2026): apenas a navegacao
/// de itens, com a referencia a ordem e a linha. O que decide litro ou quilo e o SETOR DE
/// ATIVIDADE, conforme a planilha de fluxo - nao o material e nao o tipo de documento.
/// </summary>
public static class MapeadorRemessaSd
{
    public static readonly JsonSerializerOptions JsonOpcoes = new(JsonSerializerDefaults.Web);

    /// <summary>Serializa pelo mesmo caminho do envio: previsualizacao e POST tem de coincidir.</summary>
    public static string Serializar(RemessaSdRequest pedido)
        => JsonSerializer.Serialize(pedido, JsonOpcoes);

    public static RemessaSdRequest Montar(DevolutivaPesagemSd devolutiva, string? setorAtividade)
    {
        ArgumentNullException.ThrowIfNull(devolutiva);
        return Montar(devolutiva.OrdemVenda, devolutiva.LinhaOrdem, devolutiva.PesoLiquido, setorAtividade);
    }

    /// <summary>
    /// Mesma montagem a partir dos campos soltos, para quem ja leu a devolutiva do banco e tem a
    /// linha, nao o evento. Fabricar um <see cref="DevolutivaPesagemSd"/> incompleto so para
    /// chamar a sobrecarga acima inventaria dados que a leitura nao traz.
    /// </summary>
    /// <param name="linhasAdicionais">
    /// Outras linhas da MESMA ordem que entram na remessa, alem da pesada. So o fluxo de biodiesel
    /// as usa (pedido do operador em 05/11/2026): a ordem ganha linhas novas depois que a primeira
    /// ja foi expedida, e a remessa precisa nascer com todas as que ainda estao em aberto.
    ///
    /// Quem decide QUAIS e <see cref="LinhasRemessaSd.Escolher"/>; aqui elas so viram itens. Cada
    /// uma leva a mesma quantidade e unidade da linha pesada - no biodiesel, 1 L. Nulo ou vazio
    /// devolve exatamente a remessa de uma linha so, que e o comportamento de sempre.
    /// </param>
    public static RemessaSdRequest Montar(string? ordemVenda, string? linhaOrdem,
        decimal pesoLiquido, string? setorAtividade,
        IReadOnlyList<string>? linhasAdicionais = null)
    {
        if (string.IsNullOrWhiteSpace(ordemVenda) || string.IsNullOrWhiteSpace(linhaOrdem))
            throw new RemessaSdBloqueadaException("SD_REMESSA_SEM_CORRELACAO",
                "A pesagem não indica a ordem de venda; não há a que referenciar a remessa.");

        var fluxo = FluxoRemessaSd.Escolher(setorAtividade, pesoLiquido);
        var ordem = ordemVenda.Trim();

        // O SAP espera o numero como ele proprio devolve, sem os zeros de preenchimento:
        // "000010" nao casa com o item "10" da ordem.
        ItemRemessaSdDto Item(string linha) => new(
            ReferenceSDDocument: ordem,
            ReferenceSDDocumentItem: linha.Trim(),
            ActualDeliveryQuantity: fluxo.Quantidade,
            DeliveryQuantityUnit: fluxo.Unidade);

        var itens = new List<ItemRemessaSdDto> { Item(linhaOrdem) };

        // A linha pesada ja esta dentro: repeti-la criaria duas linhas para o mesmo item da ordem.
        // A comparacao ignora zeros a esquerda porque "10" e "000010" sao a mesma linha.
        var pesada = linhaOrdem.Trim().TrimStart('0');
        foreach (var adicional in linhasAdicionais ?? [])
        {
            var numero = (adicional ?? "").Trim();
            if (numero.Length == 0 || numero.TrimStart('0') == pesada) continue;
            if (itens.Any(i => i.ReferenceSDDocumentItem.TrimStart('0') == numero.TrimStart('0'))) continue;
            itens.Add(Item(numero));
        }

        return new RemessaSdRequest(new ItensRemessaSdDto(itens));
    }

    /// <summary>
    /// Numero da remessa criada, lido da resposta. Sem ele a remessa existe no SAP e nos nao
    /// sabemos qual e - o que impede tanto o passo seguinte quanto a conferencia.
    /// </summary>
    public static string? LerNumeroRemessa(string? corpoResposta)
    {
        if (string.IsNullOrWhiteSpace(corpoResposta)) return null;
        try
        {
            using var doc = JsonDocument.Parse(corpoResposta);
            var raiz = doc.RootElement;
            if (raiz.ValueKind == JsonValueKind.Object && raiz.TryGetProperty("d", out var d)) raiz = d;
            if (raiz.ValueKind != JsonValueKind.Object) return null;
            if (!raiz.TryGetProperty("DeliveryDocument", out var numero)) return null;
            var texto = numero.ValueKind switch
            {
                JsonValueKind.String => numero.GetString(),
                JsonValueKind.Number => numero.ToString(),
                _ => null
            };
            return string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
        }
        catch (JsonException) { return null; }
    }
}
