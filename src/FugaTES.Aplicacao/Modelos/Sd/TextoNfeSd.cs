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
        // "amostras testemunhas", separado. Estava grudado aqui, e o texto do SAP traz com espaco:
        // o rotulo nao era reconhecido, o espaco do lacre se estendia por cima dele ate "Mot.:",
        // parecia ja preenchido, e o lacre era descartado em silencio. Foi o que aconteceu na
        // remessa 80003915, em 05/11/2026, com o lacre 0150059,0150060 chegando e nao entrando.
        // A comparacao agora tolera diferenca de espaco (ver MapeadorTextoNfeSd.Casa), entao a
        // forma grudada de eventuais ordens antigas tambem continua sendo reconhecida.
        "N dos envelopes de seguranca das amostras testemunhas:",
        // Os dois modelos. A comparacao ignora caixa, entao "MOTORISTA:" cai em "Motorista:".
        "Mot.:", "Motorista:", "CPF:", "Placas:", "Placa:", "Lacre:", "Transportadora:"
    ];

    /// <summary>Rotulo de cada dado da balanca, na ordem em que sao acrescentados ao fim.</summary>
    public const string RotuloLacre = "Lacres:";
    public const string RotuloPlaca = "Placas:";
    public const string RotuloMotorista = "Mot.:";
    public const string RotuloCpf = "CPF:";

    /// <summary>
    /// EXISTEM DOIS MODELOS DE TEXTO no cadastro, confirmados pelo operador em 05/11/2026.
    ///
    /// Um escreve "Lacres:", "Placas:" e "Mot.:" numa linha so; o outro escreve "LACRES:",
    /// "PLACA:" e "MOTORISTA:", cada um na sua linha. Na remessa 80003917 - ordem 4292, setor 65 -
    /// nenhum rotulo nosso casou, e os valores foram todos para o fim do texto como linhas
    /// pendentes: a nota ficava com "PLACA:" vazio em cima e "Placas: JAK3F79" embaixo.
    ///
    /// A comparacao ignora caixa (ver MapeadorTextoNfeSd.Casa), entao "LACRES:" e "Lacres:" sao o
    /// mesmo rotulo e nao precisam de duas entradas. O que precisa de entrada propria e a palavra
    /// diferente: "PLACA:" no singular nao e "Placas:", e "MOTORISTA:" nao e "Mot.:".
    ///
    /// O PRIMEIRO de cada lista e o canonico: e ele que vai ao texto quando nenhum dos rotulos
    /// existe e o dado precisa ser acrescentado ao fim.
    /// </summary>
    public static readonly string[] RotulosLacre = [RotuloLacre, "Lacre:"];
    public static readonly string[] RotulosPlaca = [RotuloPlaca, "Placa:"];
    public static readonly string[] RotulosMotorista = [RotuloMotorista, "Motorista:"];
    public static readonly string[] RotulosCpf = [RotuloCpf];
    public static readonly string[] RotulosTransportadora = [RotuloTransportadora];

    /// <summary>
    /// Transportadora, pedida pelo operador em 05/11/2026. Diferente dos quatro acima, este
    /// rotulo pode NAO existir no texto que vem da ordem - e tudo bem: quem nao acha o seu espaco
    /// vai numa linha ao fim, que e a regra 3 da composicao. O dado nao se perde.
    /// </summary>
    public const string RotuloTransportadora = "Transportadora:";
}

/// <summary>O que a balanca tem a acrescentar. Campo nulo ou vazio significa "nao tenho".</summary>
public sealed record ComplementoTextoNfeSd(
    string? Lacre = null, string? Placa = null, string? Motorista = null, string? Cpf = null,
    // Placa da CARRETA, pedida pelo operador em 04/11/2026. Vai junto com a do veiculo, no mesmo
    // rotulo "Placas:" - que ja nasce no plural no texto da ordem de venda.
    string? PlacaCarreta = null,
    // Transportadora, pedida em 05/11/2026: CODIGO E NOME, os dois no mesmo rotulo proprio -
    // "14300001 - Transporte Jabuti". O Guardian manda os dois campos separados.
    string? TransportadoraCodigo = null,
    string? TransportadoraNome = null)
{
    public bool TemAlgo => !string.IsNullOrWhiteSpace(Lacre) || !string.IsNullOrWhiteSpace(Placa)
        || !string.IsNullOrWhiteSpace(Motorista) || !string.IsNullOrWhiteSpace(Cpf)
        || !string.IsNullOrWhiteSpace(PlacaCarreta) || !string.IsNullOrWhiteSpace(Transportadora);

    /// <summary>
    /// A transportadora como vai para o texto: "codigo - nome". So um deles, vai sozinho - perder
    /// o dado seria pior do que mostra-lo incompleto, a mesma regra das duas placas.
    /// </summary>
    public string? Transportadora
    {
        get
        {
            var codigo = TransportadoraCodigo?.Trim();
            var nome = TransportadoraNome?.Trim();
            if (string.IsNullOrEmpty(codigo)) return string.IsNullOrEmpty(nome) ? null : nome;
            if (string.IsNullOrEmpty(nome)) return codigo;
            // Codigo e nome iguais nao viram "X - X".
            return string.Equals(codigo, nome, StringComparison.OrdinalIgnoreCase)
                ? nome : codigo + " - " + nome;
        }
    }

    /// <summary>
    /// As duas placas como vao para o texto: "veiculo / carreta". So uma delas, vai sozinha; a
    /// carreta sem o veiculo tambem - perder o dado seria pior do que mostra-lo incompleto.
    /// </summary>
    public string? Placas
    {
        get
        {
            var veiculo = Placa?.Trim();
            var carreta = PlacaCarreta?.Trim();
            if (string.IsNullOrEmpty(veiculo)) return string.IsNullOrEmpty(carreta) ? null : carreta;
            if (string.IsNullOrEmpty(carreta)) return veiculo;
            // Mesma placa nos dois campos nao vira "ABC1234 / ABC1234".
            return string.Equals(veiculo, carreta, StringComparison.OrdinalIgnoreCase)
                ? veiculo : veiculo + " / " + carreta;
        }
    }
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
