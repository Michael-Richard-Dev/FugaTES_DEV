using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Guardian;

namespace FugaTES.Integracao.Guardian;

public sealed partial class GuardianContratoSoapClient(HttpClient httpClient, GuardianHttpOptions options)
    : IGuardianContratoSincronizacaoGateway
{
    private const string GuardianNamespace = "http://toledobrasil.com.br/WS_Guardian";
    private const string Action = GuardianNamespace + "/CadastraContrato";
    private static readonly XNamespace SoapNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace ContractNamespace = GuardianNamespace;

    public async Task CadastrarAsync(ContratoGuardianSet26 contrato, CancellationToken cancellationToken)
    {
        _ = await CadastrarComRespostaAsync(contrato, cancellationToken);
    }

    public async Task<ResultadoContratoGuardianComResposta> CadastrarComRespostaAsync(
        ContratoGuardianSet26 contrato, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contrato);
        if (new[] { contrato.Codigo, contrato.Descricao, contrato.Estado, contrato.TipoLancamento }.Any(string.IsNullOrWhiteSpace))
            throw new GuardianHttpException("Guardian contract request is incomplete.");
        options.ValidateContratoSoapEndpoint();
        var endpoint = options.ContratoSoapEndpoint;
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") || options.Timeout <= TimeSpan.Zero)
            throw new GuardianHttpException("Guardian contract SOAP endpoint or timeout is invalid.");

        var envelope = GuardianContratoSoapCodec.CriarCadastro(contrato);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
            };
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{Action}\"");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new GuardianHttpException($"Guardian contract request failed with HTTP {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            var body = document.Root?.Name == SoapNamespace + "Envelope"
                ? document.Root.Element(SoapNamespace + "Body") : null;
            var operation = body?.Elements(ContractNamespace + "CadastraContratoResponse").SingleOrDefault();
            var errors = operation?.Descendants().Where(element => element.Name.LocalName == "Erro").ToArray() ?? [];
            if (errors.Length != 1 || !int.TryParse(errors[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                throw new GuardianHttpException("Guardian contract response is not successful.");
            if(code!=0) throw new GuardianHttpException("Guardian contract was rejected.") { BusinessErrorCode=code };
            var codes = operation?.Descendants().Where(element => element.Name.LocalName == "Contrato")
                .SelectMany(element => element.Elements().Where(child => child.Name.LocalName == "Codigo"))
                .Select(element => element.Value).ToArray() ?? [];
            if (codes.Length > 1 || (codes.Length == 1 && string.IsNullOrWhiteSpace(codes[0])))
                throw new GuardianHttpException("Guardian contract response identity is ambiguous.");
            var sequences=operation?.Descendants().Where(e=>e.Name.LocalName=="Contrato").SelectMany(e=>e.Elements().Where(c=>c.Name.LocalName=="Sequencial")).ToArray() ?? [];
            long? sequence=null;
            if(sequences.Length>1 || (sequences.Length==1 && (!long.TryParse(sequences[0].Value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var parsed) || parsed<=0)))
                throw new GuardianHttpException("Guardian contract sequence is ambiguous.");
            if(sequences.Length==1)sequence=long.Parse(sequences[0].Value,CultureInfo.InvariantCulture);
            return new ResultadoContratoGuardianComResposta(codes.Length == 1 ? codes[0] : null,sequence);
        }
        catch (XmlException exception)
        {
            throw new GuardianHttpException("Guardian contract response is invalid.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GuardianHttpException("Guardian contract request timed out.", exception);
        }
    }
}
