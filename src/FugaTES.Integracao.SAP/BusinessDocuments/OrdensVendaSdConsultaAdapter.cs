using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Integracao.SAP.Http;

namespace FugaTES.Integracao.SAP.BusinessDocuments;

/// <summary>
/// SD: varredura de ordens de venda em API_SALES_ORDER_SRV (OData V2), conforme a planilha de
/// integracao.
///
/// A consulta e feita no CABECALHO (A_SalesOrder) com expand de itens e parceiros, e nao
/// diretamente em A_SalesOrderItem, por um motivo concreto: organizacao de vendas e tipo de
/// documento vivem no cabecalho, e filtrar por eles atraves de navegacao nao e suportado de forma
/// confiavel no V2. O filtro por ShippingPoint, que e campo de item, e aplicado sobre os itens ja
/// trazidos - e por isso o $top limita ORDENS, nao linhas.
/// </summary>
public sealed class OrdensVendaSdConsultaAdapter(SapODataHttpExecutor executor)
    : SapConsultaAdapterBase(executor), IConsultaOrdensVendaSdSap
{
    private const string Servico = "/sap/opu/odata/sap/API_SALES_ORDER_SRV";
    private const string ColecaoOrdens = Servico + "/A_SalesOrder";

    public Task<ResultadoConsultaSap<IReadOnlyList<OrdemVendaSdLeitura>>> ConsultarAsync(
        OrdensVendaSdFiltro filtro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        // Filtro invalido nunca vira requisicao: uma varredura sem recorte a cada 30 segundos
        // traria a base de vendas inteira.
        if (!filtro.EhValido())
            return Task.FromResult(ResultadoConsultaSap<IReadOnlyList<OrdemVendaSdLeitura>>
                .Falha(StatusConsultaSap.FalhaConfiguracao));

        return ConsultarColecaoAsync(ColecaoOrdens, MontarQuery(filtro),
            raiz => Mapear(raiz, filtro), cancellationToken);
    }

    /// <summary>
    /// Le um corpo de resposta do SAP e devolve as linhas, aplicando o mesmo recorte por centro da
    /// varredura. Exposto porque e o unico ponto verificavel sem o SAP no ar - e o mesmo caminho
    /// usado em producao, nao uma copia. Lanca <see cref="JsonException"/> em resposta paginada ou
    /// fora do contrato, deliberadamente: truncar em silencio esconderia ordens.
    /// </summary>
    public static IReadOnlyList<OrdemVendaSdLeitura> LerResposta(string corpoJson, OrdensVendaSdFiltro filtro)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        using var doc = JsonDocument.Parse(corpoJson);
        return Mapear(RaizColecao(doc.RootElement), filtro).ToArray();
    }

    /// <summary>Monta $filter, $expand e $top. Exposto para teste: a query e o contrato real.</summary>
    public static string MontarQuery(OrdensVendaSdFiltro filtro)
    {
        var sb = new StringBuilder("$filter=");
        sb.Append("SalesOrganization eq ").Append(Literal(filtro.SalesOrganization));
        // Situacao da ordem entra no $filter para o SAP nao mandar o que vai ser descartado.
        // A conferencia local em Mapear permanece: se o servico ignorar a clausula, a linha ainda
        // nao aparece.
        sb.Append(" and OverallSDProcessStatus eq ").Append(Literal(filtro.SituacaoOrdemAprovada));
        // Lista vazia = todos os tipos: a clausula simplesmente nao entra. Emitir "and ()" geraria
        // filtro invalido, e emitir "and (false)" traria sempre zero.
        if (filtro.SalesOrderTypes.Count > 0)
        {
            sb.Append(" and (");
            for (var i = 0; i < filtro.SalesOrderTypes.Count; i++)
            {
                if (i > 0) sb.Append(" or ");
                sb.Append("SalesOrderType eq ").Append(Literal(filtro.SalesOrderTypes[i]));
            }
            sb.Append(')');
        }
        sb.Append("&$expand=to_Item,to_Partner");
        sb.Append("&$top=").Append(filtro.MaximoPorVarredura.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // Aspas simples em OData V2 sao escapadas dobrando. Sem isto, um valor com apostrofo
    // quebraria a query - ou pior, mudaria o filtro.
    private static string Literal(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static IEnumerable<OrdemVendaSdLeitura> Mapear(JsonElement raiz, OrdensVendaSdFiltro filtro)
    {
        foreach (var ordem in raiz.EnumerateArray())
        {
            var codigo = Texto(ordem, "SalesOrder");
            if (string.IsNullOrWhiteSpace(codigo)) continue;

            // Fecha por ausencia: ordem sem a situacao esperada - inclusive sem o campo - nao
            // entra. O operador pediu somente "A", e adivinhar aqui traria o que ele excluiu.
            var situacao = Texto(ordem, "OverallSDProcessStatus");
            if (!string.Equals(situacao, filtro.SituacaoOrdemAprovada, StringComparison.OrdinalIgnoreCase))
                continue;

            var (emissor, recebedor) = Parceiros(ordem, filtro);

            foreach (var item in Itens(ordem))
            {
                var centro = Texto(item, "ShippingPoint");
                // O recorte por centro acontece aqui porque ShippingPoint e campo de item.
                if (!filtro.ShippingPoints.Contains(centro, StringComparer.Ordinal)) continue;

                yield return new OrdemVendaSdLeitura(
                    Centro: centro,
                    CodigoOrdemVenda: codigo!,
                    Material: Texto(item, "Material"),
                    LinhaItem: Texto(item, "SalesOrderItem"),
                    SituacaoOrdem: situacao,
                    TipoDocumentoVendas: Texto(ordem, "SalesOrderType"),
                    EmpresaOrdem: Texto(ordem, "SalesOrganization"),
                    Emissor: emissor,
                    SetorAtividade: Texto(item, "OrganizationDivision"),
                    RecebedorMercadoria: recebedor,
                    PayloadJson: Payload(ordem, item, filtro, emissor, recebedor));
            }
        }
    }

    /// <summary>
    /// JSON da linha: apenas os campos da planilha, com o nome OData de origem como chave, para
    /// que cada valor continue rastreavel ate o SAP. Fica de fora o que a resposta trazia e nao
    /// serve a conferencia - metadados do servico, os outros itens da ordem e os demais parceiros.
    /// </summary>
    private static string Payload(JsonElement ordem, JsonElement item, OrdensVendaSdFiltro filtro,
        string? emissor, string? recebedor)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SalesOrder"] = Texto(ordem, "SalesOrder"),
            ["SalesOrderItem"] = Texto(item, "SalesOrderItem"),
            ["ShippingPoint"] = Texto(item, "ShippingPoint"),
            ["Material"] = Texto(item, "Material"),
            ["OverallSDProcessStatus"] = Texto(ordem, "OverallSDProcessStatus"),
            ["SalesOrderType"] = Texto(ordem, "SalesOrderType"),
            ["SalesOrganization"] = Texto(ordem, "SalesOrganization"),
            ["OrganizationDivision"] = Texto(item, "OrganizationDivision"),
            ["Emissor"] = new Dictionary<string, object?>
            { ["PartnerFunction"] = filtro.FuncaoEmissor, ["Customer"] = emissor },
            ["RecebedorMercadoria"] = new Dictionary<string, object?>
            { ["PartnerFunction"] = filtro.FuncaoRecebedor, ["Customer"] = recebedor }
        });

    // V2 entrega colecao expandida como { "results": [...] }; V4, como array direto.
    private static IEnumerable<JsonElement> Itens(JsonElement ordem)
    {
        if (!ordem.TryGetProperty("to_Item", out var itens) &&
            !ordem.TryGetProperty("_Item", out itens)) yield break;
        if (itens.ValueKind == JsonValueKind.Object && itens.TryGetProperty("results", out var results))
            itens = results;
        if (itens.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in itens.EnumerateArray()) yield return item;
    }

    /// <summary>
    /// Emissor e recebedor saem de A_SalesOrderHeaderPartner, selecionados PELA FUNCAO do parceiro
    /// e nao pela ordem em que vierem na lista - a ordem nao e garantida, e pegar o primeiro
    /// Customer daria o parceiro errado conforme o SAP devolvesse.
    ///
    /// O campo da funcao e lido como PartnerFunctionInternalCode (nome da planilha) ou
    /// PartnerFunction, porque os dois aparecem conforme a versao do servico.
    /// </summary>
    private static (string? Emissor, string? Recebedor) Parceiros(JsonElement ordem, OrdensVendaSdFiltro filtro)
    {
        if (!ordem.TryGetProperty("to_Partner", out var parceiros) &&
            !ordem.TryGetProperty("_Partner", out parceiros)) return (null, null);
        if (parceiros.ValueKind == JsonValueKind.Object && parceiros.TryGetProperty("results", out var results))
            parceiros = results;
        if (parceiros.ValueKind != JsonValueKind.Array) return (null, null);

        string? emissor = null, recebedor = null;
        foreach (var p in parceiros.EnumerateArray())
        {
            var funcao = Texto(p, "PartnerFunctionInternalCode") ?? Texto(p, "PartnerFunction");
            if (funcao is null) continue;
            if (string.Equals(funcao, filtro.FuncaoEmissor, StringComparison.OrdinalIgnoreCase))
                emissor ??= Texto(p, "Customer");
            else if (string.Equals(funcao, filtro.FuncaoRecebedor, StringComparison.OrdinalIgnoreCase))
                recebedor ??= Texto(p, "Customer");
        }
        return (emissor, recebedor);
    }
}
