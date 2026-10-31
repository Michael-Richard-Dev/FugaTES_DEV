using System.Net;
using System.Text;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Integracao.Guardian;
namespace FugaTES.Testes;
public sealed class GuardianEmitterReadTests
{
    [Fact] public async Task New_emitter_is_proven_by_exact_code_active_status_and_signature_scoped_get()
    {
        var wire=new Wire(200,"{\"codigo\":\"20000169\",\"sequencial\":321,\"estado\":1}");
        using var http=new HttpClient(wire);var result=await Client(http).ConsultarAsync("20000169",default);
        Assert.True(result.Confirmado,result.Motivo);Assert.Equal("20000169",result.Codigo);Assert.Equal(321,result.Sequencial);Assert.Equal(133289,result.IdAssinatura);
        var uri=Assert.Single(wire.Gets);Assert.Equal("/api/v0/Guardian/Emissor/20000169",uri.AbsolutePath);Assert.Equal("?idAssinatura=133289",uri.Query);
        Assert.Equal(1,wire.TokenPosts);Assert.Equal(0,wire.BusinessPosts);
    }
    [Theory]
    [InlineData(404,"{}","R171_EMITTER_NOT_FOUND")]
    [InlineData(401,"{}","R171_EMITTER_AUTHENTICATION_FAILED")]
    [InlineData(403,"{}","R171_EMITTER_AUTHORIZATION_FAILED")]
    [InlineData(400,"{}","R171_EMITTER_LOOKUP_UNVERIFIED")]
    [InlineData(500,"{}","R171_EMITTER_LOOKUP_UNVERIFIED")]
    [InlineData(200,"{\"codigo\":\"20000169\",\"sequencial\":321,\"estado\":0}","R171_EMITTER_INACTIVE")]
    [InlineData(200,"{\"codigo\":\"99999\",\"sequencial\":321,\"estado\":1}","R171_EMITTER_IDENTITY_MISMATCH")]
    [InlineData(200,"{\"codigo\":\"20000169\",\"sequencial\":0,\"estado\":1}","R171_EMITTER_RESPONSE_INVALID")]
    [InlineData(200,"[]","R171_EMITTER_RESPONSE_INVALID")]
    [InlineData(200,"[{\"codigo\":\"20000169\",\"sequencial\":321,\"estado\":1}]","R171_EMITTER_RESPONSE_INVALID")]
    [InlineData(200,"{\"codigo\":\"20000169\",\"codigo\":\"20000169\",\"sequencial\":321,\"estado\":1}","R171_EMITTER_RESPONSE_INVALID")]
    [InlineData(200,"not JSON SECRET_PASSWORD","R171_EMITTER_RESPONSE_INVALID")]
    public async Task Unproven_lookup_never_approves_or_retries(int status,string body,string code)
    {var wire=new Wire(status,body);using var http=new HttpClient(wire);var result=await Client(http).ConsultarAsync("20000169",default);Assert.False(result.Confirmado);Assert.Equal(code,result.Motivo);Assert.Single(wire.Gets);Assert.Equal(0,wire.BusinessPosts);Assert.DoesNotContain("SECRET",result.Motivo);}
    [Theory][InlineData("http://guardian.cloudprix.com.br",133289)][InlineData("https://attacker.invalid",133289)][InlineData("https://guardian.cloudprix.com.br",0)]
    public async Task Wrong_origin_or_signature_is_blocked_before_authentication(string origin,int signature)
    {var wire=new Wire(200,"{}");using var http=new HttpClient(wire);var result=await Client(http,origin,signature).ConsultarAsync("20000169",default);Assert.False(result.Confirmado);Assert.Equal(0,wire.TokenPosts);Assert.Empty(wire.Gets);}
    [Theory][InlineData("../Token")][InlineData("00000000")][InlineData("20000169?scope=1")][InlineData("")]
    public async Task Malformed_candidate_cannot_become_a_route_or_lookup(string code)
    {var wire=new Wire(200,"{}");using var http=new HttpClient(wire);Assert.False((await Client(http).ConsultarAsync(code,default)).Confirmado);Assert.Empty(wire.Gets);Assert.Equal(0,wire.TokenPosts);}
    [Fact] public async Task Response_size_limit_blocks_before_json_materialization()
    {var wire=new Wire(200,new string('x',262145));using var http=new HttpClient(wire);Assert.False((await Client(http).ConsultarAsync("20000169",default)).Confirmado);}
    static IGuardianEmitterRead Client(HttpClient http,string origin="https://guardian.cloudprix.com.br",int signature=133289)
    {
        var options=new GuardianHttpOptions{BaseUrl=new Uri(origin),Timeout=TimeSpan.FromSeconds(3)};
        var type=typeof(GuardianTokenClient).Assembly.GetType("FugaTES.Integracao.Guardian.GuardianEmitterReadAdapter");Assert.NotNull(type);
        return Assert.IsAssignableFrom<IGuardianEmitterRead>(Activator.CreateInstance(type,http,options,new GuardianTokenClient(http,options,new Credential()),signature));
    }
    private sealed class Credential:ICredenciaisGuardianSet26Provider{public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct)=>Task.FromResult(new CredenciaisGuardianSet26("TEST_ONLY","TEST_ONLY","TOKEN_SECRET_NOT_QUERY_SCOPE"));}
    private sealed class Wire(int status,string body):HttpMessageHandler
    {
        public List<Uri> Gets=[];public int TokenPosts,BusinessPosts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.Method==HttpMethod.Post&&request.RequestUri!.AbsolutePath=="/api/v2/CreateToken"){TokenPosts++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"token\":\"TEST_ONLY_TOKEN\"}")});}
            if(request.Method!=HttpMethod.Get){BusinessPosts++;throw new InvalidOperationException("BUSINESS_POST_FORBIDDEN");}
            Gets.Add(request.RequestUri!);Assert.Equal("Bearer",request.Headers.Authorization?.Scheme);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
}
