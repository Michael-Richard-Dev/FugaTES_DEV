using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.Integracao.SAP.Http;

namespace FugaTES.Integracao.SAP.BusinessDocuments;

/// <summary>Desfecho de UMA gravacao do texto ZNFE.</summary>
public sealed record GravacaoTextoNfeSd(
    bool Ok, int HttpStatus, bool Criou, IReadOnlyList<MensagemSap> MensagensSap)
{
    public string Resumo => LeitorErroSapOData.Resumir(MensagensSap);
}

public interface IGravarTextoNfeSd
{
    Task<TextoNfeSdAtual> LerAsync(string numeroRemessa, CancellationToken ct);

    Task<GravacaoTextoNfeSd> GravarAsync(string numeroRemessa, TextoNfeSdAtual atual, string texto,
        CancellationToken ct);
}

/// <summary>
/// Passo 5: informacoes complementares da NF-e (elemento ZNFE) no cabecalho da remessa.
///
/// Existe texto -> PATCH na entidade do texto. Nao existe -> POST na navegacao do cabecalho, que
/// e o unico lugar onde ele pode nascer. A escolha vem da LEITURA, nunca de supor.
///
/// O PATCH substitui o texto inteiro. Por isso este cliente nao compoe nada: recebe pronto o
/// texto final, que foi montado a partir do que ele mesmo leu.
/// </summary>
public sealed class TextoNfeSdSapClient(SapODataHttpExecutor executor) : IGravarTextoNfeSd
{
    public const string Servico = "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/";
    public const string Entidade = Servico + "A_OutbDeliveryHeaderText";
    public const string Navegacao = "/to_DeliveryDocumentText";

    public async Task<TextoNfeSdAtual> LerAsync(string numeroRemessa, CancellationToken ct)
    {
        var uri = executor.ConstruirUri(RotaNavegacao(numeroRemessa), "$format=json");
        var resposta = await executor.ObterAsync(uri, ct).ConfigureAwait(false);
        // 404 na navegacao e "nao ha texto", nao falha: e o caso que leva ao POST.
        return resposta.Status is HttpStatusCode.OK
            ? MapeadorTextoNfeSd.LerTextoAtual(resposta.Body)
            : new(null, null);
    }

    public async Task<GravacaoTextoNfeSd> GravarAsync(string numeroRemessa, TextoNfeSdAtual atual,
        string texto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(atual);
        ArgumentNullException.ThrowIfNull(texto);

        var criar = !atual.Existe;
        var uri = executor.ConstruirUri(criar ? RotaNavegacao(numeroRemessa) : RotaEntidade(numeroRemessa));
        var json = criar
            ? MapeadorTextoNfeSd.Serializar(
                new CriarTextoNfeSdRequest(TextoNfeSd.Elemento, TextoNfeSd.Idioma, texto))
            : MapeadorTextoNfeSd.Serializar(new AlterarTextoNfeSdRequest(texto));

        var token = await executor.ObterTokenCsrfAsync(
            executor.ConstruirUri(RotaNavegacao(numeroRemessa)), ct).ConfigureAwait(false);

        // O texto pertence a remessa, e a remessa pode estar travada pela gravacao anterior - o
        // ajuste acaba de escrever nela quando o fluxo roda sozinho. Ver TravaSapOData.
        return await TravaSapOData.InsistirSeTravadoAsync(async cancelamento =>
        {
            using var conteudo = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            conteudo.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var resposta = await executor.EnviarComCsrfAsync(
                criar ? HttpMethod.Post : HttpMethod.Patch, uri, conteudo, token,
                criar ? null : atual.ETag ?? "*", cancelamento).ConfigureAwait(false);

            // 404 NO PATCH: a leitura disse que havia texto, e a escrita diz que nao ha. Quem
            // decide e a escrita - entao cria.
            //
            // Aconteceu na remessa 80003917, em 05/11/2026. A navegacao to_DeliveryDocumentText
            // devolveu o texto que vem da ordem, com os rotulos vazios, e por isso escolhemos
            // PATCH; mas a ENTIDADE por chave ainda nao existia, e o SAP respondeu 404 "Resource
            // not found". A chave estava certa: um GET nela, depois que alguem salvou o texto a
            // mao, devolveu 200 na mesma URL.
            //
            // A chave e composta por remessa, elemento e idioma - unica. Um POST aqui cria o que
            // falta ou conflita; nao ha como duplicar.
            if (!criar && resposta.Status == HttpStatusCode.NotFound)
            {
                using var criacao = new ByteArrayContent(Encoding.UTF8.GetBytes(
                    MapeadorTextoNfeSd.Serializar(
                        new CriarTextoNfeSdRequest(TextoNfeSd.Elemento, TextoNfeSd.Idioma, texto))));
                criacao.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                var depois = await executor.EnviarComCsrfAsync(HttpMethod.Post,
                    executor.ConstruirUri(RotaNavegacao(numeroRemessa)), criacao, token, null,
                    cancelamento).ConfigureAwait(false);
                var criado = depois.Status is HttpStatusCode.Created or HttpStatusCode.OK;
                return new GravacaoTextoNfeSd(criado, (int)depois.Status, true,
                    criado ? [] : LeitorErroSapOData.Ler(depois.Body));
            }

            var ok = criar
                ? resposta.Status is HttpStatusCode.Created or HttpStatusCode.OK
                : resposta.Status is HttpStatusCode.NoContent or HttpStatusCode.OK;
            return new GravacaoTextoNfeSd(ok, (int)resposta.Status, criar,
                ok ? [] : LeitorErroSapOData.Ler(resposta.Body));
        }, g => g.Ok, g => g.MensagensSap, ct).ConfigureAwait(false);
    }

    public static string RotaNavegacao(string numeroRemessa)
        => AjusteRemessaSdSapClient.ChaveCabecalho(numeroRemessa) + Navegacao;

    /// <summary>Chave composta do texto: remessa, elemento e idioma. Os tres sao fixos ou conferidos.</summary>
    public static string RotaEntidade(string numeroRemessa)
        => Entidade + "(DeliveryDocument='" + Numero(numeroRemessa)
            + "',TextElement='" + TextoNfeSd.Elemento
            + "',Language='" + TextoNfeSd.Idioma + "')";

    private static string Numero(string numeroRemessa)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(numeroRemessa);
        var limpo = numeroRemessa.Trim();
        if (limpo.Length > 10 || !limpo.All(char.IsAsciiDigit))
            throw new ArgumentException("SD_TEXTO_CHAVE_INVALIDA", nameof(numeroRemessa));
        return limpo;
    }
}
