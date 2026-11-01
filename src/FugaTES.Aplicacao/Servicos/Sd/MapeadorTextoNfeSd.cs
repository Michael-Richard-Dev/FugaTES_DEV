using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Sd;

namespace FugaTES.Aplicacao.Servicos.Sd;

/// <summary>
/// Passo 5: compoe o texto ZNFE preenchendo os espacos vazios do que veio da ordem de venda.
///
/// Tres regras, nesta ordem de importancia:
///   1. nada do texto existente e removido ou reescrito;
///   2. espaco JA preenchido pela ordem nao e tocado - o que veio do negocio vale mais do que o
///      que veio da balanca;
///   3. dado que nao encontrou o seu rotulo nao se perde: vai numa linha ao fim, visivel.
/// </summary>
public static class MapeadorTextoNfeSd
{
    public static readonly JsonSerializerOptions JsonOpcoes = new(JsonSerializerDefaults.Web);

    public static string Serializar<T>(T corpo) => JsonSerializer.Serialize(corpo, JsonOpcoes);

    private static readonly (string Rotulo, Func<ComplementoTextoNfeSd, string?> Valor)[] Campos =
    [
        (TextoNfeSd.RotuloLacre, c => c.Lacre),
        (TextoNfeSd.RotuloPlaca, c => c.Placa),
        (TextoNfeSd.RotuloMotorista, c => c.Motorista),
        (TextoNfeSd.RotuloCpf, c => c.Cpf)
    ];

    /// <summary>
    /// Texto final. Com <paramref name="textoAtual"/> nulo ou vazio (remessa sem texto ainda),
    /// devolve so as linhas da balanca - nao ha o que preservar.
    /// </summary>
    public static string Compor(string? textoAtual, ComplementoTextoNfeSd complemento)
    {
        ArgumentNullException.ThrowIfNull(complemento);
        if (!complemento.TemAlgo)
            throw new RemessaSdBloqueadaException("SD_TEXTO_SEM_COMPLEMENTO",
                "A pesagem não trouxe lacre, placa, motorista nem CPF; não há o que acrescentar.");

        if (string.IsNullOrWhiteSpace(textoAtual))
            return string.Join("\n", Campos
                .Select(c => (c.Rotulo, Valor: Limpar(c.Valor(complemento))))
                .Where(c => c.Valor is not null)
                .Select(c => c.Rotulo + " " + c.Valor));

        var texto = textoAtual;
        var pendentes = new List<string>();

        foreach (var (rotulo, obter) in Campos)
        {
            var valor = Limpar(obter(complemento));
            if (valor is null) continue;

            var posicao = PosicaoDoEspaco(texto, rotulo);
            if (posicao is null) { pendentes.Add(rotulo + " " + valor); continue; }

            var (inicio, fim) = posicao.Value;
            // Ja preenchido pela ordem: nao se toca. Regra 2.
            if (texto[inicio..fim].Trim().Length > 0) continue;

            // O espaco em branco que estava ali tambem SEPARAVA deste campo o proximo da linha.
            // Sem repor a separacao, "Lacres:" e "N dos envelopes" ficariam colados.
            var separa = fim < texto.Length && texto[fim] != '\n';
            texto = texto[..inicio] + " " + valor + (separa ? " " : "") + texto[fim..];
        }

        return pendentes.Count == 0 ? texto : texto.TrimEnd() + "\n" + string.Join("\n", pendentes);
    }

    /// <summary>
    /// Onde comeca e termina o espaco de um rotulo: do fim dele ate o proximo rotulo conhecido,
    /// ou ate o fim da linha. Devolve nulo quando o rotulo nao existe no texto.
    /// </summary>
    private static (int Inicio, int Fim)? PosicaoDoEspaco(string texto, string rotulo)
    {
        var achado = texto.IndexOf(rotulo, StringComparison.Ordinal);
        if (achado < 0) return null;
        var inicio = achado + rotulo.Length;

        var fim = texto.IndexOf('\n', inicio);
        if (fim < 0) fim = texto.Length;

        foreach (var outro in TextoNfeSd.Rotulos)
        {
            if (outro == rotulo) continue;
            var proximo = texto.IndexOf(outro, inicio, StringComparison.Ordinal);
            // So conta o que estiver entre o inicio do espaco e o fim da linha.
            if (proximo >= inicio && proximo < fim) fim = proximo;
        }

        return (inicio, fim);
    }

    private static string? Limpar(string? valor)
    {
        var limpo = valor?.Trim();
        return string.IsNullOrEmpty(limpo) ? null : limpo;
    }

    /// <summary>
    /// Le o texto atual da resposta de to_DeliveryDocumentText, escolhendo a entrada do elemento
    /// ZNFE em portugues. Outro elemento nao serve: cada um vai para um lugar da NF-e.
    /// </summary>
    public static TextoNfeSdAtual LerTextoAtual(string? corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return new(null, null);
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var raiz = doc.RootElement;
            if (raiz.ValueKind == JsonValueKind.Object && raiz.TryGetProperty("d", out var d)) raiz = d;
            var lista = raiz.ValueKind == JsonValueKind.Object && raiz.TryGetProperty("results", out var r)
                ? r : raiz;
            if (lista.ValueKind != JsonValueKind.Array) return new(null, null);

            foreach (var item in lista.EnumerateArray())
            {
                if (Texto(item, "TextElement") != TextoNfeSd.Elemento) continue;
                if (Texto(item, "Language") is string idioma
                    && !idioma.Equals(TextoNfeSd.Idioma, StringComparison.OrdinalIgnoreCase)) continue;
                // Texto existente e vazio sao coisas diferentes de texto inexistente; "" conta
                // como existente, e o PATCH e que vale, nao o POST.
                var valor = item.TryGetProperty("TextElementText", out var t)
                    && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                return new(valor, Etag(item));
            }
            return new(null, null);
        }
        catch (JsonException) { return new(null, null); }
    }

    /// <summary>
    /// Tira do documento que o Guardian enviou o que a balanca tem a acrescentar. O lacre vem em
    /// campo adicional; placa, motorista e CPF vem na raiz. Nada e inventado: o que nao estiver
    /// no documento fica nulo e simplesmente nao entra no texto.
    /// </summary>
    public static ComplementoTextoNfeSd LerComplemento(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return new();
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object) return new();
            return new(
                Lacre: CampoAdicional(raiz, CamposProtocoloSd.Lacre, "Lacre") ?? Texto(raiz, "Lacre"),
                Placa: Texto(raiz, "Placa"),
                Motorista: Texto(raiz, "NomeMotorista"),
                Cpf: Texto(raiz, "CpfMotorista"));
        }
        catch (JsonException) { return new(); }
    }

    /// <summary>Campo adicional por numero ou por nome, nas duas formas que o Guardian aceita.</summary>
    private static string? CampoAdicional(JsonElement raiz, int numero, string nome)
    {
        if (!raiz.TryGetProperty("camposAdicionais", out var lista)
            || lista.ValueKind != JsonValueKind.Array) return null;

        foreach (var campo in lista.EnumerateArray())
        {
            if (campo.ValueKind != JsonValueKind.Object) continue;
            var bate = campo.TryGetProperty("numero", out var n)
                    && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var v) && v == numero;
            if (!bate && Texto(campo, "nome") is string texto)
                bate = texto.Equals(nome, StringComparison.OrdinalIgnoreCase);
            if (bate) return Texto(campo, "valor");
        }
        return null;
    }

    private static string? Etag(JsonElement item)
        => item.TryGetProperty("__metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
            ? Texto(meta, "etag") : null;

    private static string? Texto(JsonElement objeto, string nome)
        => objeto.ValueKind == JsonValueKind.Object && objeto.TryGetProperty(nome, out var v)
            && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()!.Trim() : null;
}
