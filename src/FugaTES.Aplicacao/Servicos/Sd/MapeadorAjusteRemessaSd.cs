using System.Globalization;
using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Sd;

namespace FugaTES.Aplicacao.Servicos.Sd;

/// <summary>
/// Passo 4: monta os corpos do ajuste da remessa a partir da pesagem e do setor de atividade.
///
/// Mesmo cuidado do passo 3: um unico <see cref="Serializar"/>, para que a previa na tela e os
/// bytes que vao ao SAP sejam o mesmo texto.
/// </summary>
public static class MapeadorAjusteRemessaSd
{
    public static readonly JsonSerializerOptions JsonOpcoes = new(JsonSerializerDefaults.Web);

    public static string Serializar<T>(T corpo) => JsonSerializer.Serialize(corpo, JsonOpcoes);

    /// <summary>Tres casas, como o SAP devolve os pesos ("100.000").</summary>
    public static string Peso(decimal valor)
        => valor.ToString("0.000", CultureInfo.InvariantCulture);

    public static AjusteCabecalhoRemessaSdRequest MontarCabecalho(
        string? setorAtividade, decimal pesoBruto, decimal pesoLiquido, string? placa)
    {
        var comPeso = AjusteRemessaSd.AjustaPeso(setorAtividade);

        // A placa vai como veio da balanca. O documento mostra "ABC1024/RS", com a UF, mas a
        // devolutiva do Guardian nao traz estado nenhum; compor um seria inventar um dado que
        // termina na NF-e. Combinado com o operador em 01/11/2026: so a placa por enquanto.
        var meio = (placa ?? "").Trim();
        if (meio.Length == 0)
            throw new RemessaSdBloqueadaException("SD_AJUSTE_PLACA_AUSENTE",
                "A pesagem não trouxe placa; o meio de transporte iria vazio para a NF-e.");

        var pedido = new AjusteCabecalhoRemessaSdRequest(meio, AjusteRemessaSd.TipoVeiculo);
        if (!comPeso) return pedido;

        if (pesoBruto <= 0 || pesoLiquido <= 0)
            throw new RemessaSdBloqueadaException("SD_AJUSTE_PESO_INVALIDO",
                "Peso bruto e líquido precisam ser maiores que zero para ir ao cabeçalho.");
        if (pesoLiquido > pesoBruto)
            throw new RemessaSdBloqueadaException("SD_AJUSTE_PESO_INCOERENTE",
                $"Peso líquido ({Peso(pesoLiquido)}) maior que o bruto ({Peso(pesoBruto)}).");

        // OS DOIS CAMPOS DO CABECALHO LEVAM O PESO LIQUIDO. Decisao do operador em 04/11/2026:
        // onde ia o bruto passa a ir o liquido tambem.
        //
        // O peso bruto continua sendo lido e conferido acima - ele e que diz se a pesagem faz
        // sentido (liquido maior que bruto e balanca com problema, e isso ainda bloqueia). O que
        // mudou e so o que viaja para o SAP.
        //
        // A propriedade continua se chamando PesoBruto porque o campo do SAP e HeaderGrossWeight;
        // o nome e do contrato deles, o valor e nosso.
        return pedido with { PesoBruto = Peso(pesoLiquido), PesoLiquido = Peso(pesoLiquido) };
    }

    /// <summary>Corpo fixo do item zerado. Existe para a previa mostrar exatamente o que vai.</summary>
    public static AjusteItemRemessaSdRequest MontarItemZerado() => new();

    /// <summary>
    /// Le a remessa como o SAP a devolve, com os itens expandidos. As chaves dos itens vem daqui
    /// - o documento escreve DeliveryDocumentItem='10' e o SAP devolve "000010"; usar o que ele
    /// proprio deu evita escolher entre os dois no escuro.
    /// </summary>
    public static RemessaSdLida? LerRemessa(string? corpo, string? etagHttp)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return null;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var raiz = doc.RootElement;
            if (raiz.ValueKind == JsonValueKind.Object && raiz.TryGetProperty("d", out var d)) raiz = d;
            if (raiz.ValueKind != JsonValueKind.Object) return null;

            var numero = Texto(raiz, "DeliveryDocument");
            if (numero is null) return null;

            var itens = new List<ItemRemessaSdLido>();
            if (raiz.TryGetProperty("to_DeliveryDocumentItem", out var nav))
            {
                var lista = nav.ValueKind == JsonValueKind.Object && nav.TryGetProperty("results", out var r)
                    ? r : nav;
                if (lista.ValueKind == JsonValueKind.Array)
                    foreach (var item in lista.EnumerateArray())
                    {
                        var chave = Texto(item, "DeliveryDocumentItem");
                        if (chave is null) continue;
                        itens.Add(new(Texto(item, "DeliveryDocument") ?? numero, chave, EtagDe(item)));
                    }
            }

            return new RemessaSdLida(numero, Normalizar(etagHttp) ?? EtagDe(raiz), itens);
        }
        catch (JsonException) { return null; }
    }

    private static string? EtagDe(JsonElement elemento)
        => elemento.TryGetProperty("__metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
            ? Normalizar(Texto(meta, "etag")) : null;

    private static string? Normalizar(string? etag)
        => string.IsNullOrWhiteSpace(etag) ? null : etag.Trim();

    private static string? Texto(JsonElement objeto, string nome)
        => objeto.ValueKind == JsonValueKind.Object && objeto.TryGetProperty(nome, out var v)
            && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()!.Trim() : null;
}
