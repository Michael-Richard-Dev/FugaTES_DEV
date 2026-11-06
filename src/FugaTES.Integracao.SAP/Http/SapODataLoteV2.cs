using System.Globalization;
using System.Net;
using System.Text;

namespace FugaTES.Integracao.SAP.Http;

/// <summary>Uma gravacao dentro do changeset: o que mudar, onde, e com que precondicao.</summary>
/// <param name="Nome">Como esta etapa aparece no relatorio. Nao vai para o SAP.</param>
/// <param name="CaminhoRelativo">
/// O caminho da entidade RELATIVO ao servico, sem barra inicial - o OData V2 exige assim dentro
/// do lote. Ex.: <c>A_OutbDeliveryItem(DeliveryDocument='80003934',DeliveryDocumentItem='000010')</c>.
/// </param>
public sealed record GravacaoLoteSap(string Nome, string CaminhoRelativo, string Json, string? ETag);

/// <summary>Desfecho de uma gravacao do lote, lido da resposta multipart.</summary>
public sealed record RespostaLoteSap(string Nome, bool Ok, int HttpStatus, IReadOnlyList<MensagemSap> MensagensSap);

/// <summary>
/// $batch do OData V2 com UM changeset - ou seja, UMA transacao no SAP.
///
/// POR QUE ISTO EXISTE. Gravar N itens da mesma remessa com N PATCHes separados cria N LUWs
/// sobre o MESMO documento, em sequencia rapida. O update task do primeiro ainda nao terminou
/// quando o segundo chega, e o SAP recusa dizendo que o documento esta sendo processado - por
/// nos mesmos. Foi a remessa 80003934, em 06/11/2026: item 000010 com HTTP 204, item 000020 com
/// HTTP 500 "The delivery is currently being processed by user FUGATES".
///
/// Num changeset as gravacoes vao numa transacao so: um enqueue, um commit. Nao ha segundo
/// PATCH para colidir com o primeiro.
///
/// E GANHA ATOMICIDADE, que e o que mais importa aqui: ou todos os itens sao zerados, ou nenhum.
/// Antes, o item 000010 ficava zerado e o 000020 nao, e a remessa ficava num estado que ninguem
/// pediu - nem o estado anterior, nem o desejado.
///
/// TAMBEM DISPENSA A RELEITURA ENTRE OS ITENS. O ETag so envelhece quando uma gravacao e
/// aceita; dentro de um changeset nada e aceito ate o commit, entao os ETags lidos antes do lote
/// valem para todas as partes. A releitura que existia ali usava o unico GET do executor que nao
/// carrega o cookie de sessao - ela lia FORA da sessao que acabara de gravar, e podia devolver
/// justamente o ETag velho que ela existia para evitar.
///
/// NAO E PARA TUDO. O cabecalho da remessa continua indo em PATCH proprio, depois do lote: o SAP
/// recalcula o peso do cabecalho a partir dos itens, e em 02/11/2026 (remessa 80003859) isso ja
/// nos custou um cabecalho 1199/699 quando a ordem das gravacoes mudou. Juntar os dois no mesmo
/// commit mudaria QUANDO o recalculo acontece em relacao a nossa escrita, e isso nao se confere
/// sem SAP na frente. O que esta provado em producao fica como esta.
/// </summary>
public static class SapODataLoteV2
{
    public const string Sufixo = "$batch";

    /// <summary>
    /// O corpo multipart com um changeset. O boundary entra como parametro para o chamador poder
    /// declara-lo no Content-Type - sao o mesmo valor, e divergir ali produz um lote que o SAP
    /// aceita e ignora.
    /// </summary>
    public static string MontarCorpo(
        IReadOnlyList<GravacaoLoteSap> gravacoes, string boundaryLote, string boundaryChangeset)
    {
        ArgumentNullException.ThrowIfNull(gravacoes);
        if (gravacoes.Count == 0) throw new ArgumentException("Lote sem gravações.", nameof(gravacoes));

        var sb = new StringBuilder();
        sb.Append("--").Append(boundaryLote).Append("\r\n");
        sb.Append("Content-Type: multipart/mixed; boundary=").Append(boundaryChangeset).Append("\r\n\r\n");

        // Content-ID numera as partes a partir de 1. O SAP usa esse numero para dizer QUAL parte
        // falhou quando o changeset e recusado - sem ele, a recusa nao se liga a uma gravacao.
        for (var i = 0; i < gravacoes.Count; i++)
        {
            var g = gravacoes[i];
            sb.Append("--").Append(boundaryChangeset).Append("\r\n");
            sb.Append("Content-Type: application/http\r\n");
            sb.Append("Content-Transfer-Encoding: binary\r\n");
            sb.Append("Content-ID: ").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("\r\n\r\n");

            sb.Append("PATCH ").Append(g.CaminhoRelativo).Append(" HTTP/1.1\r\n");
            sb.Append("Content-Type: application/json\r\n");
            sb.Append("Accept: application/json\r\n");
            // Sem ETag vai "*": a mesma regra do PATCH avulso. Preferimos a precondicao real
            // sempre que o SAP tiver dado uma.
            sb.Append("If-Match: ").Append(string.IsNullOrWhiteSpace(g.ETag) ? "*" : g.ETag).Append("\r\n");
            sb.Append("Content-Length: ")
              .Append(Encoding.UTF8.GetByteCount(g.Json).ToString(CultureInfo.InvariantCulture)).Append("\r\n\r\n");
            sb.Append(g.Json).Append("\r\n");
        }

        sb.Append("--").Append(boundaryChangeset).Append("--\r\n");
        sb.Append("--").Append(boundaryLote).Append("--\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// O desfecho de cada gravacao, lido da resposta.
    ///
    /// O changeset e tudo-ou-nada, e a resposta reflete isso: quando ele e aceito vem uma parte
    /// por gravacao, todas com 2xx; quando e recusado, o SAP costuma devolver UMA parte de erro
    /// para o changeset inteiro. Por isso, na recusa, TODAS as gravacoes voltam marcadas com o
    /// mesmo desfecho - e e a verdade: nenhuma delas foi aplicada.
    /// </summary>
    public static IReadOnlyList<RespostaLoteSap> LerRespostas(
        IReadOnlyList<GravacaoLoteSap> gravacoes, HttpStatusCode statusDoLote, string corpo)
    {
        ArgumentNullException.ThrowIfNull(gravacoes);

        // O POST do $batch em si falhou (CSRF, autenticacao, servico fora): nada foi gravado.
        if (statusDoLote is not (HttpStatusCode.Accepted or HttpStatusCode.OK))
            return Todas(gravacoes, false, (int)statusDoLote, LeitorErroSapOData.Ler(corpo ?? ""));

        var partes = PartesHttp(corpo ?? "");

        // Nenhuma parte legivel: nao da para afirmar que gravou. Tratar como recusa e o unico
        // desfecho honesto - dizer "ok" aqui inventaria um sucesso que ninguem viu.
        if (partes.Count == 0)
            return Todas(gravacoes, false, (int)statusDoLote,
                [new MensagemSap("SAP_LOTE_SEM_RESPOSTA", "error",
                    "O SAP aceitou o lote mas não devolveu o resultado das gravações. "
                    + "Confira a remessa no SAP antes de tentar de novo.")]);

        // Uma parte so, com erro, para varias gravacoes: e a recusa do changeset inteiro.
        if (partes.Count < gravacoes.Count)
        {
            var falha = partes.FirstOrDefault(p => p.Status >= 400) ?? partes[0];
            return Todas(gravacoes, false, falha.Status, LeitorErroSapOData.Ler(falha.Corpo));
        }

        var saida = new List<RespostaLoteSap>(gravacoes.Count);
        for (var i = 0; i < gravacoes.Count; i++)
        {
            var parte = partes[i];
            var ok = parte.Status is >= 200 and < 300;
            saida.Add(new(gravacoes[i].Nome, ok, parte.Status, ok ? [] : LeitorErroSapOData.Ler(parte.Corpo)));
        }
        return saida;
    }

    private static List<RespostaLoteSap> Todas(
        IReadOnlyList<GravacaoLoteSap> gravacoes, bool ok, int status, IReadOnlyList<MensagemSap> mensagens)
        => gravacoes.Select(g => new RespostaLoteSap(g.Nome, ok, status, mensagens)).ToList();

    private sealed record ParteHttp(int Status, string Corpo);

    /// <summary>
    /// As respostas HTTP de dentro do multipart. Nao interpreta boundary: procura as linhas de
    /// status "HTTP/1.1 nnn", que e o que distingue uma resposta embutida de texto de envelope -
    /// e o SAP varia o boundary da resposta, inclusive aninhando o do changeset.
    /// </summary>
    private static List<ParteHttp> PartesHttp(string corpo)
    {
        var partes = new List<ParteHttp>();
        var linhas = corpo.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < linhas.Length; i++)
        {
            var linha = linhas[i].Trim();
            if (!linha.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase)) continue;

            var pedacos = linha.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pedacos.Length < 2 || !int.TryParse(pedacos[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
                continue;

            // Do fim dos cabecalhos desta parte ate o proximo boundary.
            var j = i + 1;
            while (j < linhas.Length && linhas[j].Trim().Length > 0) j++;
            var corpoParte = new StringBuilder();
            for (var k = j + 1; k < linhas.Length; k++)
            {
                if (linhas[k].StartsWith("--", StringComparison.Ordinal)) break;
                corpoParte.Append(linhas[k]).Append('\n');
            }

            partes.Add(new(status, corpoParte.ToString().Trim()));
            i = j;
        }

        return partes;
    }
}
