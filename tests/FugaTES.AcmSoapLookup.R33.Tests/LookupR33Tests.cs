using FugaTES.AcmSoapLookup.R33;
using System.Net;
using System.Text;
using System.Xml.Linq;
namespace FugaTES.AcmSoapLookup.R33.Tests;
public class LookupR33Tests
{
    const string Namespace="http://toledobrasil.com.br/WS_Guardian";
    static readonly Uri Endpoint=new("https://offline.invalid/soap");
    static LookupProfileR33 Profile()=>new("HML",Endpoint,new("https://offline.invalid"),"OFFLINE_READ_PROOF",DateTimeOffset.UtcNow.AddHours(1),true);
    static LookupRequestR33 Scope()=>new("0000001031","3011",2);
    static string Reply(string code="0000001031")=>"<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body><ConsultaContratoResponse xmlns='"+Namespace+"'><voRetornoContrato><VORetornoContrato><Erro>0</Erro><Contrato><Codigo>"+code+"</Codigo><Descricao>OFFLINE_CONTRACT</Descricao><ValorContrato>1.0</ValorContrato><Estado>308102</Estado><ValorProximidadeFimContrato>0</ValorProximidadeFimContrato><TipoLancamento>309837</TipoLancamento><CodigoEmissor>OFFLINE_EMISSOR</CodigoEmissor><CodigoItem>OFFLINE_ITEM</CodigoItem></Contrato></VORetornoContrato></voRetornoContrato></ConsultaContratoResponse></s:Body></s:Envelope>";
    sealed class Transport:HttpMessageHandler
    {
        public int Calls;public string Body="",Action="";public string Response=Reply();public bool Fail;
        public HttpStatusCode Status=HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {Calls++;Body=await r.Content!.ReadAsStringAsync(ct);Action=r.Headers.GetValues("SOAPAction").Single();if(Fail)throw new Exception("OFFLINE_SECRET_MUST_NOT_LEAK");return new(Status){Content=new StringContent(Response,Encoding.UTF8,"text/xml")};}
    }
    [Fact] public async Task ONLY_CONSULTACONTRATO_REACHABLE()
    {
        var t=new Transport();var r=await LookupRunnerR33.RunAsync(Scope(),Profile(),t);
        Assert.Equal("LOOKUP_CONFIRMED",r.State);Assert.Equal(1,t.Calls);Assert.Equal("\""+Namespace+"/ConsultaContrato\"",t.Action);
        var xml=XDocument.Parse(t.Body);XNamespace soap="http://schemas.xmlsoap.org/soap/envelope/";XNamespace g=Namespace;
        var op=Assert.Single(xml.Root!.Element(soap+"Body")!.Elements());Assert.Equal(g+"ConsultaContrato",op.Name);
        Assert.Equal("0000001031",op.Descendants(g+"CodigoContrato").Single().Value);Assert.Equal("UNVERIFIED",r.CenterReturned);Assert.Equal("UNVERIFIED",r.VersionReturned);
        Assert.DoesNotContain("CadastraContrato",t.Body);Assert.DoesNotContain("AlteraContrato",t.Body);
    }
    [Theory][InlineData("1031","3011",2)][InlineData("0000001031","3008",2)][InlineData("0000001031","3011",3)]
    public async Task CONTRACT_SCOPE_ENFORCED(string code,string center,int version)
    {var t=new Transport();var r=await LookupRunnerR33.RunAsync(new(code,center,version),Profile(),t);Assert.Equal("LOCAL_CONTRACT_SCOPE_INVALID",r.Reason);Assert.Equal(0,t.Calls);}
    [Theory][InlineData(false)][InlineData(true)] public async Task CONFIGURATION_INVALID_BLOCKS_TRANSPORT(bool wrongOrigin)
    {var t=new Transport();var p=wrongOrigin?Profile() with{ApprovedOrigin=new("https://other.invalid")}:Profile() with{ReadApproved=false};Assert.Equal("BLOCKED",(await LookupRunnerR33.RunAsync(Scope(),p,t)).State);Assert.Equal(0,t.Calls);}
    [Fact] public async Task MISSING_APPROVAL_REFERENCE_BLOCKED()
    {var t=new Transport();Assert.Equal("BLOCKED",(await LookupRunnerR33.RunAsync(Scope(),Profile() with{ApprovalReference=null},t)).State);Assert.Equal(0,t.Calls);}
    [Fact] public async Task INCONCLUSIVE_SOAP_INDETERMINATE()
    {var t=new Transport{Response="<invalid/>"};Assert.Equal("INDETERMINATE",(await LookupRunnerR33.RunAsync(Scope(),Profile(),t)).State);Assert.Equal(1,t.Calls);}
    [Fact] public async Task WRONG_RETURN_CODE_INDETERMINATE()
    {var t=new Transport{Response=Reply("OTHER")};Assert.Equal("INDETERMINATE",(await LookupRunnerR33.RunAsync(Scope(),Profile(),t)).State);Assert.Equal(1,t.Calls);}
    [Fact] public async Task NO_RETRY_WITH_WRITE()
    {var t=new Transport{Fail=true};var r=await LookupRunnerR33.RunAsync(Scope(),Profile(),t);Assert.Equal("INDETERMINATE",r.State);Assert.Equal(1,t.Calls);Assert.DoesNotContain("OFFLINE_SECRET",r.ToString());}
    [Theory][InlineData("CadastraContrato")][InlineData("AlteraContrato")][InlineData("ConsultaContratoExtra")]
    public async Task TRANSPORT_BLOCKS_WRITE_OPERATIONS(string op)
    {
        var t=new Transport();using var http=new HttpClient(new ConsultaOnlyTransportR33(Endpoint,t));
        using var req=new HttpRequestMessage(HttpMethod.Post,Endpoint){Content=new StringContent("<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body><"+op+" xmlns='"+Namespace+"'/></s:Body></s:Envelope>")};
        req.Headers.TryAddWithoutValidation("SOAPAction","\""+Namespace+"/"+op+"\"");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>http.SendAsync(req));Assert.Equal(0,t.Calls);
    }
    [Fact] public async Task SOAPACTION_CANNOT_HIDE_WRITE_BODY()
    {
        var t=new Transport();using var http=new HttpClient(new ConsultaOnlyTransportR33(Endpoint,t));
        using var req=new HttpRequestMessage(HttpMethod.Post,Endpoint){Content=new StringContent("<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body><CadastraContrato xmlns='"+Namespace+"'/></s:Body></s:Envelope>")};req.Headers.TryAddWithoutValidation("SOAPAction","\""+Namespace+"/ConsultaContrato\"");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>http.SendAsync(req));Assert.Equal(0,t.Calls);
    }
    [Theory][InlineData("GET")][InlineData("PUT")][InlineData("DELETE")]
    public async Task NO_SAP_POST_OR_OTHER_HTTP(string method)
    {
        var t=new Transport();using var http=new HttpClient(new ConsultaOnlyTransportR33(Endpoint,t));using var req=new HttpRequestMessage(new HttpMethod(method),new Uri("https://sap.invalid/any"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>http.SendAsync(req));Assert.Equal(0,t.Calls);
    }
    [Fact] public void NO_GENERAL_STARTUP_DEPENDENCIES()
    {
        var refs=typeof(LookupRunnerR33).Assembly.GetReferencedAssemblies().Select(x=>x.Name).ToArray();
        foreach(var forbidden in new[]{"FugaTES.WinForms","FugaTES.HostIntegracao","FugaTES.Persistencia","Npgsql","FugaTES.Integracao.SAP"})Assert.DoesNotContain(forbidden,refs);
    }
    [Fact] public void DEFAULT_INVOCATION_BLOCKED_BEFORE_IO()
    {Assert.False(Program.TryParseArguments([],out _,out _));}
    [Fact] public void CREDENTIAL_ARGUMENTS_REJECTED()
    {Assert.False(Program.TryParseArguments(["--execute-read","--code","0000001031","--center","3011","--reference-version","2","--password","OFFLINE"],out _,out _));}
    [Fact] public void EXPLICIT_SCOPE_PARSE_WITHOUT_STARTUP()
    {Assert.True(Program.TryParseArguments(["--execute-read","--code","0000001031","--center","3011","--reference-version","2","--profile",@"C:\OFFLINE_PROFILE.json"],out var r,out _));Assert.Equal(Scope(),r);}
    [Fact] public async Task PRE_CANCELLED_NO_TRANSPORT()
    {var t=new Transport();using var c=new CancellationTokenSource();c.Cancel();Assert.Equal("BLOCKED",(await LookupRunnerR33.RunAsync(Scope(),Profile(),t,c.Token)).State);Assert.Equal(0,t.Calls);}
    [Fact] public async Task NO_REDIRECT_RETRY()
    {var t=new Transport{Status=HttpStatusCode.Redirect};Assert.Equal("INDETERMINATE",(await LookupRunnerR33.RunAsync(Scope(),Profile(),t)).State);Assert.Equal(1,t.Calls);}
}
