using System.Net;
using System.Text;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Tools;
namespace FugaTES.Testes;
public sealed class SapProviderReadOnlyVerificationTests
{
 static FugaTES.Aplicacao.Configuracao.AmbienteSap Dev=>FugaTES.Aplicacao.Configuracao.AmbientesSap.Dev110;

 [Fact] public async Task Matches_the_proven_fresh_Basic_GET_then_fetches_own_CSRF_session_without_POST()
 {var w=new Wire();var a=new Auth();using var http=new HttpClient(w);var result=await SapProviderReadOnlyVerification.CheckAsync(http,a,Dev,default);Assert.True(result.Passed);Assert.True(result.MetadataConfirmed);Assert.True(result.CsrfConfirmed);Assert.Equal(2,a.Calls);Assert.Equal(2,w.Calls);Assert.Equal(0,w.Posts);}
 [Theory][InlineData(401)][InlineData(403)][InlineData(302)]
 public async Task Rejected_or_redirected_metadata_is_not_retried_and_never_fetches_CSRF(int status)
 {var w=new Wire{MetadataStatus=status};using var http=new HttpClient(w);var result=await SapProviderReadOnlyVerification.CheckAsync(http,new Auth(),Dev,default);Assert.False(result.Passed);Assert.Equal(status,result.HttpStatus);Assert.Equal(1,w.Calls);Assert.Equal(0,w.Posts);}
 [Theory][InlineData("<html>login</html>")][InlineData("<entry/>")][InlineData("<!DOCTYPE x [<!ENTITY y SYSTEM 'file:///secret'>]><x>&y;</x>")]
 public async Task HTTP200_without_the_SAP_metadata_contract_cannot_confirm_authentication(string body)
 {var w=new Wire{Metadata=body};using var http=new HttpClient(w);var result=await SapProviderReadOnlyVerification.CheckAsync(http,new Auth(),Dev,default);Assert.False(result.Passed);Assert.False(result.MetadataConfirmed);Assert.Equal(1,w.Calls);}
 [Fact] public async Task Missing_CSRF_token_blocks_activation_without_logging_sensitive_headers()
 {var w=new Wire{Token=false};using var http=new HttpClient(w);var result=await SapProviderReadOnlyVerification.CheckAsync(http,new Auth(),Dev,default);Assert.False(result.Passed);Assert.True(result.MetadataConfirmed);Assert.False(result.CsrfConfirmed);Assert.Equal("CSRF",result.Phase);Assert.Equal(0,w.Posts);Assert.DoesNotContain("SYNTHETIC",System.Text.Json.JsonSerializer.Serialize(result));}
 /// <summary>
 /// A mesma checagem contra o QAS120. Ate 05/11/2026 a origem e o sap-client estavam escritos
 /// dentro do verificador: rodada com as credenciais do QAS ela sondaria o DEV e diria PASS, e
 /// quem a rodou concluiria que o provedor do QAS esta bom. O Wire recusa host ou cliente que
 /// nao sejam os do ambiente pedido, por isso este teste so passa se o ambiente for respeitado.
 /// </summary>
 [Theory][InlineData("DEV110")][InlineData("QAS120")]
 public async Task O_provedor_e_conferido_no_ambiente_pedido(string nome)
 {
  var ambiente=FugaTES.Aplicacao.Configuracao.AmbientesSap.Aprovados.Single(a=>a.Nome==nome);
  var w=new Wire{Ambiente=ambiente};var a=new Auth();using var http=new HttpClient(w);
  var result=await SapProviderReadOnlyVerification.CheckAsync(http,a,ambiente,default);
  Assert.True(result.Passed);Assert.Equal(2,w.Calls);Assert.Equal(0,w.Posts);
 }
 sealed class Auth:IAutenticacaoSap{public int Calls;public Task AplicarAsync(HttpRequestMessage r,CancellationToken c){Calls++;r.Headers.Authorization=new("Basic","SYNTHETIC_ONLY");return Task.CompletedTask;}}
 sealed class Wire:HttpMessageHandler
 {
  public int Calls,Posts,MetadataStatus=200;public bool Token=true;
  // O ambiente conferido e o que foi PASSADO, nao o DEV escrito: e isso que faz este teste
  // valer para o QAS120 tambem.
  public FugaTES.Aplicacao.Configuracao.AmbienteSap Ambiente=FugaTES.Aplicacao.Configuracao.AmbientesSap.Dev110;
  public string Metadata="""<edmx:Edmx xmlns:edmx="http://schemas.microsoft.com/ado/2007/06/edmx"><edmx:DataServices><Schema xmlns="http://schemas.microsoft.com/ado/2008/09/edm"><EntityContainer><EntitySet Name="ReceberPesagem" EntityType="cds.ReceberPesagemType" /></EntityContainer></Schema></edmx:DataServices></edmx:Edmx>""";
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
  {
   Calls++;if(r.Method==HttpMethod.Post)Posts++;Assert.Equal(HttpMethod.Get,r.Method);Assert.Equal(new Uri(Ambiente.Origem).Host,r.RequestUri!.Host);Assert.Equal(new Uri(Ambiente.Origem).Port,r.RequestUri.Port);Assert.Equal("?sap-client="+Ambiente.SapClient,r.RequestUri.Query);Assert.NotNull(r.Headers.Authorization);Assert.False(r.Headers.Contains("Cookie"));
   if(r.RequestUri.AbsolutePath.EndsWith("/$metadata")){Assert.Equal("*/*",r.Headers.GetValues("Accept").Single());return Task.FromResult(new HttpResponseMessage((HttpStatusCode)MetadataStatus){Content=new StringContent(Metadata,Encoding.UTF8,"application/xml")});}
   Assert.Equal("/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/",r.RequestUri.AbsolutePath);Assert.Equal("Fetch",r.Headers.GetValues("X-CSRF-Token").Single());var response=new HttpResponseMessage(HttpStatusCode.OK);if(Token)response.Headers.Add("X-CSRF-Token","SYNTHETIC_CSRF");response.Headers.Add("Set-Cookie","SESSION=SYNTHETIC; Path=/");return Task.FromResult(response);
  }
 }
}
