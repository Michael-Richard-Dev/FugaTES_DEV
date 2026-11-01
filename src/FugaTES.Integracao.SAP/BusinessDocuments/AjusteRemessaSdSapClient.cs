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
/// Para na primeira etapa que falha. Com o cabecalho recusado, zerar os itens deixaria a remessa
/// pior do que estava: itens sem peso e cabecalho sem o peso da balanca.
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

        var passo = await EnviarAsync("cabeçalho", uriCabecalho,
            MapeadorAjusteRemessaSd.Serializar(cabecalho), token, remessa.ETag, ct).ConfigureAwait(false);
        etapas.Add(passo);
        if (!passo.Ok || !zerarItens) return etapas;

        if (remessa.Itens.Count == 0)
        {
            etapas.Add(new("itens", false, 0,
                [new MensagemSap("SD_AJUSTE_SEM_ITENS", "error",
                    "A remessa foi lida sem itens; não há o que zerar. Confira no SAP.")]));
            return etapas;
        }

        var corpoItem = MapeadorAjusteRemessaSd.Serializar(MapeadorAjusteRemessaSd.MontarItemZerado());
        foreach (var item in remessa.Itens)
        {
            var uriItem = executor.ConstruirUri(ChaveItem(item.DeliveryDocument, item.DeliveryDocumentItem));
            var etapa = await EnviarAsync($"item {item.DeliveryDocumentItem}", uriItem, corpoItem,
                token, item.ETag, ct).ConfigureAwait(false);
            etapas.Add(etapa);
            if (!etapa.Ok) break;
        }

        return etapas;
    }

    private async Task<EtapaAjusteRemessaSd> EnviarAsync(string etapa, Uri uri, string json,
        string token, string? etag, CancellationToken ct)
    {
        using var conteudo = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        conteudo.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var resposta = await executor
            .EnviarComCsrfAsync(HttpMethod.Patch, uri, conteudo, token, etag ?? "*", ct)
            .ConfigureAwait(false);

        var status = (int)resposta.Status;
        // PATCH bem-sucedido no OData V2 devolve 204 sem corpo; 200 tambem ocorre.
        var ok = resposta.Status is HttpStatusCode.NoContent or HttpStatusCode.OK;
        return new(etapa, ok, status, ok ? [] : LeitorErroSapOData.Ler(resposta.Body));
    }

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
