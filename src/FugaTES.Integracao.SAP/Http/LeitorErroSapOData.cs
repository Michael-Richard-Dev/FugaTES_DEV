using System.Text.Json;

namespace FugaTES.Integracao.SAP.Http;

public sealed record MensagemSap(string Codigo, string Severidade, string Texto);

/// <summary>
/// Le o corpo de erro do OData do SAP, INCLUSIVE a lista errordetails.
///
/// Existe por um caso concreto de 01/11/2026: a criacao de remessa falhou com
/// "VL/367 - An item with no delivery quantity is not permitted", e a causa real estava numa
/// mensagem de severidade "info" logo acima, "VL/150 - Only 0,000 L of material available".
/// Quem guarda so o codigo do topo entrega ao operador a consequencia e esconde o motivo.
///
/// Por isso a ordem e preservada e a severidade vai junto: a primeira mensagem costuma explicar,
/// e a ultima costuma ser o erro que derrubou a chamada.
/// </summary>
public static class LeitorErroSapOData
{
    public static IReadOnlyList<MensagemSap> Ler(string? corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return [];
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            if (!doc.RootElement.TryGetProperty("error", out var erro)) return [];

            var itens = new List<MensagemSap>();
            // Detalhes primeiro: e neles que mora a causa.
            if (erro.TryGetProperty("innererror", out var inner)
                && inner.TryGetProperty("errordetails", out var detalhes)
                && detalhes.ValueKind == JsonValueKind.Array)
                foreach (var d in detalhes.EnumerateArray())
                    if (Montar(d) is { } m) itens.Add(m);

            // A mensagem do topo entra se ainda nao tiver vindo nos detalhes - costuma repetir.
            if (Montar(erro) is { } principal
                && !itens.Any(i => string.Equals(i.Codigo, principal.Codigo, StringComparison.Ordinal)))
                itens.Add(principal);

            return itens;
        }
        // Corpo que nao e JSON (o SAP devolve Atom quando o Accept pede XML) nao vira excecao:
        // devolver vazio deixa quem chamou mostrar o corpo cru, que e melhor que nada.
        catch (JsonException) { return []; }
    }

    /// <summary>Uma linha por mensagem, na ordem em que o SAP as devolveu.</summary>
    public static string Resumir(IReadOnlyList<MensagemSap> mensagens, int limite = 600)
    {
        if (mensagens.Count == 0) return "";
        var texto = string.Join(" | ", mensagens.Select(m =>
            $"{m.Codigo} ({m.Severidade}): {m.Texto}"));
        return texto.Length <= limite ? texto : texto[..limite] + "...";
    }

    private static MensagemSap? Montar(JsonElement no)
    {
        if (no.ValueKind != JsonValueKind.Object) return null;
        var codigo = Texto(no, "code");
        var severidade = Texto(no, "severity") ?? "error";
        var texto = Mensagem(no);
        if (codigo is null && texto is null) return null;
        return new(codigo ?? "", severidade, (texto ?? "").Trim());
    }

    // "message" vem como texto solto ou como { "lang": ..., "value": ... }, conforme o endpoint.
    private static string? Mensagem(JsonElement no)
    {
        if (!no.TryGetProperty("message", out var m)) return null;
        return m.ValueKind switch
        {
            JsonValueKind.String => m.GetString(),
            JsonValueKind.Object => Texto(m, "value"),
            _ => null
        };
    }

    private static string? Texto(JsonElement no, string nome)
        => no.ValueKind == JsonValueKind.Object && no.TryGetProperty(nome, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
