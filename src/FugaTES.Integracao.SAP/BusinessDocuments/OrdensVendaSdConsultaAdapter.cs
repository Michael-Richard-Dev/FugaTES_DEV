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
        // Corte por data de criacao. Vai no $filter, e nao numa conferencia local, para o SAP nao
        // mandar o que seria descartado: com o corte a varredura do motor sai de 268 ordens para
        // as poucas criadas depois dele.
        if (filtro.DataCriacaoMinima is DateOnly corte)
            sb.Append(" and CreationDate ge datetime'")
              .Append(corte.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
              .Append("T00:00:00'");
        sb.Append("&$expand=to_Item,to_Partner");
        // Mais recentes primeiro. Sem isto o SAP devolve pela chave, do menor para o maior, e o
        // $top passa a ser uma janela sobre as ordens MAIS ANTIGAS: em 01/11/2026 havia 268
        // ordens elegiveis e a 4232, criada no dia, nao aparecia na tela.
        // Confirmado no servico que a ordenacao e numerica (4232, 4221, 4214...), e nao textual.
        sb.Append("&$orderby=SalesOrder desc");
        sb.Append("&$top=").Append(filtro.MaximoPorVarredura.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// Data no formato do OData V2 em JSON: "/Date(1793491200000)/", milissegundos desde a epoca,
    /// em UTC. Qualquer outra forma devolve nulo, e nulo nao passa no corte por data - tratar
    /// "nao sei quando foi criada" como "foi criada hoje" criaria protocolo no acervo antigo.
    /// </summary>
    public static DateTime? DataOData(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        var texto = valor.Trim();
        if (!texto.StartsWith("/Date(", StringComparison.Ordinal)
            || !texto.EndsWith(")/", StringComparison.Ordinal)) return null;
        var miolo = texto[6..^2];
        if (miolo.Length == 0) return null;
        // Algumas respostas trazem deslocamento de fuso ("/Date(123+0200)/"): o instante e o
        // numero antes do sinal, ja em UTC.
        var sinal = miolo.IndexOfAny(['+', '-'], 1);
        if (sinal > 0) miolo = miolo[..sinal];
        return long.TryParse(miolo, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : null;
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
                    // Setor de atividade e campo de CABECALHO em A_SalesOrder. Lia-lo do item
                    // deixava a coluna vazia - e e ele que decide o processo de remessa
                    // (biodiesel em litro, demais em quilo). O item fica como alternativa porque
                    // o servico V4 o expoe la.
                    SetorAtividade: Texto(ordem, "OrganizationDivision") ?? Texto(item, "OrganizationDivision"),
                    RecebedorMercadoria: recebedor,
                    PayloadJson: Payload(ordem, item, filtro, emissor, recebedor))
                {
                    DataCriacao = DataOData(Texto(ordem, "CreationDate"))
                };
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
            ["OrganizationDivision"] = Texto(ordem, "OrganizationDivision") ?? Texto(item, "OrganizationDivision"),
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
