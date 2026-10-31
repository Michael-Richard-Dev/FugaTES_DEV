using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Callback.Http;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
namespace FugaTES.Testes;

// Test-only source admission. Never referenced or registered by a production composition.
internal sealed class OfflineToledoAdmission:IToledoCallbackAdmission
{public Task<ToledoCallbackActor> AuthorizeAsync(HttpContext c,CancellationToken ct)=>Task.FromResult(new ToledoCallbackActor(true,"OFFLINE_TEST_SOURCE",200,"OFFLINE"));}

public sealed class ToledoUnifiedHttpTests
{
 public static IEnumerable<object[]> Fixtures()
 {yield return ["MASKED_REGRESSION"];if(Environment.GetEnvironmentVariable("FUGATES_TOLEDO_PROVIDED_FIXTURE") is {Length:>0})yield return ["USER_PROVIDED_PRIVATE"];}
 static string Payload(string name)=>name=="USER_PROVIDED_PRIVATE"?File.ReadAllText(Environment.GetEnvironmentVariable("FUGATES_TOLEDO_PROVIDED_FIXTURE")!):ToledoCallbackTests.ValidJson;
 [Theory] [MemberData(nameof(Fixtures))]
 public void Complete_fixture_keeps_every_source_value_without_printing_personal_data(string fixture)
 {
  var json=Payload(fixture);using var raw=JsonDocument.Parse(json);var dto=ToledoPesagemCallbackDto.Deserializar(raw.RootElement);
  using var preserved=JsonDocument.Parse(dto.DocumentoJson);Assert.True(JsonElement.DeepEquals(raw.RootElement,preserved.RootElement),"Full payload fidelity failed");
  Assert.Equal("0020318",dto.IdPesagem);Assert.Equal("0000001155",dto.Pesagens.Single().ContratoACM);Assert.Equal(new DateTime(2026,10,27,12,0,22),dto.DataPesagemLocal);
  Assert.Equal(raw.RootElement.GetProperty("to_QualidPes").GetArrayLength(),dto.Qualidades.Count);
  Assert.Equal(40000m,dto.PesoBruto);Assert.Equal(20000m,dto.PesoTara);Assert.Equal(DateTimeKind.Unspecified,dto.DataPesagemLocal.Kind);
  Assert.Equal(JsonValueKind.String,preserved.RootElement.GetProperty("IdPesagem").ValueKind);
  foreach(var field in new[]{"NomeMotorista","CpfMotorista","Placa","UsuarioPesagem"})Assert.True(
    raw.RootElement.GetProperty(field).GetRawText()==preserved.RootElement.GetProperty(field).GetRawText(),"Source personal field fidelity failed");
  if(fixture=="USER_PROVIDED_PRIVATE")
  {Assert.Equal(9,dto.Qualidades.Count);Assert.All(dto.Qualidades,q=>Assert.Equal("",q.Tipo));Assert.Equal("0.0",dto.Qualidades[0].ValorAferido);Assert.Equal("",dto.Qualidades[^1].ValorAferido);
   Assert.Contains("1147",raw.RootElement.GetProperty("to_Pesagem")[0].GetProperty("ParceiroNome").GetString());Assert.Equal("0000001155",dto.Mapear().Itens.Single().ContratoACM);}
 }
 [Theory] [InlineData("wrong-secret",401)] [InlineData("absent",401)] [InlineData("wrong-peer",403)] [InlineData("http",403)] [InlineData("forged-xff",403)]
 public async Task Invalid_authentication_or_forwarded_source_never_proves_identity(string mode,int expected)
 {
  var o=Options();var reads=0;var auth=new ProtectedToledoCallbackAdmission(o,_=>{reads++;return Encoding.UTF8.GetBytes(new string('K',64));});
  var c=Context();c.Request.Headers[o.HeaderName]=mode=="wrong-secret"?new string('X',64):new string('K',64);
  if(mode=="absent")c.Request.Headers.Remove(o.HeaderName);
  if(mode is "wrong-peer" or "forged-xff")c.Connection.RemoteIpAddress=IPAddress.Parse("192.0.2.9");
  if(mode=="forged-xff"){c.Request.Headers["X-Forwarded-For"]="127.0.0.1";c.Request.Headers["X-Forwarded-Proto"]="https";}
  if(mode=="http")c.Request.Scheme="http";
  var result=await auth.AuthorizeAsync(c,default);Assert.False(result.Accepted);Assert.Equal(expected,result.HttpStatus);
  if(expected==403)Assert.Equal(0,reads);
 }
 [Fact] public async Task Valid_source_requires_secret_as_well_as_HTTPS_and_peer()
 {
  var o=Options();var c=Context();c.Request.Headers[o.HeaderName]=new string('K',64);
  var auth=new ProtectedToledoCallbackAdmission(o,_=>Encoding.UTF8.GetBytes(new string('K',64)));
  Assert.True((await auth.AuthorizeAsync(c,default)).Accepted);
  c.Request.Headers[o.HeaderName]=new Microsoft.Extensions.Primitives.StringValues([new string('K',64),new string('K',64)]);
  Assert.False((await auth.AuthorizeAsync(c,default)).Accepted);
 }
 [Fact] public async Task Secret_read_failure_is_sanitized_and_not_replaced_by_an_environment_secret()
 {
  var c=Context();c.Request.Headers[Options().HeaderName]=new string('K',64);
  var auth=new ProtectedToledoCallbackAdmission(Options(),_=>throw new IOException("SENSITIVE_TEST_ONLY"));
  var result=await auth.AuthorizeAsync(c,default);Assert.False(result.Accepted);Assert.Equal(503,result.HttpStatus);Assert.DoesNotContain("SENSITIVE",result.Code);
 }
 [Theory] [InlineData(5432,"fugates_acm_hml_app")] [InlineData(5434,"fugates_acm_hml_app")] [InlineData(5433,"fugates_dev_app")] [InlineData(5433,"fugates_sap_inbound_app")]
 public void Wrong_source_port_or_role_is_rejected_without_connection(int port,string role)=>Assert.Throws<InvalidOperationException>(()=>
  ToledoCallbackComposition.ValidateDedicatedBinding($"Host=127.0.0.1;Port={port};Database=fugates_dev;Username={role};Search Path=fugates;Passfile=/synthetic/pgpass;Enlist=false"));
 [Fact] public void Toledo_keyed_source_does_not_replace_or_consume_legacy_source()
 {
  var services=new ServiceCollection();using var legacy=NpgsqlDataSource.Create("Host=127.0.0.1;Port=5432;Database=unused;Username=legacy");services.AddSingleton(legacy);
  var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ToledoInbound:Enabled"]="true",["ConnectionStrings:Toledo"]="Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_acm_hml_app;Search Path=fugates;Passfile=/synthetic/pgpass;Enlist=false"}).Build();
  config["Sap:BaseUrl"]="https://vhfufds4ci.sap.fugacouros.com.br:44300";config["Sap:SapClient"]="110";
  services.AddSingleton<FugaTES.Integracao.SAP.Autenticacao.IAutenticacaoSap,OfflineSupplierAuth>();
  ToledoCallbackComposition.Configure(services,config,"PostgreSql");using var sp=services.BuildServiceProvider();
  Assert.Same(legacy,sp.GetRequiredService<NpgsqlDataSource>());
  var dedicated=sp.GetRequiredKeyedService<NpgsqlDataSource>(ToledoCallbackComposition.DataSourceKey);Assert.NotSame(legacy,dedicated);
  Assert.Equal(5433,new NpgsqlConnectionStringBuilder(dedicated.ConnectionString).Port);
  Assert.DoesNotContain(services,d=>d.ServiceType==typeof(Microsoft.Extensions.Hosting.IHostedService));
 }
 [Fact] public async Task Invalid_unquoted_leading_zero_is_not_silently_repaired_in_production()
 {
  var invalid=ToledoCallbackTests.ValidJson.Replace("\"IdPesagem\":\"0020318\"","\"IdPesagem\":0020318");
  using var services=new ServiceCollection().BuildServiceProvider();var response=await ToledoCallbackTests.Post(invalid,services);Assert.Equal(400,response.Status);
 }
 sealed class OfflineSupplierAuth:FugaTES.Integracao.SAP.Autenticacao.IAutenticacaoSap
 {public Task AplicarAsync(HttpRequestMessage r,CancellationToken ct)=>Task.CompletedTask;}
 internal static ToledoCallbackAdmissionOptions Options()=>new(){SourceIdentity="SYNTHETIC_TOLEDO_SOURCE",HeaderName="X-Test-Toledo",SecretFile=Path.Combine(Path.GetTempPath(),"SYNTHETIC_NOT_AN_EXISTING_SECRET"),AllowedPeers=[IPAddress.Loopback]};
 internal static DefaultHttpContext Context(){var c=new DefaultHttpContext();c.Request.Scheme="https";c.Connection.RemoteIpAddress=IPAddress.Loopback;c.Connection.LocalIpAddress=IPAddress.Loopback;return c;}
}
