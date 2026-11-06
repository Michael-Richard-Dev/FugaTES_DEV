using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.Integracao.SAP.Http;

namespace FugaTES.Integracao.SAP.BusinessDocuments;

/// <summary>Desfecho de UMA etapa do ajuste. Guarda o que serve para auditar e para a tela.</summary>
public sealed record EtapaAjusteRemessaSd(
    string Etapa, bool Ok, int HttpStatus, IReadOnlyList<MensagemSap> MensagensSap)
{
    public string Resumo => LeitorErroSapOData.Resumir(MensagensSap);
}

public interface IAjustarRemessaSdSap
{
    Task<RemessaSdLida?> LerAsync(string numeroRemessa, CancellationToken ct);

    Task<IReadOnlyList<EtapaAjusteRemessaSd>> AjustarAsync(
        RemessaSdLida remessa, AjusteCabecalhoRemessaSdRequest cabecalho, bool zerarItens,
        CancellationToken ct);
}

/// <summary>
/// Passo 4 em API_OUTBOUND_DELIVERY_SRV;v=2: PATCH no cabecalho e, no fluxo de biodiesel, PATCH
/// em cada item zerando os pesos.
///
/// O ETag vai no If-Match sempre que o SAP tiver dado um. O documento sugere "*", que funciona,
/// mas sobrescreve mesmo que alguem tenha mexido na remessa entre a leitura e a escrita - e aqui
/// o que esta sendo escrito e o peso que vai para a NF-e.
///
/// A ordem e ITENS e depois CABECALHO, e isso importa: alterar um item faz o SAP recalcular o
/// peso do cabecalho a partir dos itens. O peso da balanca tem de ser a ultima gravacao.
///
/// Para na primeira etapa que falha. Com um item recusado, gravar o cabecalho deixaria a remessa
/// meio ajustada - e o peso ainda seria recalculado adiante.
/// </summary>
public sealed class AjusteRemessaSdSapClient(SapODataHttpExecutor executor) : IAjustarRemessaSdSap
{
    public const string Servico = "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/";
    public const string Cabecalho = Servico + "A_OutbDeliveryHeader";
    public const string Item = Servico + "A_OutbDeliveryItem";

    public async Task<RemessaSdLida?> LerAsync(string numeroRemessa, CancellationToken ct)
    {
        var uri = executor.ConstruirUri(ChaveCabecalho(numeroRemessa),
            "$expand=to_DeliveryDocumentItem&$format=json");
        var resposta = await executor.ObterAsync(uri, ct).ConfigureAwait(false);
        return resposta.Status != HttpStatusCode.OK
            ? null
            : MapeadorAjusteRemessaSd.LerRemessa(resposta.Body, resposta.ETag);
    }

    public async Task<IReadOnlyList<EtapaAjusteRemessaSd>> AjustarAsync(
        RemessaSdLida remessa, AjusteCabecalhoRemessaSdRequest cabecalho, bool zerarItens,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(remessa);
        ArgumentNullException.ThrowIfNull(cabecalho);
        var etapas = new List<EtapaAjusteRemessaSd>();

        var uriCabecalho = executor.ConstruirUri(ChaveCabecalho(remessa.DeliveryDocument));
        var token = await executor.ObterTokenCsrfAsync(uriCabecalho, ct).ConfigureAwait(false);

        // ITENS PRIMEIRO, cabecalho depois. A ordem nao e detalhe: alterar um item faz o SAP
        // RECALCULAR o peso do cabecalho a partir dos itens. Na remessa 80003859, em 02/11/2026,
        // gravamos o cabecalho com 1200/700 e zeramos o item em seguida - o SAP descontou o 1 kg
        // do item e o cabecalho ficou 1199/699. O peso do cabecalho tem de ser a ULTIMA gravacao,
        // para nada o recalcular depois.
        if (zerarItens)
        {
            if (remessa.Itens.Count == 0)
            {
                etapas.Add(new("itens", false, 0,
                    [new MensagemSap("SD_AJUSTE_SEM_ITENS", "error",
                        "A remessa foi lida sem itens; não há o que zerar. Confira no SAP.")]));
                return etapas;
            }

            // TODOS OS ITENS NUMA TRANSACAO SO ($batch com um changeset).
            //
            // Ate 06/11/2026 cada item ia num PATCH proprio, e isso criava N gravacoes sobre o
            // MESMO documento em sequencia rapida. Duas coisas davam errado, nesta ordem
            // historica:
            //
            //   1. o ETag do item seguinte envelhecia assim que o anterior era aceito - 412 na
            //      remessa 80003910 (05/11). Foi contornado relendo entre os itens;
            //   2. a releitura nao resolveu a colisao de verdade: o update task do PATCH anterior
            //      ainda estava rodando, e o SAP recusou dizendo que a remessa estava sendo
            //      processada POR NOS MESMOS - HTTP 500 na remessa 80003934 (06/11), que ficou com
            //      o item 000010 zerado e o 000020 nao.
            //
            // No changeset ha um enqueue e um commit: nao existe segundo PATCH para colidir com o
            // primeiro, e o ETag nao envelhece no meio porque nada e aceito ate o fim. A releitura
            // entre itens saiu junto - ela usava o unico GET do executor que nao carrega o cookie
            // de sessao, e lia fora da sessao que acabara de gravar.
            //
            // E, o que mais importa: ou todos os itens sao zerados, ou nenhum. O estado "metade
            // zerada" deixa de existir.
            var corpoItem = MapeadorAjusteRemessaSd.Serializar(MapeadorAjusteRemessaSd.MontarItemZerado());
            var gravacoes = remessa.Itens
                .Select(item => new GravacaoLoteSap(
                    $"item {item.DeliveryDocumentItem}",
                    ChaveItemRelativa(item.DeliveryDocument, item.DeliveryDocumentItem),
                    corpoItem,
                    item.ETag))
                .ToArray();

            var doLote = await EnviarLoteAsync(gravacoes, token, ct).ConfigureAwait(false);
            etapas.AddRange(doLote);
            // Changeset e tudo-ou-nada: uma parte recusada significa que NENHUMA foi aplicada.
            if (doLote.Any(e => !e.Ok)) return etapas;

            // Zerar os itens muda a VERSAO do documento, e com ela o ETag do cabecalho. Mandar o
            // cabecalho com o ETag lido antes devolveria 412 "State of the resource was already
            // changed" - o mesmo 412 que recebemos ao fazer na ordem inversa. Por isso a releitura:
            // a precondicao passa a ser a do estado ATUAL, e nao a de antes de nos mesmos
            // alterarmos a remessa.
            var atual = await LerAsync(remessa.DeliveryDocument, ct).ConfigureAwait(false);
            if (atual is null)
            {
                // Sem releitura nao da para gravar o cabecalho com seguranca. O ETag velho daria
                // 412, e "*" passaria por cima de uma alteracao de terceiro sem perceber.
                etapas.Add(new("cabeçalho", false, 0,
                    [new MensagemSap("SD_AJUSTE_RELEITURA_FALHOU", "error",
                        "Os itens foram zerados, mas a remessa não pôde ser lida de novo para "
                        + "gravar o peso no cabeçalho. Tente o ajuste outra vez.")]));
                return etapas;
            }
            remessa = atual;
        }

        etapas.Add(await EnviarAsync("cabeçalho", uriCabecalho,
            MapeadorAjusteRemessaSd.Serializar(cabecalho), token, remessa.ETag, ct).ConfigureAwait(false));
        return etapas;
    }

    /// <summary>
    /// O lote, com a mesma insistencia em trava que um PATCH avulso: a trava pode vir do enqueue
    /// do passo ANTERIOR do fluxo - a criacao da remessa, segundos antes -, e nesse caso ela nao
    /// e culpa de ter varios itens. O changeset resolve a colisao entre OS NOSSOS ITENS; a trava
    /// entre passos continua existindo, e e transitoria do mesmo jeito.
    ///
    /// Insistir e seguro porque o changeset e atomico: uma recusa garante que nada foi gravado,
    /// entao repetir nao grava duas vezes.
    /// </summary>
    private Task<IReadOnlyList<EtapaAjusteRemessaSd>> EnviarLoteAsync(
        IReadOnlyList<GravacaoLoteSap> gravacoes, string token, CancellationToken ct)
        => TravaSapOData.InsistirSeTravadoAsync(async cancelamento =>
        {
            // Boundaries unicos por envio: reaproveitar entre requisicoes e o tipo de coisa que
            // so quebra sob concorrencia.
            var marca = Guid.NewGuid().ToString("N");
            var boundaryLote = "batch_" + marca;
            var boundaryChangeset = "changeset_" + marca;

            var corpo = SapODataLoteV2.MontarCorpo(gravacoes, boundaryLote, boundaryChangeset);
            var uri = executor.ConstruirUri(Servico + SapODataLoteV2.Sufixo);
            var resposta = await executor
                .EnviarLoteAsync(uri, corpo, boundaryLote, token, cancelamento).ConfigureAwait(false);

            return (IReadOnlyList<EtapaAjusteRemessaSd>)SapODataLoteV2
                .LerRespostas(gravacoes, resposta.Status, resposta.Body)
                .Select(r => new EtapaAjusteRemessaSd(r.Nome, r.Ok, r.HttpStatus, r.MensagensSap))
                .ToArray();
        },
        etapas => etapas.All(e => e.Ok),
        etapas => etapas.SelectMany(e => e.MensagensSap).ToArray(),
        ct);

    private Task<EtapaAjusteRemessaSd> EnviarAsync(string etapa, Uri uri, string json,
        string token, string? etag, CancellationToken ct)
        => TravaSapOData.InsistirSeTravadoAsync(async cancelamento =>
        {
            using var conteudo = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            conteudo.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var resposta = await executor
                .EnviarComCsrfAsync(HttpMethod.Patch, uri, conteudo, token, etag ?? "*", cancelamento)
                .ConfigureAwait(false);

            // PATCH bem-sucedido no OData V2 devolve 204 sem corpo; 200 tambem ocorre.
            var ok = resposta.Status is HttpStatusCode.NoContent or HttpStatusCode.OK;
            return new EtapaAjusteRemessaSd(etapa, ok, (int)resposta.Status,
                ok ? [] : LeitorErroSapOData.Ler(resposta.Body));
        }, e => e.Ok, e => e.MensagensSap, ct);

    /// <summary>
    /// Chave do cabecalho. O numero e conferido antes de entrar na URL: a chave do OData e texto
    /// entre aspas simples, e um valor arbitrario ali dentro mudaria a entidade alvo.
    /// </summary>
    public static string ChaveCabecalho(string numeroRemessa)
        => Cabecalho + "('" + Digitos(numeroRemessa, nameof(numeroRemessa)) + "')";

    public static string ChaveItem(string numeroRemessa, string item)
        => Item + "(DeliveryDocument='" + Digitos(numeroRemessa, nameof(numeroRemessa))
            + "',DeliveryDocumentItem='" + Digitos(item, nameof(item)) + "')";

    /// <summary>
    /// A mesma chave do item, mas RELATIVA ao servico - sem o caminho do servico e sem barra
    /// inicial. E a forma que o OData V2 exige na linha de requisicao de dentro do $batch: um
    /// caminho absoluto ali faz o SAP recusar a parte.
    ///
    /// Passa pela mesma conferencia de digitos de <see cref="ChaveItem"/>, que e o que impede um
    /// valor arbitrario de mudar a entidade alvo.
    /// </summary>
    public static string ChaveItemRelativa(string numeroRemessa, string item)
        => ChaveItem(numeroRemessa, item)[Servico.Length..];

    private static string Digitos(string valor, string nome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valor, nome);
        var limpo = valor.Trim();
        if (limpo.Length > 10 || !limpo.All(char.IsAsciiDigit))
            throw new ArgumentException("SD_AJUSTE_CHAVE_INVALIDA: " + nome, nome);
        return limpo;
    }
}
