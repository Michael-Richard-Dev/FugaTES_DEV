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

            var corpoItem = MapeadorAjusteRemessaSd.Serializar(MapeadorAjusteRemessaSd.MontarItemZerado());
            var itens = remessa.Itens;
            for (var i = 0; i < itens.Count; i++)
            {
                var item = itens[i];
                var etag = item.ETag;

                // RELEITURA ENTRE OS ITENS. Gravar um item muda a versao do DOCUMENTO, e com ela o
                // ETag dos OUTROS itens - nao so o do cabecalho. O ETag lido antes do laco vale
                // para o primeiro PATCH e envelhece no instante em que ele e aceito.
                //
                // Ate 04/11/2026 isso nunca apareceu porque a remessa de biodiesel tinha UM item.
                // Na remessa 80003910, em 05/11, a criacao passou a trazer tambem as linhas da
                // ordem em aberto: o item 000010 gravou com HTTP 204 e o 000020 foi recusado com
                // 412 "State of the resource (entity) was already changed (If-Match)". E o mesmo
                // 412 que a releitura abaixo ja evitava para o cabecalho - faltava estende-la aqui.
                //
                // O primeiro item segue usando o ETag que o chamador leu, sem GET extra: assim a
                // remessa de um item unico - todos os outros setores - continua fazendo
                // exatamente as mesmas chamadas de antes.
                if (i > 0)
                {
                    var relido = await LerAsync(remessa.DeliveryDocument, ct).ConfigureAwait(false);
                    var atualizado = relido?.Itens.FirstOrDefault(x => string.Equals(
                        x.DeliveryDocumentItem, item.DeliveryDocumentItem, StringComparison.Ordinal));
                    if (atualizado is null)
                    {
                        // Sem o ETag do estado atual nao da para gravar com seguranca: o velho
                        // daria 412, e "*" passaria por cima de uma alteracao de terceiro.
                        etapas.Add(new($"item {item.DeliveryDocumentItem}", false, 0,
                            [new MensagemSap("SD_AJUSTE_RELEITURA_ITEM_FALHOU", "error",
                                $"O item {item.DeliveryDocumentItem} não pôde ser lido de novo "
                                + "para gravar com a precondição atual. Tente o ajuste outra vez.")]));
                        return etapas;
                    }
                    etag = atualizado.ETag;
                }

                var uriItem = executor.ConstruirUri(ChaveItem(item.DeliveryDocument, item.DeliveryDocumentItem));
                var etapa = await EnviarAsync($"item {item.DeliveryDocumentItem}", uriItem, corpoItem,
                    token, etag, ct).ConfigureAwait(false);
                etapas.Add(etapa);
                // Para no primeiro item recusado: seguir deixaria a remessa com parte dos itens
                // zerada e parte nao, e o cabecalho sem o peso da balanca.
                if (!etapa.Ok) return etapas;
            }

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

    private static string Digitos(string valor, string nome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valor, nome);
        var limpo = valor.Trim();
        if (limpo.Length > 10 || !limpo.All(char.IsAsciiDigit))
            throw new ArgumentException("SD_AJUSTE_CHAVE_INVALIDA: " + nome, nome);
        return limpo;
    }
}
