using System.Net;
using System.Net.Http.Json;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Dtos.Pesagem;

namespace FugaTES.Integracao.SAP.Http;

public sealed class PesagemSapHttpGateway(
    HttpClient httpClient,
    OpcoesSap opcoes,
    IAutenticacaoSap autenticacao,
    CookieContainer cookieContainer) : IPesagemSapMvpGateway
{
    private const string ResourcePath = "/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem";

    public async Task EnviarAsync(PesagemSapMvp pesagem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pesagem);
        if (string.IsNullOrWhiteSpace(pesagem.ContratoACM))
            throw new ArgumentException("ContratoACM is required before SAP POST.", nameof(pesagem));
        var uri = BuildUri();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(opcoes.Timeout);

        using var fetch = new HttpRequestMessage(HttpMethod.Get, uri);
        fetch.Headers.TryAddWithoutValidation("X-CSRF-Token", "Fetch");
        await autenticacao.AplicarAsync(fetch, timeout.Token).ConfigureAwait(false);
        using var fetchResponse = await httpClient.SendAsync(fetch, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!fetchResponse.IsSuccessStatusCode)
            throw new PesagemSapHttpException("SAP CSRF fetch failed.", fetchResponse.StatusCode);
        if (!fetchResponse.Headers.TryGetValues("X-CSRF-Token", out var tokenValues) ||
            string.IsNullOrWhiteSpace(tokenValues.FirstOrDefault()))
            throw new PesagemSapHttpException("SAP CSRF token is absent.", fetchResponse.StatusCode);

        CaptureCookies(uri, fetchResponse);
        var token = tokenValues.First()!;
        using var post = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(Map(pesagem)) };
        post.Headers.TryAddWithoutValidation("X-CSRF-Token", token);
        var cookies = cookieContainer.GetCookieHeader(uri);
        if (!string.IsNullOrWhiteSpace(cookies)) post.Headers.TryAddWithoutValidation("Cookie", cookies);
        await autenticacao.AplicarAsync(post, timeout.Token).ConfigureAwait(false);

        using var postResponse = await httpClient.SendAsync(post, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!postResponse.IsSuccessStatusCode)
            throw new PesagemSapHttpException("SAP weighing POST failed.", postResponse.StatusCode);
    }

    private Uri BuildUri()
    {
        if (opcoes.BaseUrl is null || !opcoes.BaseUrl.IsAbsoluteUri ||
            string.IsNullOrWhiteSpace(opcoes.SapClient) || opcoes.Timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("SAP weighing configuration is invalid.");
        var resource = new Uri(opcoes.BaseUrl, ResourcePath);
        return new Uri(resource.AbsoluteUri + "?sap-client=" + Uri.EscapeDataString(opcoes.SapClient));
    }

    private void CaptureCookies(Uri uri, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var value in values) cookieContainer.SetCookies(uri, value);
    }

    private static PesagemSapWireDto Map(PesagemSapMvp source) =>
        new(
            source.SistemaBalanca, source.Centro, source.TipoPesagem, source.IdPesagem,
            null, null, null, null, source.IdAgendamento, source.IdTransportadora, null,
            source.PesoBruto, source.PesoTara, source.PesoAntesDesconto, null,
            null, null, null, source.TipoOperacao, null, source.Material,
            null, null, null, [],
            [new(
                Indice: 1,
                NrIndiceTotal: 1,
                IdParceiro: source.IdParceiro,
                IdParceiroEntrega: null,
                ObservacaoPesagem: null,
                ParceiroCpfCnpj: null,
                ParceiroNome: null,
                ParceiroEntregaNome: null,
                ContratoAcm: source.ContratoACM,
                OrdemVendas: null,
                Remessa: null,
                PedidoCompras: source.PedidoCompras,
                TipoDocumento: source.TipoDocumento,
                PesoAntesDesconto: source.PesoAntesDesconto,
                PesoAposDesconto: null,
                PercentualDesdobro: null,
                ChaveAcessoNFe: null,
                NumeroNf: null,
                SerieNf: null,
                DataNf: null,
                ValorUnitarioNf: null,
                ValorTotalNf: null,
                QuantidadeNf: null,
                CpfCnpjTerceiro: null,
                NomeTerceiro: null,
                NumeroProtocolo: null,
                DataProtocolo: null,
                HoraProtocolo: null)]);
}

public sealed class PesagemSapHttpException(string message, HttpStatusCode statusCode) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
