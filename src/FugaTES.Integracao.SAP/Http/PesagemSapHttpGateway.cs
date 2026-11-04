using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Dtos.Pesagem;

using FugaTES.Aplicacao.Configuracao;
namespace FugaTES.Integracao.SAP.Http;

public sealed class PesagemSapHttpGateway(
    HttpClient httpClient,
    OpcoesSap opcoes,
    IAutenticacaoSap autenticacao,
    CookieContainer cookieContainer) : IPesagemSapMvpGateway
{
    private const string ResourcePath = "/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem";
    private static readonly JsonSerializerOptions AcmJson = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task EnviarAsync(PesagemSapMvp pesagem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pesagem);
        if (string.IsNullOrWhiteSpace(pesagem.ContratoACM))
            throw new ArgumentException("ContratoACM is required before SAP POST.", nameof(pesagem));
        // Validate and map before any CSRF GET / SAP POST. R161B rejects
        // incomplete ACM ticket evidence in fail-closed mode.
        var wire = Map(pesagem);
        Uri uri;
        try { uri = BuildUri(); }
        catch (InvalidOperationException exception)
        {
            throw new FalhaPesagemSapMvpException("SAP weighing pre-effect configuration failed.",
                ClassificacaoFalhaIntegracao.FalhaDefinitiva, exception);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(opcoes.Timeout);

        try
        {
            using var fetch = new HttpRequestMessage(HttpMethod.Get, uri);
            fetch.Headers.TryAddWithoutValidation("X-CSRF-Token", "Fetch");
            await autenticacao.AplicarAsync(fetch, timeout.Token).ConfigureAwait(false);
            using var fetchResponse = await httpClient.SendAsync(fetch, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!fetchResponse.IsSuccessStatusCode)
                throw new PesagemSapHttpException("SAP CSRF fetch failed.", fetchResponse.StatusCode,
                    fetchResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? ClassificacaoFalhaIntegracao.FalhaDefinitiva : ClassificacaoFalhaIntegracao.FalhaTransitoriaSegura);
            if (!fetchResponse.Headers.TryGetValues("X-CSRF-Token", out var tokenValues) ||
                string.IsNullOrWhiteSpace(tokenValues.FirstOrDefault()))
                throw new PesagemSapHttpException("SAP CSRF token is absent.", fetchResponse.StatusCode,
                    ClassificacaoFalhaIntegracao.FalhaTransitoriaSegura);

            CaptureCookies(uri, fetchResponse);
            var acm = IsAcm(pesagem);
            using var post = new HttpRequestMessage(HttpMethod.Post, uri)
            { Content = acm ? JsonContent.Create(wire, options: AcmJson) : JsonContent.Create(wire) };
            post.Headers.TryAddWithoutValidation("X-CSRF-Token", tokenValues.First());
            var cookies = cookieContainer.GetCookieHeader(uri);
            if (!string.IsNullOrWhiteSpace(cookies)) post.Headers.TryAddWithoutValidation("Cookie", cookies);
            await autenticacao.AplicarAsync(post, timeout.Token).ConfigureAwait(false);

            try
            {
                using var postResponse = await httpClient.SendAsync(post, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!postResponse.IsSuccessStatusCode)
                    throw new PesagemSapHttpException("SAP weighing POST failed.", postResponse.StatusCode,
                        (int)postResponse.StatusCode >= 400 && (int)postResponse.StatusCode < 500 &&
                        postResponse.StatusCode != HttpStatusCode.TooManyRequests
                            ? ClassificacaoFalhaIntegracao.FalhaDefinitiva : ClassificacaoFalhaIntegracao.ResultadoIndeterminado);
                // The supplied SAP contract does not define a verifiable business acknowledgement.
                // A 2xx may already have committed. Preserve it for manual reconciliation, never retry.
                if (acm)
                    throw new FalhaPesagemSapMvpException("R164B_SAP_BUSINESS_CONFIRMATION_NOT_PROVEN",
                        ClassificacaoFalhaIntegracao.ResultadoIndeterminado);
            }
            catch (PesagemSapHttpException) { throw; }
            catch (FalhaPesagemSapMvpException) { throw; }
            catch (Exception exception)
            {
                throw new FalhaPesagemSapMvpException("SAP weighing POST outcome is uncertain.",
                    ClassificacaoFalhaIntegracao.ResultadoIndeterminado, exception);
            }
        }
        catch (PesagemSapHttpException) { throw; }
        catch (FalhaPesagemSapMvpException) { throw; }
        catch (InvalidOperationException exception)
        {
            throw new FalhaPesagemSapMvpException("SAP weighing pre-effect configuration failed.",
                ClassificacaoFalhaIntegracao.FalhaDefinitiva, exception);
        }
        catch (Exception exception)
        {
            throw new FalhaPesagemSapMvpException("SAP weighing pre-effect request failed.",
                ClassificacaoFalhaIntegracao.FalhaTransitoriaSegura, exception);
        }
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

    private static FalhaPesagemSapMvpException Invalid(string reason) =>
        new("R161B_" + reason, ClassificacaoFalhaIntegracao.FalhaDefinitiva);

    private static PesagemSapWireDto Map(PesagemSapMvp source)
    {
        if (source.TipoDocumento == "AC3P" && !CentrosAcm.EhPermitido(source.Centro))
            throw Invalid("ACM_SCOPE_NOT_APPROVED");
        var acm = IsAcm(source);
        AcmSapTicketEvidence? ticket = null;
        IReadOnlyCollection<QualidadePesagemSapWireDto> qualidades = [];
        string? parceiroEntrega = null;
        var direction = source.TipoPesagem;
        if (acm)
        {
            ticket = source.EvidenciaTicket ?? throw Invalid("GUARDIAN_TICKET_EVIDENCE_REQUIRED");
            direction = AcmSapWeighingDirection.ToSap(ticket.TipoPesagem) ?? throw Invalid("WEIGHING_DIRECTION_UNVERIFIED");
            // The evidence must be bound to this original Guardian callback,
            // not to an SAP example or to a different ticket/contract.
            if (string.IsNullOrWhiteSpace(ticket.EvidenceReference) ||
                string.IsNullOrWhiteSpace(ticket.CallbackFingerprint) ||
                ticket.TicketGuardian != source.IdPesagem ||
                ticket.ProtocoloGuardian != source.NumeroProtocolo ||
                ticket.ContratoACM != source.ContratoACM ||
                ticket.Centro != source.Centro || ticket.Material != source.Material ||
                ticket.BusinessPartnerContrato != source.IdParceiro ||
                direction != AcmSapWeighingDirection.ToSap(source.TipoPesagem) ||
                source.SistemaBalanca != "Toledo" ||
                ticket.DataFinalPesagem == default ||
                source.PesoBruto <= 0 || source.PesoTara < 0 ||
                source.PesoAntesDesconto <= 0 ||
                source.PesoAntesDesconto != source.PesoBruto - source.PesoTara ||
                !FitsWeight(source.PesoBruto) || !FitsWeight(source.PesoTara) || !FitsWeight(source.PesoAntesDesconto) ||
                source.TipoOperacao <= 0 ||
                ticket.TipoOperacaoSap is not > 0 || ticket.TipoOperacaoSap != source.TipoOperacao ||
                string.IsNullOrWhiteSpace(source.IdPesagem) ||
                source.IdPesagem.Length > 10 ||
                string.IsNullOrWhiteSpace(source.IdParceiro) || source.IdParceiro.Length > 10 ||
                source.ContratoACM.Length > 20 ||
                (ticket.IdAgendamentoSap is not null && (string.IsNullOrWhiteSpace(ticket.IdAgendamentoSap) || ticket.IdAgendamentoSap.Length > 10)) ||
                (source.IdTransportadora is not null && (string.IsNullOrWhiteSpace(source.IdTransportadora) || source.IdTransportadora.Length > 10)) ||
                (ticket.NomeMotorista?.Length ?? 0) > 20 || (ticket.CpfMotorista?.Length ?? 0) > 20 || (ticket.PlacaTrator?.Length ?? 0) > 20 ||
                !IsSupportedCommodity(source.Material))
                throw Invalid("TICKET_IDENTITY_OR_WEIGHTS_UNVERIFIED");

            parceiroEntrega = ticket.TipoPessoa switch
            {
                "PJ" => source.IdParceiro,
                "PF" when !string.IsNullOrWhiteSpace(ticket.BusinessPartnerPropriedade) &&
                          ticket.BusinessPartnerPropriedade.Length <= 10 => ticket.BusinessPartnerPropriedade,
                _ => throw Invalid("DELIVERY_PROPERTY_BP_NOT_ATTESTED")
            };
            if (direction == "E" &&
                (ticket.ChaveAcessoNFe is null ||
                 ticket.ChaveAcessoNFe.Length != 44 ||
                 !ticket.ChaveAcessoNFe.All(char.IsAsciiDigit)))
                throw Invalid("ENTRY_NFE_ACCESS_KEY_REQUIRED");
            if (direction == "E" && ticket.ChaveAcessoNFe is not null && ticket.ChaveAcessoNFe.Length > 0 &&
                (ticket.ChaveAcessoNFe.Length != 44 || !ticket.ChaveAcessoNFe.All(char.IsAsciiDigit)))
                throw Invalid("NFE_ACCESS_KEY_INVALID");
            if (ticket.Qualidades is null || ticket.Qualidades.Count == 0)
                throw Invalid("REAL_COMMODITY_QUALITY_REQUIRED");
            var mapped = new List<QualidadePesagemSapWireDto>(ticket.Qualidades.Count);
            foreach (var qualidade in ticket.Qualidades)
            {
                if (qualidade is null || string.IsNullOrWhiteSpace(qualidade.Tipo) ||
                    qualidade.Tipo.Length > 20 ||
                    (qualidade.Grupo?.Length ?? 0) > 30 ||
                    qualidade.ValorAferido < 0 ||
                    qualidade.ValorAferido > 999999999.9m ||
                    decimal.Round(qualidade.ValorAferido, 1) != qualidade.ValorAferido)
                    throw Invalid("GUARDIAN_QUALITY_INVALID_OR_NOT_MEASURED");
                // Grupo can be empty, per the SAP example. Values are not generated.
                mapped.Add(new(qualidade.Tipo, qualidade.Grupo, qualidade.ValorAferido));
            }
            qualidades = mapped;
        }
        return new(
            source.SistemaBalanca, source.Centro, direction, source.IdPesagem,
            acm ? "/Date(" + ticket!.DataFinalPesagem.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + ")/" : null,
            null, null, null, acm ? (object?)ticket!.IdAgendamentoSap : source.IdAgendamento, source.IdTransportadora, null,
            source.PesoBruto, source.PesoTara, source.PesoAntesDesconto,
            acm ? source.PesoAntesDesconto : null,
            acm ? ticket!.NomeMotorista : null,
            acm ? ticket!.CpfMotorista : null,
            acm ? ticket!.PlacaTrator : null,
            source.TipoOperacao, null, source.Material,
            null, null, null, qualidades,
            [new(
                Indice: 1,
                NrIndiceTotal: 1,
                IdParceiro: source.IdParceiro,
                IdParceiroEntrega: parceiroEntrega,
                ObservacaoPesagem: null,
                ParceiroCpfCnpj: null,
                ParceiroNome: null,
                ParceiroEntregaNome: null,
                ContratoAcm: source.ContratoACM,
                OrdemVendas: null,
                Remessa: null,
                PedidoCompras: acm ? null : source.PedidoCompras,
                TipoDocumento: source.TipoDocumento,
                PesoAntesDesconto: source.PesoAntesDesconto,
                PesoAposDesconto: acm ? source.PesoAntesDesconto : null,
                PercentualDesdobro: acm ? 100m : null,
                ChaveAcessoNFe: acm && direction == "E" ? ticket!.ChaveAcessoNFe : null,
                NumeroNf: null,
                SerieNf: null,
                DataNf: null,
                ValorUnitarioNf: null,
                ValorTotalNf: null,
                QuantidadeNf: null,
                CpfCnpjTerceiro: null,
                NomeTerceiro: null,
                NumeroProtocolo: acm ? null : source.NumeroProtocolo,
                DataProtocolo: null,
                HoraProtocolo: null)]);
    }

    private static bool IsAcm(PesagemSapMvp source) => CentrosAcm.EhPermitido(source.Centro) && source.TipoDocumento == "AC3P";

    private static bool FitsWeight(decimal value) => value >= 0 && value <= 9999999999.999m && decimal.Round(value, 3) == value;

    private static bool IsSupportedCommodity(string material)
    {
        if (string.IsNullOrWhiteSpace(material)) return false;
        var normalized = material.TrimStart('0');
        return normalized is "7000000" or "7000001" or "7000002";
    }

}

public sealed class PesagemSapHttpException(string message, HttpStatusCode statusCode,
    ClassificacaoFalhaIntegracao classificacao) : FalhaPesagemSapMvpException(message, classificacao)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
