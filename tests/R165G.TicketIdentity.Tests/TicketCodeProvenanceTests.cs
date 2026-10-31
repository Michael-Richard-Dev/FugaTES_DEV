using System.Net;
using System.Text;
using System.Xml.Linq;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Integracao.Guardian;
using Xunit;

public sealed class TicketCodeProvenanceTests
{
    static readonly XNamespace T="urn:synthetic-r165g-not-provider";
    static readonly XNamespace S="http://schemas.xmlsoap.org/soap/envelope/";
    internal static CorrelacaoRecebimentoMvp Correlation()
    {
        var cb=new CallbackGuardianMvp("9471","8810",150,50,100,"5");
        return new("OC1106","9471",new RecebimentoSapCpiMvp("RECEBIMENTO","SYNTHETIC","7000000","0020000184","","0000001106","OC1106","AC3P","3011","10","AC3P",null,10,"0000001106"),
            "8810",cb,EstadoCorrelacaoGuardianSap.TicketAssociado,CallbackGuardianMvpFingerprint.Calcular(cb),null,new(150,50,100));
    }
    static GuardianTicketFieldContractR164D Fields()=>new(
        new("t:ticket","t:protocol","t:finalOp","t:finished","t:direction","t:person","t:gross","t:tara","t:net", "t:qualities/t:item","t:type","t:value",null,"t:operation","t:operationEvidence",NfeKey:"t:nfe"),
        new Dictionary<string,string>{{"t",T.NamespaceName}},"SYNTHETIC_CAPTURE","SYNTHETIC_RULE","SYNTHETIC_DIRECTION",true,true);
    static GuardianTicketCompletoSoapClientR164D Client(Transport transport)=>new(new HttpClient(transport),
        new(new("https://guardian.invalid/synthetic.asmx"),XName.Get("TicketCompletoObtem","http://toledobrasil.com.br/WS_Guardian"),T+"request",T+"TicketCodigo",T+"response",T+"result",
            new Dictionary<string,string>{{"t",T.NamespaceName}},new string('A',64),"SYNTHETIC_AUTH_ATTESTATION",true,false,TimeSpan.FromSeconds(1)),transport.Authentication);

    [Fact]
    public async Task Callback_sequence_alone_does_not_authorize_any_ticket_code_request()
    {
        using var transport=new Transport();
        var reader=new GuardianTicketCompletoVerifiedReaderR164D(Client(transport),Fields());
        var result=await reader.ObterAsync(Correlation(),default);
        Assert.Null(result);
        Assert.Equal(0,transport.Calls);
        Assert.Equal(0,transport.Authentication.Calls);
    }

    static GuardianTicketCodeEvidenceR165G Proof()
    {
        var c=Correlation();
        return new("TKT-00042",c.TicketGuardian!,c.ProtocoloGuardian!,c.Entrada.ContratoAcmSap!,c.CallbackFingerprint!,"SYNTHETIC_IDENTITY_RECEIPT",true);
    }
    static GuardianTicketCompletoVerifiedReaderR164D ProvenReader(Transport transport,
        GuardianTicketCodeEvidenceR165G? proof,GuardianTicketFieldContractR164D? fields=null)=>
        new(Client(transport),fields??Fields() with {Paths=Fields().Paths with {TicketCode="t:realCode"}},ticketCodeEvidence:proof);

    [Theory][InlineData("TKT-00042")][InlineData("0000042")][InlineData("8810")]
    public async Task Explicit_attested_code_is_preserved_and_checked_separately_from_sequence(string code)
    {
        using var transport=new Transport{RealCode=code};
        var result=await ProvenReader(transport,Proof() with {Codigo=code}).ObterAsync(Correlation(),default);
        Assert.NotNull(result);
        Assert.Equal("8810",result.TicketGuardian);
        Assert.Contains("SYNTHETIC_IDENTITY_RECEIPT",result.EvidenceReference);
        Assert.Equal(1,transport.Calls);Assert.Equal(1,transport.Authentication.Calls);
        var xml=XDocument.Parse(transport.RequestBody!);
        Assert.Equal(code,Assert.Single(xml.Descendants(T+"TicketCodigo")).Value);
        // Equal string values are allowed only with the same explicit proof;
        // no numerical equality is treated as evidence by the reader.
    }
    [Theory]
    [InlineData("missing")][InlineData("blank-code")][InlineData("padded-code")]
    [InlineData("sequence")][InlineData("protocol")][InlineData("contract")]
    [InlineData("fingerprint")][InlineData("reference")][InlineData("ambiguous")]
    public async Task Missing_ambiguous_or_unrelated_identity_blocks_before_authentication(string defect)
    {
        using var transport=new Transport();
        var proof=defect switch {
            "missing"=>null,"blank-code"=>Proof() with{Codigo=""},"padded-code"=>Proof() with{Codigo=" TKT-00042"},
            "sequence"=>Proof() with{Sequencial="OTHER"},"protocol"=>Proof() with{Protocolo="OTHER"},
            "contract"=>Proof() with{ContratoSap="OTHER"},"fingerprint"=>Proof() with{CallbackFingerprint="OTHER"},
            "reference"=>Proof() with{EvidenceReference=""},_=>Proof() with{UniqueAssociationConfirmed=false}
        };
        Assert.Null(await ProvenReader(transport,proof).ObterAsync(Correlation(),default));
        Assert.Equal(0,transport.Calls);Assert.Equal(0,transport.Authentication.Calls);
    }
    [Theory][InlineData(null)][InlineData("")][InlineData("t:ticket")][InlineData("t:protocol")]
    public async Task Unproven_or_aliased_response_code_path_blocks_before_transport(string? path)
    {
        using var transport=new Transport();
        var fields=Fields() with {Paths=Fields().Paths with {TicketCode=path}};
        Assert.Null(await ProvenReader(transport,Proof(),fields).ObterAsync(Correlation(),default));
        Assert.Equal(0,transport.Calls);Assert.Equal(0,transport.Authentication.Calls);
    }
    [Theory][InlineData("code")][InlineData("sequence")][InlineData("protocol")]
    public async Task Each_returned_identity_must_match_its_own_attested_value(string defect)
    {
        using var transport=new Transport();
        if(defect=="code")transport.RealCode="OTHER";
        if(defect=="sequence")transport.Sequence="OTHER";
        if(defect=="protocol")transport.Protocol="OTHER";
        Assert.Null(await ProvenReader(transport,Proof()).ObterAsync(Correlation(),default));
        Assert.Equal(1,transport.Calls);Assert.Equal(1,transport.Authentication.Calls);
    }
    [Theory][InlineData("./t:ticket","8810")][InlineData("alias:ticket","8810")][InlineData("./t:protocol","9471")]
    public async Task Semantically_aliased_elements_cannot_masquerade_as_a_separate_code(string path,string code)
    {
        using var transport=new Transport{RealCode="OTHER"};
        var fields=Fields() with {
            Paths=Fields().Paths with{TicketCode=path},
            NamespacePrefixes=new Dictionary<string,string>{{"t",T.NamespaceName},{"alias",T.NamespaceName}}
        };
        Assert.Null(await ProvenReader(transport,Proof() with{Codigo=code},fields).ObterAsync(Correlation(),default));
        Assert.Equal(1,transport.Calls);
    }
    [Fact] public async Task Caller_cancellation_prevents_authentication_and_transport()
    {
        using var transport=new Transport();
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(()=>
            ProvenReader(transport,Proof()).ObterAsync(Correlation(),cancellation.Token));
        Assert.Equal(0,transport.Calls);Assert.Equal(0,transport.Authentication.Calls);
    }

    internal sealed class Auth:IConfirmedGuardianTicketSoapAuthenticationR164D
    { public int Calls; public Task ApplyAsync(HttpRequestMessage request,CancellationToken ct){Calls++;return Task.CompletedTask;} }
    internal sealed class Transport:HttpMessageHandler
    {
        public readonly Auth Authentication=new();
        public int Calls;
        public string? RequestBody;
        public string RealCode="TKT-00042",Sequence="8810",Protocol="9471";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;RequestBody=await request.Content!.ReadAsStringAsync(ct);
            var result=new XElement(T+"result",new XElement(T+"realCode",RealCode),new XElement(T+"ticket",Sequence),new XElement(T+"protocol",Protocol),
                new XElement(T+"finalOp","3"),new XElement(T+"finished","2026-10-22T12:00:00Z"),new XElement(T+"direction","E"),new XElement(T+"person","PJ"),
                new XElement(T+"gross","150"),new XElement(T+"tara","50"),new XElement(T+"net","100"),new XElement(T+"operation","1"),new XElement(T+"operationEvidence","SYNTHETIC_RULE"),
                new XElement(T+"nfe",new string('1',44)),new XElement(T+"qualities",new XElement(T+"item",new XElement(T+"type","SYNTHETIC_QUALITY"),new XElement(T+"value","1.2"))));
            return new(HttpStatusCode.OK){Content=new StringContent(new XDocument(new XElement(S+"Envelope",new XElement(S+"Body",new XElement(T+"response",result)))).ToString(),Encoding.UTF8,"text/xml")};
        }
    }
}
