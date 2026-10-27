#nullable enable
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FugaTES.WinForms.Acm;

/// <summary>
/// Origem efetiva dos dados exibidos. Nunca inferida: quem monta a visao informa,
/// e a projecao persistida NAO pode ser rotulada como payload originalmente recebido.
/// </summary>
public enum OrigemJsonContrato
{
    /// <summary>Bytes exatamente como o FugaTES os recebeu do SAP.</summary>
    RecebidoPeloFugaTES,
    /// <summary>Resultado de um GET autorizado feito agora; estado atual, nao o recebido.</summary>
    ConsultaSapAtual,
    /// <summary>Reconstrucao a partir do snapshot tipado persistido.</summary>
    ProjecaoPersistida,
}

public static class OrigemJsonContratoTexto
{
    public static string Rotulo(OrigemJsonContrato origem) => origem switch
    {
        OrigemJsonContrato.RecebidoPeloFugaTES => "RECEBIDO_PELO_FUGATES",
        OrigemJsonContrato.ConsultaSapAtual => "CONSULTA_SAP_ATUAL",
        OrigemJsonContrato.ProjecaoPersistida => "PROJECAO_PERSISTIDA",
        _ => "ORIGEM_DESCONHECIDA",
    };

    public static string Explicacao(OrigemJsonContrato origem) => origem switch
    {
        OrigemJsonContrato.RecebidoPeloFugaTES =>
            "Payload como recebido do SAP, byte a byte.",
        OrigemJsonContrato.ConsultaSapAtual =>
            "GET autorizado executado agora. Reflete o estado ATUAL no SAP, "
            + "que pode divergir do que foi recebido.",
        OrigemJsonContrato.ProjecaoPersistida =>
            "Reconstrucao a partir do snapshot tipado persistido em "
            + "contrato_sap_toledo_versao.payload_contrato. NAO e o payload originalmente "
            + "recebido: campos fora do contrato tipado nao foram preservados na admissao.",
        _ => "Origem nao informada.",
    };
}

public sealed record CampoDocumentadoNtt(
    string CampoDocumentacao,
    string CampoSap,
    string? Valor,
    bool Presente,
    string Situacao);

/// <summary>
/// Representacao "Conforme documentacao SAP" (NTT Data, "Servicos Balancas agro",
/// secao SERVICO ENVIAR CONTRATO (GET), pagina 6).
///
/// Visao ADICIONAL, somente de apresentacao. O payload persistido continua com os
/// nomes do servico OData (NumeroContrato, ...) e NAO e alterado por esta classe —
/// Inc006SapContratoInboundTests exige que o round-trip nao contenha nomes da
/// documentacao.
///
/// Regras que esta classe nao quebra:
///  - identificadores permanecem string, com zeros a esquerda intactos;
///  - decimais sao lidos do texto bruto do JSON, sem passar por double;
///  - datas /Date(...)/ sao mostradas como vieram, com o instante UTC derivado
///    marcado como derivado — nenhuma conversao de fuso implicita;
///  - campo ausente e representado como AUSENTE, nunca preenchido por suposicao.
/// </summary>
public static class ContratoSapDocumentoNtt
{
    /// <summary>Mapeamento documentado, na ordem da pagina 6.</summary>
    public static readonly (string Documentacao, string Sap)[] Mapeamento =
    [
        ("NRCONTRATO", "NumeroContrato"),
        ("ITEM", "Item"),
        ("TPCONTRATO", "TipoContrato"),
        ("DESCTPCONTRATO", "DescricaoTipoContrato"),
        ("DTEMISSAO", "DataEmissao"),
        ("IDPARCEIRO", "IdParceiro"),
        ("CENTRO", "Centro"),
        ("MATERIAL", "Material"),
        ("SISTBALANCA", "SistemaBalanca"),
        ("TPPESAGEM", "TipoPesagem"),
        ("DATENTINI", "DataEntregaInicio"),
        ("DATENTFIM", "DataEntregaFim"),
        ("UNIDCONT", "UnidCont"),
        ("QUANCONT", "QuantidadeContrato"),
        ("QTDSALDDEVOL", "QuantidadeSaldoDevolucao"),
        ("TABQUALID", "TabelaQualidadeID"),
        ("TABQUALVERS", "TabelaQualidadeVersao"),
        ("TABQUALVERSVALID", "TabelaQualidadeVersaoValidade"),
        ("OBSERVACAO", "Observacao"),
        ("OPERACAOCONT", "OperacaoContrato"),
        ("VLUNITARIO", "ValorUnitario"),
        ("STATCONTRATO", "StatusContrato"),
        ("DIASTOL", "DiasToler"),
        ("DATLIMITENT", "DataLimiteEntrega"),
    ];

    /// <summary>
    /// Campos que constam da documentacao e NAO existem na origem efetivamente
    /// persistida: nao estao no DTO de entrada, no snapshot nem no canonicalizador.
    /// Sao exibidos como ausentes. MATERIALSAP nao e presumido igual a MATERIAL.
    /// </summary>
    public static readonly (string Documentacao, string Motivo)[] SemOrigemConhecida =
    [
        ("GRUIDPROG",
            "Ausente no contrato de entrada (SapContractInboundItemHttpDto), no "
            + "ContratoSapToledoSnapshot e no payload persistido."),
        ("MATERIALSAP",
            "Ausente nas mesmas origens. NAO equivale a MATERIAL: nenhum valor e "
            + "presumido."),
    ];

    private const string Ausente = "AUSENTE NA ORIGEM";

    public static IReadOnlyList<CampoDocumentadoNtt> Mapear(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        using var documento = JsonDocument.Parse(payloadJson);
        var raiz = documento.RootElement;
        var linhas = new List<CampoDocumentadoNtt>(Mapeamento.Length + SemOrigemConhecida.Length);

        foreach (var (doc, sap) in Mapeamento)
        {
            if (!raiz.TryGetProperty(sap, out var valor) || valor.ValueKind == JsonValueKind.Null)
            {
                linhas.Add(new(doc, sap, null, false, Ausente));
                continue;
            }
            var (texto, situacao) = Representar(valor);
            linhas.Add(new(doc, sap, texto, true, situacao));
        }

        foreach (var (doc, motivo) in SemOrigemConhecida)
            linhas.Add(new(doc, "(sem campo correspondente)", null, false, Ausente + " - " + motivo));

        return linhas;
    }

    /// <summary>
    /// Campos presentes no payload e fora da documentacao. Preservados e exibidos:
    /// o operador precisa ver que existem.
    /// </summary>
    public static IReadOnlyList<CampoDocumentadoNtt> CamposForaDaDocumentacao(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        using var documento = JsonDocument.Parse(payloadJson);
        var documentados = Mapeamento.Select(m => m.Sap).ToHashSet(StringComparer.Ordinal);
        var extras = new List<CampoDocumentadoNtt>();
        foreach (var propriedade in documento.RootElement.EnumerateObject())
        {
            if (documentados.Contains(propriedade.Name)) continue;
            var (texto, situacao) = Representar(propriedade.Value);
            extras.Add(new("(fora da documentacao)", propriedade.Name, texto, true, situacao));
        }
        return extras;
    }

    /// <summary>
    /// JSON com os nomes da documentacao. Campos ausentes entram como null para que a
    /// ausencia seja visivel; os valores saem com o texto bruto do payload, de modo que
    /// zeros a esquerda e precisao decimal sobrevivem. Campos fora da documentacao vao
    /// para um objeto separado, sem se misturarem aos documentados.
    /// </summary>
    public static string SerializarConformeDocumentacao(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        using var documento = JsonDocument.Parse(payloadJson);
        var raiz = documento.RootElement;
        var destino = new JsonObject();

        foreach (var (doc, sap) in Mapeamento)
            destino[doc] = raiz.TryGetProperty(sap, out var valor) && valor.ValueKind != JsonValueKind.Null
                ? JsonNode.Parse(valor.GetRawText())
                : null;

        foreach (var (doc, _) in SemOrigemConhecida)
            destino[doc] = null;

        var documentados = Mapeamento.Select(m => m.Sap).ToHashSet(StringComparer.Ordinal);
        JsonObject? extras = null;
        foreach (var propriedade in raiz.EnumerateObject())
        {
            if (documentados.Contains(propriedade.Name)) continue;
            extras ??= [];
            extras[propriedade.Name] = JsonNode.Parse(propriedade.Value.GetRawText());
        }
        if (extras is not null) destino["_CamposForaDaDocumentacao"] = extras;

        return destino.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Texto para exibicao e situacao, a partir do valor bruto. Nada passa por
    /// double, DateTime ou conversao de fuso.
    /// </summary>
    private static (string Texto, string Situacao) Representar(JsonElement valor)
    {
        switch (valor.ValueKind)
        {
            case JsonValueKind.String:
                var texto = valor.GetString() ?? string.Empty;
                if (TentarInstanteOData(texto, out var utc))
                    return (texto, "DATA ODATA - instante UTC derivado: " + utc
                        + " (derivado, sem conversao de fuso)");
                if (texto.Length > 1 && texto[0] == '0')
                    return (texto, "TEXTO - zeros a esquerda preservados");
                return (texto, "TEXTO");

            case JsonValueKind.Number:
                // Texto bruto: preserva a escala exatamente como persistida.
                return (valor.GetRawText(), "NUMERO - texto bruto do JSON preservado");

            case JsonValueKind.True:
            case JsonValueKind.False:
                return (valor.GetRawText(), "BOOLEANO");

            case JsonValueKind.Array:
                var itens = valor.EnumerateArray().ToArray();
                if (itens.Length == 0)
                    return ("[]", "LISTA VAZIA - nada carregado; nao equivale a inexistente na origem");
                return (valor.GetRawText(), "LISTA - " + itens.Length + " item(ns)");

            case JsonValueKind.Object:
                if (valor.TryGetProperty("__deferred", out _))
                    return (valor.GetRawText(),
                        "NAVEGACAO DIFERIDA (__deferred) - NAO carregado nesta resposta");
                return (valor.GetRawText(), "OBJETO");

            default:
                return (string.Empty, Ausente);
        }
    }

    /// <summary>
    /// Reconhece /Date(1234567890000)/ e /Date(1234567890000+0000)/ e devolve o
    /// instante UTC derivado dos milissegundos de epoch. O deslocamento, quando vem,
    /// e mantido no texto original e NAO e aplicado: aplicar seria conversao implicita.
    /// </summary>
    internal static bool TentarInstanteOData(string texto, out string utcIso)
    {
        utcIso = string.Empty;
        if (texto.Length < 8 || !texto.StartsWith("/Date(", StringComparison.Ordinal)
            || !texto.EndsWith(")/", StringComparison.Ordinal)) return false;
        var interior = texto[6..^2];
        var corte = interior.IndexOfAny(['+', '-'], 1);
        var milissegundos = corte > 0 ? interior[..corte] : interior;
        if (!long.TryParse(milissegundos, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var epoch)) return false;
        try
        {
            utcIso = DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
