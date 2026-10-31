using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Resultados.SAP;

namespace FugaTES.Aplicacao.Contratos.Gateways.SAP;

/// <summary>
/// SD: varredura periodica de ordens de venda no SAP.
///
/// Distinta de <see cref="IConsultaOrdemVendaSap"/>, que busca UMA ordem por chave em outro
/// servico (OData V4, api_salesorder/srvd_a2x). Esta porta consulta a COLECAO no servico V2
/// API_SALES_ORDER_SRV, que e o indicado na planilha de integracao e o unico que expoe
/// OverallSDProcessStatus, OrganizationDivision e PartnerFunctionInternalCode com esses nomes.
/// </summary>
public interface IConsultaOrdensVendaSdSap
{
    Task<ResultadoConsultaSap<IReadOnlyList<OrdemVendaSdLeitura>>> ConsultarAsync(
        OrdensVendaSdFiltro filtro, CancellationToken cancellationToken);
}
