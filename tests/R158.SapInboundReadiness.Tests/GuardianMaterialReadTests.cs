using System.Net;
using System.Text;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Integracao.Guardian;
namespace FugaTES.Testes;
public sealed class GuardianMaterialReadTests
{
 static string Body(string code="7009999",int state=1,long sequence=51)=>$"{{\"codigo\":\"{code}\",\"sequencial\":{sequence},\"estado\":{state},\"tipo\":20,\"nome\":\"OFFLINE_ITEM\",\"fatorConversao\":0.0}}";
 [Theory][InlineData("7009999")][InlineData("MAT-TEST.01")][InlineData("000ABC")]
 public async Task Exact_active_item_is_proven_by_documented_GET_and_never_created(string code)
 {var wire=new Wire(200,Body(code));using var http=new HttpClient(wire);var result=await Client(http).ConsultarAsync(code,default);Assert.True(result.Confirmado,result.Motivo);Assert.Equal(code,result.Codigo);Assert.Equal(51,result.Sequencial);Assert.Equal(133289,result.IdAssinatura);var uri=Assert.Single(wire.Gets);Assert.Equal("/api/v0/Guardian/Item/"+code,uri.AbsolutePath);Assert.Equal("?idAssinatura=133289",uri.Query);Assert.Equal(1,wire.TokenPosts);Assert.Equal(0,wire.BusinessPosts);}
 [Theory][InlineData(404,"{}","ACM_MATERIAL_NOT_FOUND")][InlineData(401,"{}","ACM_MATERIAL_AUTHENTICATION_FAILED")][InlineData(403,"{}","ACM_MATERIAL_AUTHORIZATION_FAILED")][InlineData(400,"{}","ACM_MATERIAL_LOOKUP_UNVERIFIED")][InlineData(500,"{}","ACM_MATERIAL_LOOKUP_UNVERIFIED")][InlineData(302,"{}","ACM_MATERIAL_LOOKUP_UNVERIFIED")]
 public async Task HTTP_failure_never_implies_absence_or_triggers_another_attempt(int status,string body,string expected)
 {var wire=new Wire(status,body);using var http=new HttpClient(wire);var result=await Client(http).ConsultarAsync("7009999",default);Assert.False(result.Confirmado);Assert.Equal(expected,result.Motivo);Assert.Single(wire.Gets);Assert.Equal(0,wire.BusinessPosts);}
 [Theory][InlineData("inactive")][InlineData("mismatch")][InlineData("signature")][InlineData("sequence")][InlineData("missing_type")][InlineData("invalid_type")][InlineData("missing_name")][InlineData("missing_factor")][InlineData("missing_state")][InlineData("array")][InlineData("duplicate")][InlineData("malformed")][InlineData("oversize")]
 public async Task Inactive_foreign_or_invalid_response_cannot_authorize_a_contract(string fault)
 {var body=fault switch{"inactive"=>Body(state:0),"mismatch"=>Body("OTHER"),"signature"=>Body()[..^1]+",\"idAssinatura\":999}","sequence"=>Body(sequence:0),"missing_type"=>Body().Replace("\"tipo\":20,",""),"invalid_type"=>Body().Replace("\"tipo\":20","\"tipo\":99"),"missing_name"=>Body().Replace("\"nome\":\"OFFLINE_ITEM\",",""),"missing_factor"=>Body().Replace(",\"fatorConversao\":0.0",""),"missing_state"=>Body().Replace("\"estado\":1,",""),"array"=>"["+Body()+"]","duplicate"=>Body()[..^1]+",\"codigo\":\"7009999\"}","malformed"=>"not JSON SECRET_MUST_NOT_LEAK",_=>new string('x',262145)};var wire=new Wire(200,body);using var http=new HttpClient(wire);var result=await Client(http).ConsultarAsync("7009999",default);Assert.False(result.Confirmado);Assert.Single(wire.Gets);Assert.Equal(0,wire.BusinessPosts);Assert.DoesNotContain("SECRET",result.Motivo);Assert.Equal(fault=="inactive"?"ACM_MATERIAL_INACTIVE":fault=="mismatch"?"ACM_MATERIAL_IDENTITY_MISMATCH":fault=="signature"?"ACM_MATERIAL_SCOPE_MISMATCH":"ACM_MATERIAL_RESPONSE_INVALID",result.Motivo);}
 [Theory][InlineData("../Token")][InlineData("A/B")][InlineData("A?scope=9")][InlineData("")][InlineData("0000")][InlineData(" 7009999")][InlineData("A\\B")][InlineData(".")][InlineData("..")] public async Task Invalid_code_never_reaches_authentication_or_HTTP(string code)
 {var wire=new Wire(200,Body());using var http=new HttpClient(wire);Assert.False((await Client(http).ConsultarAsync(code,default)).Confirmado);Assert.Empty(wire.Gets);Assert.Equal(0,wire.TokenPosts);}
 [Theory][InlineData("http://guardian.cloudprix.com.br",133289)][InlineData("https://attacker.invalid",133289)][InlineData("https://guardian.cloudprix.com.br",0)] public async Task Wrong_profile_is_rejected_before_using_credentials(string origin,int signature)
 {var wire=new Wire(200,Body());using var http=new HttpClient(wire);Assert.False((await Client(http,origin,signature).ConsultarAsync("7009999",default)).Confirmado);Assert.Empty(wire.Gets);Assert.Equal(0,wire.TokenPosts);}
 static IGuardianMaterialRead Client(HttpClient http,string origin="https://guardian.cloudprix.com.br",int signature=133289)
 {var o=new GuardianHttpOptions{BaseUrl=new Uri(origin),Timeout=TimeSpan.FromSeconds(3)};var type=typeof(GuardianTokenClient).Assembly.GetType("FugaTES.Integracao.Guardian.GuardianMaterialReadAdapter");Assert.NotNull(type);return Assert.IsAssignableFrom<IGuardianMaterialRead>(Activator.CreateInstance(type,http,o,new GuardianTokenClient(http,o,new Credential()),signature));}
 sealed class Credential:ICredenciaisGuardianSet26Provider{public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct)=>Task.FromResult(new CredenciaisGuardianSet26("OFFLINE_ONLY","OFFLINE_ONLY","OFFLINE_SIGNATURE"));}
 sealed class Wire(int status,string body):HttpMessageHandler
 {
  public List<Uri> Gets=[];public int TokenPosts,BusinessPosts;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {if(request.Method==HttpMethod.Post&&request.RequestUri!.AbsolutePath=="/api/v2/CreateToken"){TokenPosts++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"token\":\"OFFLINE_TOKEN\"}")});}if(request.Method!=HttpMethod.Get){BusinessPosts++;throw new InvalidOperationException("NO_BUSINESS_WRITE");}Gets.Add(request.RequestUri!);Assert.Equal("Bearer",request.Headers.Authorization?.Scheme);return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent(body,Encoding.UTF8,"application/json")});}
 }
}
