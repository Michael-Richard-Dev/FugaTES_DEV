using System.Text;

namespace FugaTES.Aplicacao.Servicos.Sd;

/// <summary>
/// Conserta o UNICO defeito de formato que o Guardian produz por desenho: caractere de controle
/// cru dentro de uma string JSON.
///
/// POR QUE ISTO EXISTE. O lacre da carga e o lacre do envelope da amostra testemunha chegam no
/// mesmo campo "Observacao", separados por uma quebra de linha - o Guardian nao tem campo
/// separado para os dois, e o operador pede ao usuario da balanca que de um Enter entre eles.
/// A quebra e PARTE DO PROCESSO, nao acidente: vai acontecer em toda pesagem com testemunha.
///
/// O JSON exige que quebra de linha dentro de string venha escapada ("\r\n", dois caracteres).
/// Crua, o documento e invalido e o .NET o recusa antes de olhar campo nenhum. Foi o que
/// aconteceu em 05/11/2026 com a pesagem 0030384: dois envios, 400 nos dois, com
/// "'0x0D' is invalid within a JSON string" na linha da observacao.
///
/// Corrigir o modelo do Guardian resolve o caso de hoje e nao o de amanha: basta alguem digitar
/// Enter em qualquer campo de texto livre para a pesagem travar, e aquela correcao nao esta na
/// mao de quem opera. Autorizado pelo operador em 06/11/2026.
///
/// O QUE ISTO NAO FAZ. Nao tolera mais nada: virgula faltando, aspas sem par, chave sem
/// fechamento continuam sendo recusados, como devem. So os caracteres U+0000 a U+001F, e so
/// DENTRO de string, sao escapados. Fora de string eles sao espaco em branco legitimo entre
/// tokens e nao sao tocados.
///
/// O documento ORIGINAL continua sendo o que se guarda em PayloadJson: ele e a evidencia do que
/// chegou. Quem le e que saneia, e por isso esta funcao e chamada nos DOIS pontos de leitura -
/// a admissao e, depois, a composicao do texto da NF-e. Sanear so na admissao deixaria
/// MapeadorTextoNfeSd.LerComplemento falhando no documento guardado, e ele engole JsonException
/// e devolve complemento vazio: a nota sairia sem lacre, sem placa e sem motorista, em silencio.
/// </summary>
public static class JsonDoGuardian
{
    /// <summary>
    /// O mesmo documento com os caracteres de controle de dentro das strings escapados. Devolve
    /// a propria entrada quando nao ha o que consertar - que e o caso comum.
    /// </summary>
    public static string Sanear(string? json)
    {
        if (string.IsNullOrEmpty(json)) return json ?? "";

        var saida = new StringBuilder(json.Length + 16);
        var emString = false;
        var escapado = false;
        var mudou = false;

        foreach (var c in json)
        {
            if (!emString)
            {
                // Fora de string: espaco, tabulacao e quebra de linha sao formatacao legitima.
                saida.Append(c);
                if (c == '"') emString = true;
                continue;
            }

            // Dentro de string. O que vem depois de uma barra invertida passa intacto: ja esta
            // escapado, e mexer nele estragaria "\\n" (barra literal + n) ou "\u000D".
            if (escapado) { saida.Append(c); escapado = false; continue; }
            if (c == '\\') { saida.Append(c); escapado = true; continue; }
            if (c == '"') { saida.Append(c); emString = false; continue; }

            // Exatamente o que o JSON proibe: U+0000 a U+001F. U+007F (DEL) e legal cru e fica.
            if (c < ' ') { saida.Append(Escapar(c)); mudou = true; continue; }

            saida.Append(c);
        }

        // String sem fechamento ou barra solta no fim: o documento esta quebrado de outro jeito e
        // quem recusa e o leitor de JSON, com a mensagem dele. Nao e nosso trabalho adivinhar.
        return mudou ? saida.ToString() : json;
    }

    private static string Escapar(char c) => c switch
    {
        '\r' => "\\r",
        '\n' => "\\n",
        '\t' => "\\t",
        '\b' => "\\b",
        '\f' => "\\f",
        _ => "\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)
    };
}
