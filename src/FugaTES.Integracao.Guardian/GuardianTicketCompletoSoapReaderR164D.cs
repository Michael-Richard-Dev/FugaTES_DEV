using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Aplicacao.Servicos.Mvp;
using Microsoft.Extensions.Logging;

namespace FugaTES.Integracao.Guardian;

// Real HTTP/SOAP transport for the confirmed homologated Guardian read method.
// Every SOAP element QName, XPath and authentication must come from the actual
// WSDL/provider response. There are NO guessed request/response wire defaults.
public sealed record GuardianTicketSoapBindingR164D(
    Uri Endpoint,
    XName Operation,
    XName RequestParameter,
    XName TicketCodeField,
    XName Response,
    XName Result,
    IReadOnlyDictionary<string, string> Namespaces,
    string WsdlEvidenceSha256,
    string AuthenticationEvidenceReference,
    bool ReadMethodConfirmed,
    bool InternalHttpApproved,
    TimeSpan Timeout);

public interface IConfirmedGuardianTicketSoapAuthenticationR164D
{
    Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

public sealed class GuardianTicketReadExceptionR164D(string reason, Exception? inner = null)
    : Exception(reason, inner);

public sealed class GuardianTicketCompletoSoapClientR164D(
    HttpClient http,
    GuardianTicketSoapBindingR164D binding,
    IConfirmedGuardianTicketSoapAuthenticationR164D authentication)
{
    public const string ConfirmedAction = "http://toledobrasil.com.br/WS_Guardian/TicketCompletoObtem";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";

    public async Task<XElement> ReadTicketAsync(string ticket, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket);
        ct.ThrowIfCancellationRequested();
        EnsureConfirmedBinding();

        // Request element names are injected ONLY from the attested WSDL. The
        // TicketCodigo TYPE member is documented, but its operation parameter
        // element is not asserted without the XSD.
        var envelope = new XDocument(new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", Soap.NamespaceName),
            new XElement(Soap + "Body", new XElement(binding.Operation,
                new XElement(binding.RequestParameter,
                    new XElement(binding.TicketCodeField, ticket))))));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(binding.Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, binding.Endpoint)
            {
                Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
            };
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{ConfirmedAction}\"");
            await authentication.ApplyAsync(request, timeout.Token).ConfigureAwait(false);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new GuardianTicketReadExceptionR164D("GUARDIAN_TICKET_SOAP_HTTP_UNCONFIRMED");
            // Buffer with a bounded size and the same timeout before synchronous XML parsing.
            await response.Content.LoadIntoBufferAsync(2_000_000, timeout.Token).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var xml = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 2_000_000,
                MaxCharactersFromEntities = 0
            });
            var document = XDocument.Load(xml);
            var body = document.Root?.Name == Soap + "Envelope"
                ? document.Root.Element(Soap + "Body") : null;
            if (body is null || body.Elements().Any(e => e.Name == Soap + "Fault"))
                throw new GuardianTicketReadExceptionR164D("GUARDIAN_TICKET_SOAP_FAULT_OR_INVALID_BODY");
            var operations = body.Elements(binding.Response).ToArray();
            if (operations.Length != 1)
                throw new GuardianTicketReadExceptionR164D("GUARDIAN_TICKET_SOAP_RESPONSE_AMBIGUOUS");
            var results = operations[0].Elements(binding.Result).ToArray();
            if (results.Length != 1 || results[0].IsEmpty)
                throw new GuardianTicketReadExceptionR164D("GUARDIAN_TICKET_SOAP_RESULT_MISSING");
            return results[0];
        }
        catch (GuardianTicketReadExceptionR164D) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new GuardianTicketReadExceptionR164D("GUARDIAN_TICKET_SOAP_TIMEOUT_UNKNOWN"); }
        catch (HttpRequestException ex)
        { throw new GuardianTicketReadExceptionR164D("GUARDIAN_TICKET_SOAP_TRANSPORT_UNKNOWN", ex); }
        catch (XmlException ex)
        { throw new GuardianTicketReadExceptionR164D("GUARDIAN_TICKET_SOAP_XML_INVALID", ex); }
    }

    private void EnsureConfirmedBinding()
    {
        if (!binding.ReadMethodConfirmed || string.IsNullOrWhiteSpace(binding.WsdlEvidenceSha256) ||
            binding.WsdlEvidenceSha256.Length != 64 ||
            !binding.WsdlEvidenceSha256.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(binding.AuthenticationEvidenceReference) ||
            binding.Endpoint is null || !binding.Endpoint.IsAbsoluteUri ||
            binding.Endpoint.Scheme is not ("http" or "https") ||
            (binding.Endpoint.Scheme == "http" && !binding.InternalHttpApproved) ||
            binding.Endpoint.Query.Length != 0 || binding.Endpoint.Fragment.Length != 0 ||
            binding.Operation.LocalName != "TicketCompletoObtem" ||
            binding.Operation.NamespaceName != "http://toledobrasil.com.br/WS_Guardian" ||
            binding.RequestParameter is null || binding.TicketCodeField is null ||
            binding.Response is null || binding.Result is null ||
            binding.Timeout <= TimeSpan.Zero || binding.Timeout > TimeSpan.FromMinutes(2))
            throw new GuardianTicketReadExceptionR164D("GUARDIAN_TICKET_SOAP_BINDING_NOT_ATTESTED");
    }
}

// XPaths MUST be attested against the exact version of the SOAP response.
// The names below are LOCAL semantic slots, not wire property claims.
public sealed record GuardianTicketFieldPathsR164D(
    string Ticket, string Protocol, string FinalOperationCode, string FinalTime,
    string WeighingType, string PersonType, string Gross, string Tara, string Net,
    string QualityItems, string QualityType, string QualityValue, string? QualityGroup,
    string SapOperationType, string SapOperationBusinessEvidence,
    string? PropertyBusinessPartner = null, string? EffectiveEmitter = null,
    string? Carrier = null, string? Driver = null, string? DriverCpf = null,
    string? TractorPlate = null, string? NfeKey = null,
    string? Contract = null, string? Material = null, string? Reference = null,
    string? Center = null, string? TicketCode = null);

public sealed record GuardianTicketFieldContractR164D(
    GuardianTicketFieldPathsR164D Paths,
    IReadOnlyDictionary<string, string> NamespacePrefixes,
    string ProviderResponseEvidenceReference,
    string SapOperationBusinessRuleEvidenceReference,
    string WeighingTypeBusinessRuleEvidenceReference,
    bool ProviderFieldsAttested,
    bool BusinessRulesAttested);

// Explicit per-correlation identity supplied by a separately attested caller.
// No implementation derives this from callback.Sequencial or protocol ID.
// No runtime registration or trusted source is introduced by this offline patch.
public sealed record GuardianTicketCodeEvidenceR165G(
    string Codigo, string Sequencial, string Protocolo, string ContratoSap,
    string CallbackFingerprint, string EvidenceReference,
    bool UniqueAssociationConfirmed = false);

// A genuine normalized reader, with enforced no-output-on-missing-wire semantics.
// It is deliberately NOT registered by any composition until the SOAP XSD,
// authentication, response field map and SAP operation provenance are attested.
public sealed class GuardianTicketCompletoVerifiedReaderR164D(
    GuardianTicketCompletoSoapClientR164D soap,
    GuardianTicketFieldContractR164D fields,
    ILogger<GuardianTicketCompletoVerifiedReaderR164D>? logger = null,
    GuardianTicketCodeEvidenceR165G? ticketCodeEvidence = null) : IAcmGuardianVerifiedWeighingReaderR164A
{
    public async Task<AcmGuardianVerifiedWeighingR164A?> ObterAsync(
        CorrelacaoRecebimentoMvp correlation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ct.ThrowIfCancellationRequested();
        var contract = correlation.Entrada.ContratoAcmSap;
        if (string.IsNullOrWhiteSpace(contract))
        {
            const string reason = "GUARDIAN_TICKET_ACM_CONTRACT_REQUIRED";
            logger?.LogWarning("Guardian ticket evidence blocked: {Reason}", reason);
            throw new GuardianTicketReadExceptionR164D(reason);
        }
        if (!fields.ProviderFieldsAttested || !fields.BusinessRulesAttested ||
            string.IsNullOrWhiteSpace(fields.ProviderResponseEvidenceReference) ||
            string.IsNullOrWhiteSpace(fields.SapOperationBusinessRuleEvidenceReference) ||
            string.IsNullOrWhiteSpace(fields.WeighingTypeBusinessRuleEvidenceReference) ||
            string.IsNullOrWhiteSpace(correlation.TicketGuardian) ||
            string.IsNullOrWhiteSpace(correlation.ProtocoloGuardian) ||
            correlation.CallbackFingerprint is null || correlation.Pesos is null ||
            !AcmR106SapReturnPreflight.EventoFinalComprovado(correlation))
            return null;
        var p = fields.Paths;
        // TicketGuardian is the callback sequence in the existing persisted model.
        // It is NOT a proven TicketCodigo. Absence/ambiguity blocks before auth/HTTP.
        if (ticketCodeEvidence is not { UniqueAssociationConfirmed: true } identity ||
            string.IsNullOrWhiteSpace(identity.Codigo) || identity.Codigo != identity.Codigo.Trim() ||
            string.IsNullOrWhiteSpace(identity.EvidenceReference) ||
            identity.Sequencial != correlation.TicketGuardian ||
            identity.Protocolo != correlation.ProtocoloGuardian ||
            identity.ContratoSap != contract ||
            identity.CallbackFingerprint != correlation.CallbackFingerprint ||
            string.IsNullOrWhiteSpace(p.TicketCode) ||
            p.TicketCode == p.Ticket || p.TicketCode == p.Protocol)
            return null;
        var wire = await soap.ReadTicketAsync(identity.Codigo, ct).ConfigureAwait(false);
        var ns = new XmlNamespaceManager(new NameTable());
        foreach (var pair in fields.NamespacePrefixes)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                return null;
            ns.AddNamespace(pair.Key, pair.Value);
        }
        XElement? Node(XElement element, string? xpath)
        {
            if (string.IsNullOrWhiteSpace(xpath)) return null;
            var found = element.XPathSelectElements(xpath, ns).ToArray();
            return found.Length == 1 && !string.IsNullOrWhiteSpace(found[0].Value)
                ? found[0] : null;
        }
        string? Val(XElement element, string? xpath) => Node(element,xpath)?.Value.Trim();
        // Different XPath strings/prefixes can still select the same XML node.
        // Never accept that node as two independently attested identities.
        var codeNode=Node(wire,p.TicketCode);
        var sequenceNode=Node(wire,p.Ticket);
        var protocolNode=Node(wire,p.Protocol);
        if (codeNode is null || sequenceNode is null || protocolNode is null ||
            ReferenceEquals(codeNode,sequenceNode) || ReferenceEquals(codeNode,protocolNode) ||
            ReferenceEquals(sequenceNode,protocolNode))
            return null;
        bool Decimal(XElement element, string? path, out decimal value) =>
            decimal.TryParse(Val(element,path), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out value);

        // Match the actual ticket and protocol in the provider response. Never
        // treat OC1106, protocol ID or contract number as the ticket identifier.
        if (Val(wire,p.TicketCode) != identity.Codigo ||
            Val(wire,p.Ticket) != identity.Sequencial ||
            Val(wire,p.Protocol) != correlation.ProtocoloGuardian ||
            Val(wire,p.FinalOperationCode) != "3" ||
            !Decimal(wire,p.Gross,out var gross) || !Decimal(wire,p.Tara,out var tara) ||
            !Decimal(wire,p.Net,out var net) ||
            gross != correlation.Pesos.PesoBruto || tara != correlation.Pesos.Tara ||
            net != correlation.Pesos.PesoLiquido)
            return null;
        var timestamp = Val(wire,p.FinalTime);
        if (timestamp is null ||
            !(timestamp.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(timestamp,@"[+-]\d{2}:\d{2}$")) ||
            !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var finished)) return null;
        var person = Val(wire,p.PersonType);
        var weigh = Val(wire,p.WeighingType);
        if (person is not ("PF" or "PJ") || weigh is not ("E" or "S")) return null;
        var operationText = Val(wire,p.SapOperationType);
        if (!short.TryParse(operationText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sapOp) ||
            sapOp <= 0) return null;
        // No fallback to OperacaoContrato or TipoOperacaoCodigo=3.
        if (Val(wire,p.SapOperationBusinessEvidence) != fields.SapOperationBusinessRuleEvidenceReference)
            return null;
        foreach (var (xpath, expected) in new[] {
            (p.Contract,contract),
            (p.Center,correlation.Entrada.Centro),
            (p.Material,correlation.Entrada.Material),
            (p.Reference,correlation.Entrada.DocumentoLogisticoReferencia) })
            if (!string.IsNullOrWhiteSpace(xpath) && Val(wire,xpath) != expected) return null;
        var qualityRows = wire.XPathSelectElements(p.QualityItems,ns).ToArray();
        if (qualityRows.Length == 0 || qualityRows.Length > 128) return null;
        var qualities = new List<AcmSapTicketQuality>(qualityRows.Length);
        foreach (var row in qualityRows)
        {
            var type = Val(row,p.QualityType);
            if (type is null || !Decimal(row,p.QualityValue,out var observed) || observed < 0)
                return null;
            qualities.Add(new AcmSapTicketQuality(type,Val(row,p.QualityGroup) ?? "",observed));
        }
        var propertyBp = Val(wire,p.PropertyBusinessPartner);
        if (person == "PF" && string.IsNullOrWhiteSpace(propertyBp)) return null;
        var nfe = Val(wire,p.NfeKey);
        if (weigh == "E" && (nfe is null || nfe.Length != 44 || !nfe.All(char.IsDigit)))
            return null;
        var carrier = Val(wire,p.Carrier);
        if (!string.IsNullOrWhiteSpace(carrier) && carrier != correlation.Entrada.BusinessPartnerTransportadora)
            return null;
        return new AcmGuardianVerifiedWeighingR164A(
            fields.ProviderResponseEvidenceReference + ":" + identity.EvidenceReference,
            correlation.ProtocoloGuardian,correlation.TicketGuardian,correlation.CallbackFingerprint,
            contract,correlation.Entrada.Centro,correlation.Entrada.Material,
            correlation.Entrada.DocumentoLogisticoReferencia,gross,tara,net,
            Val(wire,p.EffectiveEmitter),person,person=="PF"?propertyBp:null,
            person=="PF" && propertyBp is not null,weigh,finished,qualities,nfe,
            Val(wire,p.Driver),Val(wire,p.DriverCpf),Val(wire,p.TractorPlate),sapOp,
            "TICKET_BUSINESS_FIELD_ATTESTED",fields.SapOperationBusinessRuleEvidenceReference);
    }
}
