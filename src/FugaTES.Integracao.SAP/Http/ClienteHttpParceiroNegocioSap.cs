using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;
using System.Net;

namespace FugaTES.Integracao.SAP.Http;

public sealed class ClienteHttpParceiroNegocioSap
{
    private const string ExpandBusinessPartner = "to_Customer,to_Supplier,to_BusinessPartnerRole,to_BusinessPartnerTax,to_BusinessPartnerAddress,to_BusinessPartnerAddress/to_AddressUsage,to_BusinessPartnerAddress/to_PhoneNumber,to_BusinessPartnerAddress/to_EmailAddress";
    private readonly HttpClient httpClient;
    private readonly OpcoesSap opcoes;
    private readonly IAutenticacaoSap autenticacao;

    public ClienteHttpParceiroNegocioSap(HttpClient httpClient, OpcoesSap opcoes, IAutenticacaoSap autenticacao)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.opcoes = opcoes ?? throw new ArgumentNullException(nameof(opcoes));
        this.autenticacao = autenticacao ?? throw new ArgumentNullException(nameof(autenticacao));
    }

    internal async Task<RespostaHttpParceiroNegocioSap> ObterAsync(string businessPartner, CancellationToken cancellationToken)
    {
        if (opcoes.BaseUrl is null || !opcoes.BaseUrl.IsAbsoluteUri || opcoes.Timeout <= TimeSpan.Zero)
        {
            return new RespostaHttpParceiroNegocioSap(StatusConsultaSap.FalhaConfiguracao, null, null);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(opcoes.Timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, CriarUriConsulta(businessPartner));
        await autenticacao.AplicarAsync(request, timeout.Token).ConfigureAwait(false);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        var conteudo = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        return new RespostaHttpParceiroNegocioSap(null, response.StatusCode, conteudo);
    }

    private Uri CriarUriConsulta(string businessPartner)
    {
        var chave = businessPartner.Replace("'", "''", StringComparison.Ordinal);
        var query = $"A_BusinessPartner('{Uri.EscapeDataString(chave)}')?$expand={Uri.EscapeDataString(ExpandBusinessPartner)}";
        var separador = opcoes.BaseUrl!.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? string.Empty : "/";
        var uri = new Uri(opcoes.BaseUrl.AbsoluteUri + separador + query, UriKind.Absolute);

        if (!string.IsNullOrWhiteSpace(opcoes.SapClient))
        {
            var join = uri.Query.Length == 0 ? "?" : "&";
            uri = new Uri(uri.AbsoluteUri + join + "sap-client=" + Uri.EscapeDataString(opcoes.SapClient));
        }

        return uri;
    }
}

internal sealed record RespostaHttpParceiroNegocioSap(StatusConsultaSap? FalhaConfiguracao, HttpStatusCode? StatusCode, string? Conteudo);
