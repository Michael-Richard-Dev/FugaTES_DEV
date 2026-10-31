using System.Xml;
using System.Xml.Linq;
namespace FugaTES.AcmSoapLookup.R33;
// Defense in depth: the full Guardian assembly contains writers, but this transport cannot send them.
public sealed class ConsultaOnlyTransportR33(Uri endpoint,HttpMessageHandler inner):DelegatingHandler(inner)
{
    const string Guardian="http://toledobrasil.com.br/WS_Guardian";
    static readonly XNamespace Soap="http://schemas.xmlsoap.org/soap/envelope/";
    static readonly XNamespace Contract=Guardian;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        if(request.Method!=HttpMethod.Post||request.RequestUri!=endpoint||request.Content is null||
            !request.Headers.TryGetValues("SOAPAction",out var actions)||!actions.SequenceEqual(new[]{"\""+Guardian+"/ConsultaContrato\""}))
            throw new InvalidOperationException("SOAP_OPERATION_NOT_ALLOWED");
        var text=await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var reader=XmlReader.Create(new StringReader(text),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536});
        var xml=XDocument.Load(reader);var root=xml.Root;
        if(root?.Name!=Soap+"Envelope"||root.Elements().Count()!=1||root.Elements().Single().Name!=Soap+"Body")throw new InvalidOperationException("SOAP_ENVELOPE_NOT_ALLOWED");
        var body=root.Element(Soap+"Body")!;
        if(body.Elements().Count()!=1||body.Elements().Single().Name!=Contract+"ConsultaContrato")throw new InvalidOperationException("SOAP_OPERATION_NOT_ALLOWED");
        var operation=body.Elements().Single();
        if(operation.Elements().Count()!=1||operation.Elements().Single().Name!=Contract+"voDadosContratoCodigo")throw new InvalidOperationException("SOAP_ARGUMENTS_NOT_ALLOWED");
        var args=operation.Elements().Single();
        if(args.Elements().Count()!=1||args.Elements().Single().Name!=Contract+"CodigoContrato"||args.Elements().Single().HasElements||args.Elements().Single().Value!=LookupRunnerR33.AllowedCode)
            throw new InvalidOperationException("SOAP_CODE_NOT_ALLOWED");
        return await base.SendAsync(request,ct).ConfigureAwait(false);
    }
}
