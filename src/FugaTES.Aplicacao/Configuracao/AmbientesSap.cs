namespace FugaTES.Aplicacao.Configuracao;

/// <summary>Um ambiente do SAP: a origem e o client, que so valem JUNTOS.</summary>
public sealed record AmbienteSap(string Nome, string Origem, string SapClient);

/// <summary>
/// Os ambientes do SAP com que o FugaTES pode falar.
///
/// Ate 05/11/2026 existia um so, e ele estava escrito como CONSTANTE em catorze arquivos - varios
/// deles lancando excecao quando a origem ou o client nao batiam. Isso nao era descuido: era o que
/// impedia o sistema de escrever num SAP que ninguem aprovou. A lista abaixo preserva essa
/// propriedade; o que muda e que agora ha dois nomes nela, em vez de um literal repetido.
///
/// O PAR E INDIVISIVEL. Origem e client sao conferidos juntos, nunca um sem o outro. Separa-los
/// aceitaria o host de DEV com o client de QAS - uma combinacao que nao existe e que, se existisse,
/// gravaria no sistema errado. E por isso que <see cref="EhAprovado"/> recebe os dois.
///
/// NAO HA CHAVE DE CONFIGURACAO dizendo "este processo e o de QAS". Ela seria uma terceira fonte
/// de verdade, capaz de discordar das outras duas. O ambiente de um processo e o par que ele tem
/// configurado: se o par e aprovado, o processo e daquele ambiente, e cada chamada e conferida
/// contra o par DELE - nao contra a lista inteira. Um processo de DEV continua sem alcancar o QAS.
///
/// Isso importa mais aqui do que pareceria: o operador confirmou em 05/11/2026 que as credenciais
/// dos dois ambientes SAO AS MESMAS. A senha, portanto, nao separa nada - este guarda e a unica
/// coisa entre um processo mal configurado e o SAP errado.
/// </summary>
public static class AmbientesSap
{
    /// <summary>Desenvolvimento. O unico que existia ate 05/11/2026.</summary>
    public static readonly AmbienteSap Dev110 =
        new("DEV110", "https://vhfufds4ci.sap.fugacouros.com.br:44300", "110");

    /// <summary>
    /// Qualidade. A URL difere da de DEV por uma letra - "ds4ci" vira "qs4ci" -, o que e
    /// exatamente o tipo de diferenca que passa despercebida numa revisao. Esta e a razao de o par
    /// ser conferido inteiro e de os dois nomes estarem aqui, lado a lado, em vez de espalhados.
    /// </summary>
    public static readonly AmbienteSap Qas120 =
        new("QAS120", "https://vhfufqs4ci.sap.fugacouros.com.br:44300", "120");

    public static readonly AmbienteSap[] Aprovados = [Dev110, Qas120];

    /// <summary>
    /// O ambiente deste par, ou nulo quando o par nao e de nenhum ambiente aprovado - inclusive
    /// quando origem e client existem mas pertencem a ambientes diferentes.
    /// </summary>
    public static AmbienteSap? Por(string? origem, string? sapClient)
    {
        var o = origem?.Trim();
        var c = sapClient?.Trim();
        if (string.IsNullOrEmpty(o) || string.IsNullOrEmpty(c)) return null;
        return Aprovados.FirstOrDefault(a =>
            string.Equals(a.Origem, o, StringComparison.Ordinal) &&
            string.Equals(a.SapClient, c, StringComparison.Ordinal));
    }

    public static bool EhAprovado(string? origem, string? sapClient) => Por(origem, sapClient) is not null;

    /// <summary>
    /// O ambiente de uma URL ja montada, pela origem dela mais o client informado. Nao le o
    /// sap-client da query de proposito: quem conhece o client do processo e o perfil, e uma URL
    /// que traga outro deve ser recusada por quem a comparar com o perfil - nao absolvida aqui.
    /// </summary>
    public static AmbienteSap? Por(Uri? uri, string? sapClient)
        => uri is { IsAbsoluteUri: true } ? Por(uri.GetLeftPart(UriPartial.Authority), sapClient) : null;
}
