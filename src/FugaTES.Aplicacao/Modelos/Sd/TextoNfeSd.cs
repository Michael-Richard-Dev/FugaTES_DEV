using System.Text.Json.Serialization;

namespace FugaTES.Aplicacao.Modelos.Sd;

/// <summary>
/// Passo 5: o texto de informacoes complementares da NF-e (elemento ZNFE) no cabecalho da remessa.
///
/// O texto NASCE da ordem de venda, com estrutura fixa e alguns espacos em branco - vendedor,
/// banco, pedido, contrato, temperatura, densidade, lacres, motorista, placas. O que a balanca
/// faz e PREENCHER os espacos vazios, nao escrever um texto novo.
///
/// Isto e o ponto perigoso do passo 5: o PATCH substitui o texto INTEIRO. Mandar so a nossa parte
/// apagaria vendedor, contrato e laudo. Por isso nada e composto sem o texto atual em maos.
/// </summary>
public static class TextoNfeSd
{
    public const string Elemento = "ZNFE";
    public const string Idioma = "PT";

    /// <summary>
    /// Rotulos conhecidos do texto que vem da ordem. Servem para delimitar onde termina o espaco
    /// de um campo e comeca o proximo - na mesma linha ha varios. Sem esta lista, "Lacres:" em
    /// "Lacres:  N dos envelopes...: 65593" pareceria preenchido.
    /// </summary>
    public static readonly string[] Rotulos =
    [
        "VENDEDOR:", "BANCO:", "PEDIDO(S):", "Contrato Nro:", "Agendamento:",
        "Temperatura do CT:", "Temperatura:", "Densidade a 20 C:", "Densidade:",
        "Fator Correcao:", "LAUDO DE CERTIFICACAO:", "Lacres:",
        "N dos envelopes de seguranca das amostrastestemunhas:",
        "Mot.:", "CPF:", "Placas:"
    ];

    /// <summary>Rotulo de cada dado da balanca, na ordem em que sao acrescentados ao fim.</summary>
    public const string RotuloLacre = "Lacres:";
    public const string RotuloPlaca = "Placas:";
    public const string RotuloMotorista = "Mot.:";
    public const string RotuloCpf = "CPF:";
}

/// <summary>O que a balanca tem a acrescentar. Campo nulo ou vazio significa "nao tenho".</summary>
public sealed record ComplementoTextoNfeSd(
    string? Lacre = null, string? Placa = null, string? Motorista = null, string? Cpf = null)
{
    public bool TemAlgo => !string.IsNullOrWhiteSpace(Lacre) || !string.IsNullOrWhiteSpace(Placa)
        || !string.IsNullOrWhiteSpace(Motorista) || !string.IsNullOrWhiteSpace(Cpf);
}

/// <summary>O texto como esta hoje no SAP. Nulo em <see cref="Texto"/> significa que nao existe.</summary>
public sealed record TextoNfeSdAtual(string? Texto, string? ETag)
{
    public bool Existe => Texto is not null;
}

/// <summary>Corpo do PATCH: so o texto muda.</summary>
public sealed record AlterarTextoNfeSdRequest(
    [property: JsonPropertyName("TextElementText")] string Texto);

/// <summary>Corpo do POST, quando ainda nao ha texto: precisa dizer qual elemento e qual idioma.</summary>
public sealed record CriarTextoNfeSdRequest(
    [property: JsonPropertyName("TextElement")] string Elemento,
    [property: JsonPropertyName("Language")] string Idioma,
    [property: JsonPropertyName("TextElementText")] string Texto);
