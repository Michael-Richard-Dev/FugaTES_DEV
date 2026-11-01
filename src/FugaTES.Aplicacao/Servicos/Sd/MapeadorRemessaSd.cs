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
    public static RemessaSdRequest Montar(string? ordemVenda, string? linhaOrdem,
        decimal pesoLiquido, string? setorAtividade)
    {
        if (string.IsNullOrWhiteSpace(ordemVenda) || string.IsNullOrWhiteSpace(linhaOrdem))
            throw new RemessaSdBloqueadaException("SD_REMESSA_SEM_CORRELACAO",
                "A pesagem não indica a ordem de venda; não há a que referenciar a remessa.");

        var fluxo = FluxoRemessaSd.Escolher(setorAtividade, pesoLiquido);

        return new RemessaSdRequest(new ItensRemessaSdDto([
            new ItemRemessaSdDto(
                // O SAP espera o numero como ele proprio devolve, sem os zeros de preenchimento:
                // "000010" nao casa com o item "10" da ordem.
                ReferenceSDDocument: ordemVenda.Trim(),
                ReferenceSDDocumentItem: linhaOrdem.Trim(),
                ActualDeliveryQuantity: fluxo.Quantidade,
                DeliveryQuantityUnit: fluxo.Unidade)
        ]));
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
