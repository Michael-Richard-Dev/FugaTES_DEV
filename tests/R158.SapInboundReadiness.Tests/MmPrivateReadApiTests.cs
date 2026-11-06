using System.Net;
using System.Net.Http.Json;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Callback.Http;
using FugaTES.Infraestrutura.Mm;
using FugaTES.Persistencia.Consultas.UnifiedDev;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
namespace FugaTES.Testes;
public sealed class MmPrivateReadApiTests
{
 [Fact] public async Task MM_route_requires_existing_key_is_GET_only_and_validates_physical_identity()
 {
  var dir=Path.Combine(Path.GetTempPath(),"mm-observation-"+Guid.NewGuid().ToString("N"));var file=new MmFileSnapshotStore(dir);
  try
  {
   file.Write(new(){RuntimeMode="VM",Host="vm-fugatesbio",State="MM_SAP_COMPLETE",LastSuccessUtc=DateTimeOffset.UtcNow,RegistryReadUtc=DateTimeOffset.UtcNow});
   var builder=DevUnifiedReadApi.CreateReadOnlyHostBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");
   await using var app=builder.Build();var database=new Store();DevUnifiedReadApi.Map(app,database,new string('A',64),new MmSnapshotReadSource(dir));await app.StartAsync();
   using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
   Assert.Equal(HttpStatusCode.Unauthorized,(await http.GetAsync("/dev/read/mm")).StatusCode);Assert.Equal(0,database.Calls);
   http.DefaultRequestHeaders.Add("X-R165AL-Read-Key",new string('A',64));
   var response=await http.GetAsync("/dev/read/mm");Assert.Equal(HttpStatusCode.OK,response.StatusCode);
   var value=await response.Content.ReadFromJsonAsync<DevReadEnvelope<MmRemoteReadback>>();Assert.True(value!.Identity.ReadOnly);Assert.True(value.Data.Fresh);Assert.Equal("VM_SNAPSHOT_NOT_JOURNAL",value.Data.Provenance);
   Assert.Equal(HttpStatusCode.MethodNotAllowed,(await http.PostAsync("/dev/read/mm",new StringContent("{}"))).StatusCode);
   database.Wrong=true;Assert.Equal(HttpStatusCode.ServiceUnavailable,(await http.GetAsync("/dev/read/mm")).StatusCode);await app.StopAsync();
  }
  finally{Directory.Delete(dir,true);}
 }
 [Fact] public async Task Stale_observation_is_explicit_not_a_false_live_SQL_result()
 {
  var dir=Path.Combine(Path.GetTempPath(),"mm-stale-"+Guid.NewGuid().ToString("N"));var file=new MmFileSnapshotStore(dir);
  try{file.Write(new(){RuntimeMode="VM",Host="vm-fugatesbio",State="MM_SAP_COMPLETE",LastSuccessUtc=DateTimeOffset.UtcNow.AddMinutes(-10)});Assert.False((await new MmSnapshotReadSource(dir).ReadAsync(default)).Fresh);}
  finally{Directory.Delete(dir,true);}
 }
 [Theory][InlineData("WINDOWS","vm-fugatesbio")][InlineData("VM","other-host")]
 public async Task Foreign_observation_is_rejected(string mode,string host)
 {
  var dir=Path.Combine(Path.GetTempPath(),"mm-scope-"+Guid.NewGuid().ToString("N"));var file=new MmFileSnapshotStore(dir);
  try{File.WriteAllText(Path.Combine(dir,"snapshot.json"),System.Text.Json.JsonSerializer.Serialize(new MmPurchaseOrderSnapshot{RuntimeMode=mode,Host=host},new System.Text.Json.JsonSerializerOptions{PropertyNamingPolicy=System.Text.Json.JsonNamingPolicy.CamelCase}));await Assert.ThrowsAsync<InvalidOperationException>(()=>new MmSnapshotReadSource(dir).ReadAsync(default));}
  finally{Directory.Delete(dir,true);}
 }
 [Fact] public async Task Missing_snapshot_does_not_create_directory_or_return_empty_success()
 {var dir=Path.Combine(Path.GetTempPath(),"mm-missing-"+Guid.NewGuid().ToString("N"));await Assert.ThrowsAsync<InvalidOperationException>(()=>new MmSnapshotReadSource(dir).ReadAsync(default));Assert.False(Directory.Exists(dir));}
 sealed class Store:IDevUnifiedReadStore
 {
  public int Calls;public bool Wrong;
  public Task<DevDatabaseIdentity> IdentityAsync(CancellationToken ct){Calls++;return Task.FromResult(new DevDatabaseIdentity("18/fugatesacmdev",Wrong?5434:5433,"fugates_dev","fugates_dev_read_app",true,DateTimeOffset.UtcNow));}
  public Task<object> ReadAsync(string r,string? s,CancellationToken ct)=>throw new NotSupportedException();
 }
}
