using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Integracao.SAP.Http;

namespace FugaTES.Integracao.SAP.BusinessDocuments;

public interface IConsultarLinhasOrdemSd
{
    Task<ResultadoConsultaSap<IReadOnlyList<LinhaOrdemSd>>> ConsultarAsync(
        string ordemVenda, CancellationToken ct);
}

/// <summary>
/// Le TODAS as linhas de uma ordem de venda, com material e situacao.
///
/// Existe para o fluxo de biodiesel decidir, na hora de criar a remessa, quais outras linhas da
/// ordem ainda estao em aberto e precisam entrar junto (pedido do operador em 05/11/2026).
///
/// E leitura NOVA, feita no momento da criacao, e nao reaproveitamento do que a varredura trouxe:
/// entre a varredura e a pesagem passam-se horas, e nesse meio tempo uma linha pode ter sido
/// acrescentada ou concluida. Decidir com dado velho e decidir errado devagar.
///
/// SDProcessStatus e campo de ITEM. A varredura ja o le, mas no cabecalho a situacao equivalente
/// (OverallSDProcessStatus) resume a ordem inteira e nao serve para escolher linha.
/// </summary>
public sealed class LinhasOrdemSdConsulta(SapODataHttpExecutor executor)
    : SapConsultaAdapterBase(executor), IConsultarLinhasOrdemSd
{
    private const string ColecaoItens = "/sap/opu/odata/sap/API_SALES_ORDER_SRV/A_SalesOrderItem";

    /// <summary>
    /// Teto da leitura. Uma ordem de venda com mais linhas do que isto nao existe no fluxo, e o
    /// teto evita que uma resposta paginada chegue parecendo completa - a base recusa pagina,
    /// mas recusar e pior do que caber.
    /// </summary>
    private const int MaximoLinhas = 200;

    public Task<ResultadoConsultaSap<IReadOnlyList<LinhaOrdemSd>>> ConsultarAsync(
        string ordemVenda, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ordemVenda);
        var ordem = ordemVenda.Trim();
        if (!ordem.All(char.IsAsciiDigit) || ordem.Length > 10)
            return Task.FromResult(
                ResultadoConsultaSap<IReadOnlyList<LinhaOrdemSd>>.Falha(StatusConsultaSap.FalhaConfiguracao));

        // $select enxuto de proposito: a entidade tem mais de cem campos e a resposta inteira so
        // enche o log de diagnostico com o que nao se le. $orderby no proprio SAP para que a
        // sequencia nao dependa da ordem em que ele resolveu devolver.
        return ConsultarColecaoAsync(ColecaoItens,
            $"$filter=SalesOrder eq '{ordem}'"
            + "&$select=SalesOrder,SalesOrderItem,Material,SDProcessStatus"
            + "&$orderby=SalesOrderItem"
            + $"&$top={MaximoLinhas}",
            Mapear, ct);
    }

    private static IEnumerable<LinhaOrdemSd> Mapear(JsonElement colecao)
    {
        foreach (var item in colecao.EnumerateArray())
        {
            var linha = Texto(item, "SalesOrderItem")?.Trim();
            if (string.IsNullOrEmpty(linha)) continue;
            yield return new LinhaOrdemSd(linha,
                Texto(item, "Material")?.Trim(),
                Texto(item, "SDProcessStatus")?.Trim());
        }
    }
}
