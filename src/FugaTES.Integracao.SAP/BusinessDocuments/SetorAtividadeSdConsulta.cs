using System.Text.Json;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Integracao.SAP.Http;

namespace FugaTES.Integracao.SAP.BusinessDocuments;

public interface IConsultarSetorAtividadeSd
{
    Task<ResultadoConsultaSap<string>> ConsultarAsync(string ordemVenda, CancellationToken ct);
}

/// <summary>
/// Le o setor de atividade de UMA ordem de venda.
///
/// E o setor que escolhe o fluxo da remessa - litro no biodiesel, quilo nos demais -, e os
/// protocolos criados antes de 01/11/2026 nao o guardaram. Perguntar ao SAP na hora e melhor do
/// que supor um valor para eles: um palpite aqui vira remessa com a quantidade errada.
///
/// OrganizationDivision e campo de CABECALHO; busca-lo no item devolvia vazio.
/// </summary>
public sealed class SetorAtividadeSdConsulta(SapODataHttpExecutor executor)
    : SapConsultaAdapterBase(executor), IConsultarSetorAtividadeSd
{
    private const string Colecao = "/sap/opu/odata/sap/API_SALES_ORDER_SRV/A_SalesOrder";

    public Task<ResultadoConsultaSap<string>> ConsultarAsync(string ordemVenda, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ordemVenda);
        if (!ordemVenda.All(char.IsAsciiDigit) || ordemVenda.Length > 10)
            return Task.FromResult(ResultadoConsultaSap<string>.Falha(StatusConsultaSap.FalhaConfiguracao));

        // Chave entre aspas simples, como o OData V2 espera. A ordem vai na forma normalizada,
        // sem zeros a esquerda, que e como o servico a devolve.
        return ConsultarAsync($"{Colecao}('{ordemVenda}')", "$select=OrganizationDivision",
            Mapear, ct);
    }

    private static string? Mapear(JsonElement entidade)
    {
        var setor = Texto(entidade, "OrganizationDivision");
        return string.IsNullOrWhiteSpace(setor) ? null : setor.Trim();
    }
}
