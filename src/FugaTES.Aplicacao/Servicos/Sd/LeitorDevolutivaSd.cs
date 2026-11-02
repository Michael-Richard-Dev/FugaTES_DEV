using System.Globalization;
using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Sd;

namespace FugaTES.Aplicacao.Servicos.Sd;

/// <summary>
/// Le o POST que o Guardian envia depois da pesagem, no fluxo de venda.
///
/// O formato do corpo e o mesmo ja recebido do Toledo no fluxo de recebimento. O que muda e a
/// CORRELACAO: a venda precisa saber a qual ordem e linha a pesagem pertence, e isso vem dos
/// campos adicionais 1003 (OrdemVendas) e 1009 (SD_ItDocV) - os mesmos que gravamos no protocolo.
/// O recebimento nao precisava deles, e por isso o exemplo daquele fluxo nao os traz.
///
/// Ate termos um retorno real do SD em maos, a busca aceita as formas plausiveis: por NUMERO do
/// campo (1003/1009) e por NOME (OrdemVendas/SD_ItDocV e variantes), em colecao de campos
/// adicionais ou soltos na raiz. O que NAO se faz e adivinhar: sem achar a chave, a devolutiva e
/// aceita e guardada, mas marcada SEM CORRELACAO - correlacionar por aproximacao (parceiro, centro
/// e data) acerta no teste e erra em producao quando ha duas ordens do mesmo cliente no dia.
/// </summary>
public static class LeitorDevolutivaSd
{
    public const int CampoOrdemVenda = CamposProtocoloSd.OrdemVenda;   // 1003
    public const int CampoLinhaOrdem = CamposProtocoloSd.LinhaDaOrdem; // 1009

    private static readonly string[] NomesOrdem =
        ["OrdemVendas", "OrdemVenda", "OrdemDeVendas", "SD_DocV", "ReferenceSDDocument"];
    private static readonly string[] NomesLinha =
        ["SD_ItDocV", "ItemDocV", "LinhaOrdem", "LinhaDaOrdem", "ReferenceSDDocumentItem"];

    /// <summary>
    /// Formatos de data aceitos. O Guardian manda em DOIS deles, conforme o caminho:
    ///
    ///   automatico : "11/2/2026 1:03:39 PM"   - mes/dia, sem zero a esquerda, com AM/PM
    ///   manual     : "02/11/2026 09:12:39"    - dia/mes, com zero a esquerda, 24 horas
    ///
    /// "11/2/2026" e "02/11/2026" sao a MESMA data (2 de novembro) escritas em convencoes opostas.
    /// O que desfaz a ambiguidade e o relogio: onde ha AM/PM, a ordem e mes/dia; onde o relogio e
    /// de 24 horas, a ordem e dia/mes. Por isso cada forma aparece so com o seu tipo de hora, e
    /// nao em todas as combinacoes - juntar as duas faria "03/04/2026 10:00:00" virar 3 de abril
    /// ou 4 de marco conforme a ordem da lista, em silencio.
    ///
    /// "M" e "d" aceitam um ou dois digitos na leitura; "MM" e "dd" exigem dois. Era essa a falha
    /// de 02/11/2026: a pesagem 0020340 veio com dia "2" e nenhum formato casava.
    /// </summary>
    private static readonly string[] FormatosData =
    [
        "M/d/yyyy h:mm:ss tt", "M/d/yyyy hh:mm:ss tt",
        "d/M/yyyy H:mm:ss", "d/M/yyyy HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss"
    ];

    public static DevolutivaPesagemSd Ler(string corpoJson)
    {
        if (string.IsNullOrWhiteSpace(corpoJson))
            throw new DevolutivaSdInvalidaException("SD_DEVOLUTIVA_VAZIA", "O corpo da requisição está vazio.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(corpoJson); }
        catch (JsonException erro)
        { throw new DevolutivaSdInvalidaException("SD_DEVOLUTIVA_JSON_INVALIDO", erro.Message); }

        using (doc)
        {
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object)
                throw new DevolutivaSdInvalidaException("SD_DEVOLUTIVA_JSON_INVALIDO", "O corpo não é um objeto.");

            var id = Obrigatorio(raiz, "IdPesagem");
            var centro = Obrigatorio(raiz, "Centro");
            var tipo = Obrigatorio(raiz, "TipoPesagem");
            // Venda e saida. Aceitar "E" aqui faria uma pesagem de recebimento virar remessa.
            if (!string.Equals(tipo, CamposProtocoloSd.Saida, StringComparison.OrdinalIgnoreCase))
                throw new DevolutivaSdInvalidaException("SD_DEVOLUTIVA_TIPO_NAO_E_SAIDA",
                    $"TipoPesagem '{tipo}' não pertence ao fluxo de venda.");

            if (!DateTime.TryParseExact(Obrigatorio(raiz, "DataPesagem"), FormatosData,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var data))
                throw new DevolutivaSdInvalidaException("SD_DEVOLUTIVA_DATA_INVALIDA",
                    "DataPesagem fora dos formatos conhecidos.");
            // O wire nao tem fuso. Preserva a hora local como veio, sem inventar UTC.
            data = DateTime.SpecifyKind(data, DateTimeKind.Unspecified);

            var bruto = Decimal(raiz, "PesoBruto");
            var tara = Decimal(raiz, "PesoTara");
            // O liquido vem pronto; nao e recalculado a partir de bruto e tara porque o desconto
            // de qualidade ja esta aplicado nele pela balanca.
            var liquido = Decimal(raiz, "PesoAposDesconto");

            var (ordem, linha) = Correlacao(raiz);

            return new DevolutivaPesagemSd(
                IdPesagem: id, Centro: centro, TipoPesagem: tipo.ToUpperInvariant(), DataPesagem: data,
                PesoBruto: bruto, PesoTara: tara, PesoLiquido: liquido,
                OrdemVenda: ordem, LinhaOrdem: linha,
                Placa: Texto(raiz, "Placa"), NomeMotorista: Texto(raiz, "NomeMotorista"),
                PayloadJson: corpoJson);
        }
    }

    /// <summary>
    /// Procura ordem e linha. Devolve nulos quando nao encontra - a devolutiva nao e recusada por
    /// isso, so fica sem correlacao, para o operador ver o documento e decidir.
    /// </summary>
    private static (string?, string?) Correlacao(JsonElement raiz)
    {
        var campos = CamposAdicionais(raiz).ToArray();
        var ordem = PorNumero(campos, CampoOrdemVenda) ?? PorNome(campos, NomesOrdem) ?? Primeiro(raiz, NomesOrdem);
        var linha = PorNumero(campos, CampoLinhaOrdem) ?? PorNome(campos, NomesLinha) ?? Primeiro(raiz, NomesLinha);

        var ordemLimpa = Normalizar(ordem);
        var linhaLimpa = Normalizar(linha);
        // Meia correlacao nao e correlacao: com a ordem e sem a linha nao da para montar a chave,
        // e nada pode ser feito com ela. Em 02/11/2026 a pesagem 0020341 chegou com o campo 1009
        // vazio; guardar "ordem 4233, linha nenhuma" violava a regra da tabela - que exige tudo
        // ou nada - e derrubava o receptor com 500.
        //
        // Nada se perde: o documento inteiro e guardado como veio, e a tela o mostra. Quem olhar
        // ve a ordem ali, e decide.
        return ordemLimpa is null || linhaLimpa is null ? (null, null) : (ordemLimpa, linhaLimpa);
    }

    // Aceita camposAdicionais na raiz ou dentro do primeiro item de to_Pesagem, que e onde o
    // Guardian agrupa o que pertence a cada desdobro.
    private static IEnumerable<JsonElement> CamposAdicionais(JsonElement raiz)
    {
        foreach (var caminho in new[] { raiz, Propriedade(raiz, "to_Pesagem") is { } p && p.ValueKind == JsonValueKind.Array
                                                && p.GetArrayLength() > 0 ? p[0] : default })
        {
            if (caminho.ValueKind != JsonValueKind.Object) continue;
            foreach (var nome in new[] { "camposAdicionais", "CamposAdicionais", "to_CamposAdicionais" })
            {
                var lista = Propriedade(caminho, nome);
                if (lista.ValueKind == JsonValueKind.Object) lista = Propriedade(lista, "results");
                if (lista.ValueKind != JsonValueKind.Array) continue;
                foreach (var campo in lista.EnumerateArray()) yield return campo;
            }
        }
    }

    private static string? PorNumero(JsonElement[] campos, int numero)
    {
        foreach (var campo in campos)
        {
            foreach (var chave in new[] { "numero", "Numero", "Campo", "campo" })
            {
                var valor = Propriedade(campo, chave);
                var achou = valor.ValueKind == JsonValueKind.Number && valor.TryGetInt32(out var n) && n == numero
                    || valor.ValueKind == JsonValueKind.String && int.TryParse(valor.GetString(), out var t) && t == numero;
                if (achou) return ValorDoCampo(campo);
            }
        }
        return null;
    }

    private static string? PorNome(JsonElement[] campos, string[] nomes)
    {
        foreach (var campo in campos)
        {
            foreach (var chave in new[] { "nome", "Nome", "Campo", "campo", "Descricao" })
            {
                var valor = Propriedade(campo, chave);
                if (valor.ValueKind == JsonValueKind.String && nomes.Contains(valor.GetString(), StringComparer.OrdinalIgnoreCase))
                    return ValorDoCampo(campo);
            }
        }
        return null;
    }

    private static string? ValorDoCampo(JsonElement campo)
    {
        foreach (var chave in new[] { "valor", "Valor", "ValorAferido", "Conteudo" })
        {
            var valor = Propriedade(campo, chave);
            if (valor.ValueKind == JsonValueKind.String) return valor.GetString();
            if (valor.ValueKind == JsonValueKind.Number) return valor.ToString();
        }
        return null;
    }

    private static string? Primeiro(JsonElement raiz, string[] nomes)
    {
        foreach (var nome in nomes)
        {
            var valor = Propriedade(raiz, nome);
            if (valor.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(valor.GetString()))
                return valor.GetString();
        }
        return null;
    }

    /// <summary>Mesma normalizacao do protocolo: sem os zeros a esquerda que o SAP preenche.</summary>
    private static string? Normalizar(string? valor)
        => string.IsNullOrWhiteSpace(valor) ? null : MapeadorProtocoloSd.Normalizar(valor);

    private static JsonElement Propriedade(JsonElement no, string nome)
        => no.ValueKind == JsonValueKind.Object && no.TryGetProperty(nome, out var v) ? v : default;

    private static string? Texto(JsonElement raiz, string nome)
    {
        var valor = Propriedade(raiz, nome);
        if (valor.ValueKind != JsonValueKind.String) return null;
        var texto = valor.GetString();
        return string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
    }

    private static string Obrigatorio(JsonElement raiz, string nome)
        => Texto(raiz, nome) ?? throw new DevolutivaSdInvalidaException(
            "SD_DEVOLUTIVA_CAMPO_AUSENTE", $"O campo {nome} não veio preenchido.");

    /// <summary>
    /// Peso vindo como texto, aceitando ponto OU virgula como separador decimal.
    ///
    /// Por que nao basta decimal.TryParse com InvariantCulture, que era o que havia aqui: a
    /// balanca Toledo manda "15000,0", e em InvariantCulture a virgula e separador de MILHAR.
    /// O valor nao era recusado - era aceito como 150000. Dez vezes o peso, em silencio, indo
    /// para a remessa e para a NF-e. Conferido em 01/11/2026 com o payload real do ACM.
    ///
    /// A regra e a MESMA ja usada no caminho do ACM com esta balanca (ToledoPesagemCallbackDto e
    /// ToledoSapODataPayload): virgula e separador decimal, nunca de milhar; separadores
    /// misturados sao recusados em vez de interpretados. "1.234,56" nao vira 1234,56 aqui - vira
    /// recusa, porque adivinhar a intencao de um peso e pior do que pedir que venha sem ambiguidade.
    /// </summary>
    public static bool TentarNumero(string? texto, out decimal numero)
    {
        numero = 0;
        if (string.IsNullOrWhiteSpace(texto)) return false;
        var limpo = texto.Trim().Replace(',', '.');
        if (limpo.Length > 128) return false;
        // Um separador no maximo, so digitos, sinal apenas a frente. O negativo passa por aqui
        // para ser recusado depois com o seu proprio codigo, e nao confundido com "nao e numero".
        if (!FormatoDecimal.IsMatch(limpo)) return false;
        return decimal.TryParse(limpo, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out numero);
    }

    private static readonly System.Text.RegularExpressions.Regex FormatoDecimal =
        new(@"\A-?[0-9]+(?:\.[0-9]+)?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static decimal Decimal(JsonElement raiz, string nome)
    {
        var valor = Propriedade(raiz, nome);
        var texto = valor.ValueKind switch
        {
            JsonValueKind.String => valor.GetString(),
            JsonValueKind.Number => valor.ToString(),
            _ => null
        };
        if (!TentarNumero(texto, out var numero))
            throw new DevolutivaSdInvalidaException("SD_DEVOLUTIVA_PESO_INVALIDO",
                $"O campo {nome} não é um número: '{texto}'.");
        if (numero < 0)
            throw new DevolutivaSdInvalidaException("SD_DEVOLUTIVA_PESO_NEGATIVO", $"{nome} veio negativo.");
        return numero;
    }
}
