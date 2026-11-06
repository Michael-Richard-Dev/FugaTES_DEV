using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Callback.Http;
using FugaTES.Persistencia.Consultas.UnifiedDev;
using FugaTES.WinForms.Monitor;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using R165AL.Desktop;
namespace FugaTES.Testes;
public sealed class MmReadFailureIsolationTests
{
 static DevDatabaseIdentity Identity(int port=5433)=>new("18/fugatesacmdev",port,"fugates_dev","fugates_dev_read_app",true,DateTimeOffset.UtcNow);
 [Theory][InlineData("MM_VM_OBSERVATION_SCOPE_REJECTED")][InlineData("MM_VM_OBSERVATION_DOCUMENT_REJECTED")][InlineData("MM_VM_OBSERVATION_UNAVAILABLE")]
 public async Task Scoped_MM_failure_preserves_previously_verified_database_connection(string code)
 {
  using var client=Client(new Transport(_=>new(HttpStatusCode.ServiceUnavailable){Content=JsonContent.Create(new{scope="mm",code,identity=Identity()})}));
  await client.ReadContractsAsync("3011",default);var before=client.Connection;var events=0;client.ConnectionChanged+=()=>events++;
  var error=await Assert.ThrowsAsync<PhysicalConnectionException>(()=>client.ReadMmAsync(default));
  Assert.Equal(code,error.Code);Assert.Equal(before,client.Connection);Assert.True(client.Connection.Connected);Assert.Equal(0,events);
 }
 [Theory][InlineData(401,5433)][InlineData(403,5433)][InlineData(503,5434)][InlineData(500,5433)]
 public async Task Auth_wrong_identity_or_unscoped_server_failure_still_invalidates_connection(int status,int port)
 {
  using var client=Client(new Transport(_=>new((HttpStatusCode)status){Content=JsonContent.Create(new{scope="mm",code="MM_VM_OBSERVATION_DOCUMENT_REJECTED",identity=Identity(port)})}));
  await client.ReadContractsAsync("3011",default);await Assert.ThrowsAnyAsync<Exception>(()=>client.ReadMmAsync(default));Assert.False(client.Connection.Connected);
 }
 [Fact] public async Task Invalid_MM_payload_does_not_become_a_global_database_outage()
 {
  using var client=Client(new Transport(_=>new(HttpStatusCode.OK){Content=JsonContent.Create(new DevReadEnvelope<MmRemoteReadback>(Identity(),new(new(){RuntimeMode="VM",Host="untrusted-host"},"VM_SNAPSHOT_NOT_JOURNAL",DateTimeOffset.UtcNow,true)))}));
  await client.ReadContractsAsync("3011",default);
  var error=await Assert.ThrowsAsync<PhysicalConnectionException>(()=>client.ReadMmAsync(default));
  Assert.Equal("MM_VM_OBSERVATION_DOCUMENT_REJECTED",error.Code);Assert.True(client.Connection.Connected);
 }
 [Fact] public void Scope_failure_message_identifies_MM_instead_of_generic_backend_failure()
 {
  var message=new PhysicalReadConnection().Failure("MM_VM_OBSERVATION_SCOPE_REJECTED").Message;
  Assert.Contains("MM",message);Assert.DoesNotContain("Backend privado indisponível",message);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public async Task API_returns_only_safe_MM_failure_code_after_validating_database_identity(bool malformedJson)
 {
  var builder=DevUnifiedReadApi.CreateReadOnlyHostBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");
  await using var app=builder.Build();DevUnifiedReadApi.Map(app,new Store(),new string('A',64),new BrokenSource(malformedJson));await app.StartAsync();
  using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
  http.DefaultRequestHeaders.Add("X-R165AL-Read-Key",new string('A',64));var response=await http.GetAsync("/dev/read/mm");
  Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);var body=await response.Content.ReadAsStringAsync();using var doc=JsonDocument.Parse(body);
  Assert.Equal("mm",doc.RootElement.GetProperty("scope").GetString());
  Assert.Equal(malformedJson?"MM_VM_OBSERVATION_DOCUMENT_REJECTED":"MM_VM_OBSERVATION_UNAVAILABLE",doc.RootElement.GetProperty("code").GetString());
  Assert.Equal(5433,doc.RootElement.GetProperty("identity").GetProperty("port").GetInt32());Assert.DoesNotContain("DO_NOT_EXPORT",body);await app.StopAsync();
 }
 [Theory][InlineData("missing-identity")][InlineData("missing-scope")][InlineData("unknown-code")]
 public async Task Incomplete_or_unrecognized_failure_cannot_preserve_global_health(string mode)
 {
  object body=mode switch{
   "missing-identity"=>new{scope="mm",code="MM_VM_OBSERVATION_DOCUMENT_REJECTED"},
   "missing-scope"=>new{code="MM_VM_OBSERVATION_DOCUMENT_REJECTED",identity=Identity()},
   _=>new{scope="mm",code="UNRECOGNIZED",identity=Identity()}};
  using var client=Client(new Transport(_=>new(HttpStatusCode.ServiceUnavailable){Content=JsonContent.Create(body)}));
  await client.ReadContractsAsync("3011",default);await Assert.ThrowsAnyAsync<Exception>(()=>client.ReadMmAsync(default));Assert.False(client.Connection.Connected);
 }
 [Fact] public void MM_screen_reports_snapshot_problem_without_accusing_the_tunnel()
 {
  ToledoSapJsonButtonTests.OnSta(()=>{
   using var form=new FugaTES.WinForms.FrmPedidosCompraMm(_=>Task.FromException<MmRemoteReadback>(new PhysicalConnectionException("MM_VM_OBSERVATION_DOCUMENT_REJECTED",resourceOnly:true)));
   form.Show();Application.DoEvents();
   Assert.Contains(ToledoSapJsonButtonTests.Children(form).OfType<Label>(),x=>x.Text.Contains("snapshot incompatível")&&!x.Text.Contains("confira o túnel"));
   form.Close();
  });
 }
 [Theory][InlineData("null")][InlineData("{\"snapshot\":{\"intervalSeconds\":\"bad\"}}")]
 public async Task Schema_error_in_MM_data_after_valid_identity_does_not_disconnect_other_views(string data)
 {
  var json="{\"identity\":"+JsonSerializer.Serialize(Identity(),new JsonSerializerOptions(JsonSerializerDefaults.Web))+",\"data\":"+data+"}";
  using var client=Client(new Transport(_=>new(HttpStatusCode.OK){Content=new StringContent(json,System.Text.Encoding.UTF8,"application/json")}));
  await client.ReadContractsAsync("3011",default);
  var error=await Assert.ThrowsAsync<PhysicalConnectionException>(()=>client.ReadMmAsync(default));
  Assert.Equal("MM_VM_OBSERVATION_DOCUMENT_REJECTED",error.Code);Assert.True(client.Connection.Connected);
 }
 [Fact] public async Task Network_failure_is_still_a_global_connection_failure()
 {
  using var client=Client(new Transport(_=>throw new HttpRequestException("offline")));
  await client.ReadContractsAsync("3011",default);await Assert.ThrowsAsync<HttpRequestException>(()=>client.ReadMmAsync(default));Assert.False(client.Connection.Connected);
 }
 static PhysicalMonitorClient Client(HttpMessageHandler handler)=>new(new("http://127.0.0.1:18761/"),new string('A',64),R165AL.PilotPlan.Create(),DevReadScope.PilotId,handler,unified:true);
 sealed class Transport(Func<HttpRequestMessage,HttpResponseMessage> mm):HttpMessageHandler
 {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(request.RequestUri!.AbsolutePath=="/dev/read/contracts"?new(HttpStatusCode.OK){Content=JsonContent.Create(new DevReadEnvelope<IReadOnlyList<ContratoAcmLeitura>>(Identity(),[]))}:mm(request));
 }
 sealed class BrokenSource(bool malformed):IMmSnapshotReadSource
 {public Task<MmRemoteReadback> ReadAsync(CancellationToken ct)=>Task.FromException<MmRemoteReadback>(malformed?new JsonException("DO_NOT_EXPORT"):new IOException("DO_NOT_EXPORT"));}
 sealed class Store:IDevUnifiedReadStore
 {
  public Task<DevDatabaseIdentity> IdentityAsync(CancellationToken ct)=>Task.FromResult(Identity());
  public Task<object> ReadAsync(string r,string? s,CancellationToken ct)=>throw new NotSupportedException();
 }
}
