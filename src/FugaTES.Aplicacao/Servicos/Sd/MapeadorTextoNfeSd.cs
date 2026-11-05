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

    // Cada dado aceita mais de um rotulo, porque existem dois modelos de texto no cadastro. O
    // primeiro da lista e o canonico: e o que vai ao texto quando nenhum deles existe.
    private static readonly (string[] Rotulos, Func<ComplementoTextoNfeSd, string?> Valor)[] Campos =
    [
        (TextoNfeSd.RotulosLacre, c => c.Lacre),
        // As DUAS placas no mesmo rotulo, que ja nasce no plural num dos modelos.
        (TextoNfeSd.RotulosPlaca, c => c.Placas),
        (TextoNfeSd.RotulosMotorista, c => c.Motorista),
        (TextoNfeSd.RotulosCpf, c => c.Cpf),
        (TextoNfeSd.RotulosTransportadora, c => c.Transportadora)
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
                .Select(c => (Rotulo: c.Rotulos[0], Valor: Limpar(c.Valor(complemento))))
                .Where(c => c.Valor is not null)
                .Select(c => c.Rotulo + " " + c.Valor));

        var texto = textoAtual;
        var pendentes = new List<string>();

        foreach (var (rotulos, obter) in Campos)
        {
            var valor = Limpar(obter(complemento));
            if (valor is null) continue;

            var posicao = PosicaoDoEspaco(texto, rotulos);
            // Nenhum dos rotulos existe no texto: o dado vai ao fim, com o canonico.
            if (posicao is null) { pendentes.Add(rotulos[0] + " " + valor); continue; }

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
    /// <summary>
    /// O espaco do primeiro <paramref name="rotulos"/> que existir no texto. Varios porque o mesmo
    /// dado tem rotulo diferente em cada um dos dois modelos de texto do cadastro.
    /// </summary>
    private static (int Inicio, int Fim)? PosicaoDoEspaco(string texto, string[] rotulos)
    {
        (int Inicio, int Tamanho)? alvo = null;
        foreach (var rotulo in rotulos)
            if (Achar(texto, rotulo, 0) is { } achado) { alvo = achado; break; }
        if (alvo is not { } encontrado) return null;

        var inicio = encontrado.Inicio + encontrado.Tamanho;

        var fim = texto.IndexOf('\n', inicio);
        if (fim < 0) fim = texto.Length;

        foreach (var outro in TextoNfeSd.Rotulos)
        {
            // Um rotulo do PROPRIO campo nao delimita o espaco dele.
            if (rotulos.Contains(outro, StringComparer.OrdinalIgnoreCase)) continue;
            // So conta o que estiver entre o inicio do espaco e o fim da linha.
            if (Achar(texto, outro, inicio) is { } seguinte && seguinte.Inicio < fim)
                fim = seguinte.Inicio;
        }

        return (inicio, fim);
    }

    /// <summary>
    /// Onde um rotulo comeca no texto, e QUANTO ele ocupou - que pode diferir do tamanho do
    /// proprio rotulo, porque a comparacao tolera diferenca de espaco em branco.
    ///
    /// A tolerancia existe por um caso real: o rotulo dos envelopes estava escrito
    /// "amostrastestemunhas" e o SAP devolve "amostras testemunhas". O rotulo deixava de ser
    /// reconhecido, o espaco do LACRE passava por cima dele ate o rotulo seguinte, parecia ja
    /// preenchido, e o lacre era descartado sem uma linha de aviso (remessa 80003915, 05/11/2026).
    ///
    /// Um caractere de espaco no rotulo casa com QUALQUER quantidade de espaco no texto, inclusive
    /// nenhuma. Rotulo sem espaco - "Mot.:", "CPF:", "Placas:" - compara exatamente como antes.
    /// </summary>
    private static (int Inicio, int Tamanho)? Achar(string texto, string rotulo, int desde)
    {
        for (var i = Math.Max(desde, 0); i <= texto.Length; i++)
        {
            var tamanho = Casa(texto, i, rotulo);
            if (tamanho >= 0) return (i, tamanho);
        }
        return null;
    }

    /// <summary>
    /// Quantos caracteres do texto o rotulo ocupa a partir de <paramref name="inicio"/>, ou -1
    /// quando nao casa.
    /// </summary>
    private static int Casa(string texto, int inicio, string rotulo)
    {
        var t = inicio;
        foreach (var c in rotulo)
        {
            if (char.IsWhiteSpace(c))
            {
                while (t < texto.Length && char.IsWhiteSpace(texto[t])) t++;
                continue;
            }
            // Sem distinguir caixa: um modelo escreve "LACRES:" e o outro "Lacres:", e sao o
            // mesmo rotulo. Decisao do operador em 05/11/2026.
            if (t >= texto.Length || char.ToUpperInvariant(texto[t]) != char.ToUpperInvariant(c))
                return -1;
            t++;
        }
        return t - inicio;
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
                Lacre: Lacres(raiz),
                Placa: Texto(raiz, "Placa"),
                Motorista: Texto(raiz, "NomeMotorista"),
                Cpf: Texto(raiz, "CpfMotorista"),
                // Carreta e transportadora chegam na RAIZ, por nome. Nao ha numero reservado para
                // elas: os que havia (1010, 1011) passaram a ser lacre.
                PlacaCarreta: Complemento(raiz, numero: -1, NomesCarreta),
                TransportadoraCodigo: Complemento(raiz, numero: -1, NomesTransportadoraCodigo),
                TransportadoraNome: Complemento(raiz, numero: -1, NomesTransportadoraNome));
        }
        catch (JsonException) { return new(); }
    }

    // ------------------------------------------------------------------------------------------
    // O CONTRATO COM O GUARDIAN, para os tres dados que ele passara a enviar.
    //
    // Em 05/11/2026 ficou provado, no banco, que a devolutiva trazia APENAS os campos adicionais
    // 1003 (ordem) e 1009 (linha) - nenhum lacre, transportadora ou carreta, em nenhuma pesagem, e
    // com "Observacao" vazia em todas. Nao era defeito de leitura: o dado nao vinha.
    //
    // Como o operador vai acrescenta-los do lado do Guardian, estas listas deixam de ser palpite e
    // passam a ser CONTRATO: e isto que o FugaTES le, e basta o Guardian mandar em qualquer uma
    // das formas. Cada dado e procurado em tres lugares, nesta ordem:
    //
    //   1. campo adicional pelo NUMERO reservado (a forma preferida - e como 1003 e 1009 chegam);
    //   2. campo adicional pelo NOME, para quando o Guardian nomear em vez de numerar;
    //   3. chave solta na raiz, como ja acontece com Placa, NomeMotorista e CpfMotorista.
    //
    // Nada e truncado em lugar nenhum: o valor vai para o texto como veio ("nao precisa limitar a
    // quantidade de caracteres" - o operador, 05/11/2026).
    // ------------------------------------------------------------------------------------------

    private static readonly string[] NomesLacre = ["Lacre", "Lacres", "NumeroLacre"];

    private static readonly string[] NomesCarreta =
        ["PlacaCarreta", "PlacaReboque", "PlacaSemiReboque", "PlacaVagao", "PlacaCarretaVagao",
         "Placa da Carreta", "Placa Carreta", "Placa da Carreta / Vagao", "Placa da Carreta / Vagão"];

    // O Guardian manda transportadora em DOIS campos, e o operador quer os dois no texto
    // (05/11/2026): "14300001 - Transporte Jabuti". Por isso duas listas, e nao uma.
    private static readonly string[] NomesTransportadoraCodigo =
        ["IdTransportadora", "CodigoTransportadora"];

    private static readonly string[] NomesTransportadoraNome =
        ["NomeTransportadora", "RazaoSocialTransportadora", "Transportadora", "Transportador"];

    /// <summary>
    /// TODOS os lacres da pesagem, numa linha so.
    ///
    /// Diferente dos outros dados do complemento: aqui nao vence o primeiro que responder - vale
    /// cada campo que tiver conteudo, porque uma carga pode levar varios lacres e o Guardian os
    /// entrega em campos separados (1006 e, desde 05/11/2026, de 1010 a 1013).
    ///
    /// Campo zerado ou em branco some, a pedido do operador: o 1006 volta "0" quando ninguem
    /// digita, porque e o valor que NOS enviamos no protocolo, e os demais vem vazios quando a
    /// carga tem menos lacres do que campos. Imprimir "Lacres: 0, 0, 0" seria pior do que nao
    /// imprimir nada.
    ///
    /// Valor repetido entra uma vez so: o mesmo lacre em dois campos e erro de digitacao na
    /// balanca, e a nota nao precisa herdar o erro.
    /// </summary>
    private static string? Lacres(JsonElement raiz)
    {
        var achados = new List<string>();

        void Juntar(string? valor)
        {
            if (SemZero(valor) is not string limpo) return;
            if (!achados.Contains(limpo, StringComparer.OrdinalIgnoreCase)) achados.Add(limpo);
        }

        // Na ordem dos numeros, que e a ordem em que o operador os preenche na balanca.
        foreach (var numero in CamposProtocoloSd.Lacres) Juntar(CampoAdicionalPorNumero(raiz, numero));
        foreach (var nome in NomesLacre) Juntar(CampoAdicionalPorNome(raiz, nome));
        foreach (var nome in NomesLacre) Juntar(Texto(raiz, nome));

        return achados.Count == 0 ? null : string.Join(", ", achados);
    }

    /// <summary>
    /// Um dado do complemento, procurado nas tres formas do contrato acima. A primeira que
    /// responder vence; nao achar devolve nulo, e nulo apenas nao entra no texto.
    /// </summary>
    private static string? Complemento(JsonElement raiz, int numero, string[] nomes)
    {
        if (SemZero(CampoAdicionalPorNumero(raiz, numero)) is string porNumero) return porNumero;
        foreach (var nome in nomes)
            if (SemZero(CampoAdicionalPorNome(raiz, nome)) is string porNome) return porNome;
        foreach (var nome in nomes)
            if (SemZero(Texto(raiz, nome)) is string naRaiz) return naRaiz;
        return null;
    }

    /// <summary>
    /// ZERO NAO E DADO. O protocolo que enviamos preenche os campos adicionais que nao se aplicam
    /// com "0" (<c>CamposProtocoloSd.NaoSeAplica</c>) - inclusive o 1006 do lacre, que no envio
    /// ainda nao existe. Se o Guardian devolver esse campo sem ninguem ter digitado nada, ele volta
    /// como "0", e sem este corte a NF-e sairia com "Lacres: 0" impresso.
    ///
    /// E a mesma licao que custou caro em 03/11/2026, quando zeros nos campos 1003 e 1009 viraram
    /// a ordem de venda "0/0" e gravaram pesagens correlacionadas com uma ordem que nao existe.
    /// </summary>
    private static string? SemZero(string? valor)
    {
        var limpo = valor?.Trim();
        if (string.IsNullOrEmpty(limpo)) return null;
        return limpo.All(c => c == '0') ? null : limpo;
    }

    /// <summary>
    /// A lista de campos adicionais da devolutiva.
    ///
    /// O NOME DELA E "CamposAdicionais", com C maiusculo - conferido no payload real em
    /// 05/11/2026. Ate aqui o codigo procurava "camposAdicionais", e TryGetProperty diferencia
    /// maiusculas: a lista nunca era encontrada e o lacre nunca entrava no texto, mesmo quando
    /// viesse. As duas grafias sao aceitas porque o protocolo que ENVIAMOS usa a minuscula, e um
    /// leitor que so entende uma das duas quebra no dia em que a outra aparecer.
    /// </summary>
    private static JsonElement? Lista(JsonElement raiz)
    {
        foreach (var nome in new[] { "CamposAdicionais", "camposAdicionais" })
            if (raiz.TryGetProperty(nome, out var lista) && lista.ValueKind == JsonValueKind.Array)
                return lista;
        return null;
    }

    /// <summary>
    /// Valor do campo adicional com este numero. As chaves internas sao minusculas - "numero" e
    /// "valor" -, conferido no mesmo payload; o numero chega como numero JSON, nao como texto.
    /// </summary>
    private static string? CampoAdicionalPorNumero(JsonElement raiz, int numero)
    {
        // Negativo significa "este dado nao tem numero reservado" - e o caso do codigo da
        // transportadora, que so chega por chave propria. Explicito para nao parecer descuido.
        if (numero < 0) return null;
        if (Lista(raiz) is not { } lista) return null;
        foreach (var campo in lista.EnumerateArray())
        {
            if (campo.ValueKind != JsonValueKind.Object) continue;
            if (!campo.TryGetProperty("numero", out var n) && !campo.TryGetProperty("Numero", out n))
                continue;
            var bate = n.ValueKind switch
            {
                JsonValueKind.Number => n.TryGetInt32(out var v) && v == numero,
                JsonValueKind.String => int.TryParse(n.GetString(), out var v) && v == numero,
                _ => false
            };
            if (bate && Valor(campo) is string texto) return texto;
        }
        return null;
    }

    private static string? CampoAdicionalPorNome(JsonElement raiz, string nome)
    {
        if (Lista(raiz) is not { } lista) return null;
        foreach (var campo in lista.EnumerateArray())
        {
            if (campo.ValueKind != JsonValueKind.Object) continue;
            var achado = Texto(campo, "nome") ?? Texto(campo, "Nome");
            if (achado is not null && achado.Equals(nome, StringComparison.OrdinalIgnoreCase)
                && Valor(campo) is string texto) return texto;
        }
        return null;
    }

    private static string? Valor(JsonElement campo) => Texto(campo, "valor") ?? Texto(campo, "Valor");

    private static string? Etag(JsonElement item)
        => item.TryGetProperty("__metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
            ? Texto(meta, "etag") : null;

    private static string? Texto(JsonElement objeto, string nome)
        => objeto.ValueKind == JsonValueKind.Object && objeto.TryGetProperty(nome, out var v)
            && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()!.Trim() : null;
}
