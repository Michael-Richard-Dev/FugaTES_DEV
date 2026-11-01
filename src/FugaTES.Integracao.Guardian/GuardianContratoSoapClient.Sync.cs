using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Guardian;

namespace FugaTES.Integracao.Guardian;

public sealed partial class GuardianContratoSoapClient
{
    public async Task<ConsultaContratoGuardian> ConsultarAsync(string codigo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(codigo)) throw new GuardianHttpException("Contract code is required.");
        var response = await EnviarOperacaoAsync("ConsultaContrato", new XElement(ContractNamespace + "voDadosContratoCodigo",
            new XElement(ContractNamespace + "CodigoContrato", codigo)), ct);
        var item = ObterRetornoUnicoSemValidarErro(response);
        if (item is null) return new(EstadoConsultaContratoGuardian.Indeterminado);
        // The response belongs to this exact ConsultaContrato request; preserve its literal code.
        // Only the approved HML business-absence response proves absence, never a generic failure.
        var error=Child(item,"Erro");
        if(error is null || !int.TryParse(error.Value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var businessCode))
            return new(EstadoConsultaContratoGuardian.Indeterminado);
        if(businessCode!=0)
        {
            var endpoint=options.ContratoSoapEndpoint;
            var contracts=item.Elements(ContractNamespace+"Contrato").ToArray();
            var noContract=contracts.Length==0 || (contracts.Length==1 && !contracts[0].HasElements && string.IsNullOrWhiteSpace(contracts[0].Value) &&
                contracts[0].Attributes().All(a=>a.IsNamespaceDeclaration || (a.Name==XName.Get("nil","http://www.w3.org/2001/XMLSchema-instance") && a.Value=="true")));
            var approvedAbsence=options.ContratoSoapEnvironment=="HML" && options.ContratoSoapInternalHmlHttpApproved &&
                endpoint is not null && endpoint.Scheme=="http" && endpoint.Host=="192.168.17.91" && endpoint.Port==80 &&
                endpoint.AbsolutePath=="/WS_GUARDIAN/WS_GUARDIAN_PLUS.asmx" && string.IsNullOrEmpty(endpoint.Query) &&
                businessCode==309851 && Child(item,"Descricao")?.Value=="O código do contrato informado não existe!" && noContract;
            return new(approvedAbsence?EstadoConsultaContratoGuardian.InexistenteComprovado:EstadoConsultaContratoGuardian.Indeterminado);
        }
        var contract = Child(item, "Contrato");
        if (contract is null) return new(EstadoConsultaContratoGuardian.Indeterminado);
        string? Value(string name)
        {
            var node = Child(contract, name);
            return node is null ? null : node.HasElements ? Child(node, "Codigo")?.Value : node.Value;
        }
        // VOContratoOut exposes EmissorIntegracao/ItemIntegracao with their own Codigo.
        // Keep legacy direct fields compatible, but never choose between conflicting identities.
        string? IntegrationCode(string legacyName, string outputName)
        {
            var legacy = contract.Elements(ContractNamespace + legacyName).ToArray();
            var output = contract.Elements(ContractNamespace + outputName).ToArray();
            if (legacy.Length > 1 || output.Length > 1) return null;
            var directCode = legacy.Length == 1 ? Value(legacyName) : null;
            var nestedCode = output.Length == 1 ? Child(output[0], "Codigo")?.Value : null;
            if ((legacy.Length == 1 && string.IsNullOrWhiteSpace(directCode)) ||
                (output.Length == 1 && string.IsNullOrWhiteSpace(nestedCode)) ||
                (directCode is not null && nestedCode is not null && directCode != nestedCode)) return null;
            return directCode ?? nestedCode;
        }
        var fields = new[] { Value("Codigo"), Value("Descricao"), Value("Estado"), Value("TipoLancamento"),
            IntegrationCode("CodigoEmissor", "EmissorIntegracao"), IntegrationCode("CodigoItem", "ItemIntegracao") };
        if (fields.Any(string.IsNullOrWhiteSpace) || fields[0] != codigo ||
            !decimal.TryParse(Value("ValorContrato"), NumberStyles.Number, CultureInfo.InvariantCulture, out var valor) ||
            !decimal.TryParse(Value("ValorProximidadeFimContrato"), NumberStyles.Number, CultureInfo.InvariantCulture, out var proximidade))
            return new(EstadoConsultaContratoGuardian.Indeterminado);
        IReadOnlyList<VeiculoContratoGuardian>? vehicles = null;
        var vehicleNode = Child(contract, "Veiculos");
        if (vehicleNode is not null)
        {
            var list = new List<VeiculoContratoGuardian>();
            foreach (var v in vehicleNode.Elements())
            {
                var code = Child(v, "Codigo")?.Value;
                var plate = Child(v, "PlacaVeiculo")?.Value;
                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(plate))
                    return new(EstadoConsultaContratoGuardian.Indeterminado);
                list.Add(new(code, plate));
            }
            vehicles = list;
        }
        var sequenceValue=Value("Sequencial"); long? sequence=null;
        if(sequenceValue is not null) { if(!long.TryParse(sequenceValue,out var parsed) || parsed<=0)return new(EstadoConsultaContratoGuardian.Indeterminado);sequence=parsed; }
        return new(EstadoConsultaContratoGuardian.Existente,
            new(fields[0]!, fields[1]!, valor, fields[2]!, proximidade, fields[3]!)
            { CodigoEmissor = fields[4], CodigoItem = fields[5], Veiculos = vehicles },sequence);
    }

    public async Task<ResultadoContratoGuardianComResposta> AlterarComRespostaAsync(
        ContratoGuardianSet26 atual, ContratoGuardianSet26 desejado, CancellationToken ct)
    {
        if (atual.Codigo != desejado.Codigo || atual.TipoLancamento != desejado.TipoLancamento)
            throw new GuardianHttpException("Contract identity and posting type cannot change.");
        if (new[] { desejado.Codigo, desejado.Descricao, desejado.Estado, desejado.TipoLancamento }.Any(string.IsNullOrWhiteSpace))
            throw new GuardianHttpException("Contract update is incomplete.");
        var contract = new XElement(ContractNamespace + "Contrato",
            new XElement(ContractNamespace + "Codigo", desejado.Codigo),
            new XElement(ContractNamespace + "Descricao", desejado.Descricao),
            new XElement(ContractNamespace + "ValorContrato", desejado.ValorContrato.ToString(CultureInfo.InvariantCulture)),
            new XElement(ContractNamespace + "Estado", desejado.Estado),
            new XElement(ContractNamespace + "ValorProximidadeFimContrato", desejado.ValorProximidadeFimContrato.ToString(CultureInfo.InvariantCulture)),
            new XElement(ContractNamespace + "TipoLancamento", atual.TipoLancamento));
        if (desejado.CodigoEmissor is not null) contract.Add(new XElement(ContractNamespace + "CodigoEmissor", desejado.CodigoEmissor));
        if (desejado.CodigoItem is not null) contract.Add(new XElement(ContractNamespace + "CodigoItem", desejado.CodigoItem));
        if (desejado.Veiculos is not null)
            contract.Add(new XElement(ContractNamespace + "Veiculos", desejado.Veiculos.Select(v =>
                new XElement(ContractNamespace + "Veiculo", new XElement(ContractNamespace + "Codigo", v.Codigo),
                    new XElement(ContractNamespace + "PlacaVeiculo", v.PlacaVeiculo)))));
        var response = await EnviarOperacaoAsync("AlteraContrato", new XElement(ContractNamespace + "voDadosContrato", contract), ct);
        var item = ObterRetornoUnico(response);
        var code = item is null ? null : Child(Child(item, "Contrato"), "Codigo")?.Value;
        if (code != desejado.Codigo) throw new GuardianHttpException("Contract update identity is not proven.");
        return new(code);
    }

    private async Task<XElement?> EnviarOperacaoAsync(string operation, XElement parameters, CancellationToken ct)
    {
        options.ValidateContratoSoapEndpoint();
        var endpoint = options.ContratoSoapEndpoint;
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") || options.Timeout <= TimeSpan.Zero)
            throw new GuardianHttpException("Guardian contract endpoint or timeout is invalid.");
        var envelope = new XDocument(new XElement(SoapNamespace + "Envelope", new XAttribute(XNamespace.Xmlns + "soap", SoapNamespace),
            new XElement(SoapNamespace + "Body", new XElement(ContractNamespace + operation, parameters))));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        { Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml") };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{GuardianNamespace}/{operation}\"");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode || (operation=="ConsultaContrato" && response.StatusCode!=System.Net.HttpStatusCode.OK))
            throw new GuardianHttpException("Guardian contract operation failed.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        var body = document.Root?.Name == SoapNamespace + "Envelope" ? document.Root.Element(SoapNamespace + "Body") : null;
        if(body?.Element(SoapNamespace+"Fault") is not null)throw new GuardianHttpException("Guardian contract SOAP fault.");
        var operations = body?.Elements(ContractNamespace + (operation + "Response")).ToArray() ?? [];
        return operations.Length == 1 ? operations[0] : null;
    }

    private static XElement? Child(XElement? parent, string name)
    {
        var nodes = parent?.Elements().Where(e => e.Name.Namespace == ContractNamespace && e.Name.LocalName == name).ToArray() ?? [];
        return nodes.Length == 1 ? nodes[0] : null;
    }

    private static XElement? ObterRetornoUnico(XElement? operation)
    {
        var entry=ObterRetornoUnicoSemValidarErro(operation);
        var error=Child(entry,"Erro");
        return error is not null && int.TryParse(error.Value,out var code) && code==0?entry:null;
    }

    private static XElement? ObterRetornoUnicoSemValidarErro(XElement? operation)
    {
        var container = Child(operation, "voRetornoContrato");
        if (container is null) return null;
        var wrapper = Child(container, "ArrayOfVORetornoContrato");
        // Accept the array type's direct entries or its explicit wrapper, never both.
        if (wrapper is not null && container.Elements().Count() != 1) return null;
        var entries = (wrapper ?? container).Elements().ToArray();
        if (entries.Length != 1 || entries[0].Name != ContractNamespace + "VORetornoContrato") return null;
        return entries[0];
    }
}
