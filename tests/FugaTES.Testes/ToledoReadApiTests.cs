using System.Net;
using FugaTES.Callback.Http;
using FugaTES.Persistencia.Consultas.UnifiedDev;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
namespace FugaTES.Testes;
public sealed class ToledoReadApiTests
{
 [Theory][InlineData("toledo")][InlineData("toledo-json")]
 public async Task Toledo_resources_are_available_only_through_authenticated_GET(string resource)
 {
  var builder=DevUnifiedReadApi.CreateReadOnlyHostBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");
  await using var app=builder.Build();var store=new FakeStore();DevUnifiedReadApi.Map(app,store,new string('A',64));await app.StartAsync();
  using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
  Assert.Equal(HttpStatusCode.Unauthorized,(await http.GetAsync("/dev/read/"+resource)).StatusCode);Assert.Equal(0,store.Calls);
  http.DefaultRequestHeaders.Add("X-R165AL-Read-Key",new string('A',64));
  Assert.Equal(HttpStatusCode.OK,(await http.GetAsync("/dev/read/"+resource+"?selector=0000001155")).StatusCode);
  Assert.Equal(resource,store.Resource);Assert.Equal("0000001155",store.Selector);Assert.Equal(1,store.Calls);
  Assert.Equal(HttpStatusCode.MethodNotAllowed,(await http.PostAsync("/dev/read/"+resource,new StringContent("{}"))).StatusCode);
  Assert.Equal(1,store.Calls);await app.StopAsync();
 }
 sealed class FakeStore:IDevUnifiedReadStore
 {
  public int Calls;public string? Resource,Selector;
  public Task<DevDatabaseIdentity> IdentityAsync(CancellationToken ct)=>Task.FromResult(new DevDatabaseIdentity("18/fugatesacmdev",5433,"fugates_dev","fugates_acm_hml_app",true,DateTimeOffset.UtcNow));
  public Task<object> ReadAsync(string resource,string? selector,CancellationToken ct){Calls++;Resource=resource;Selector=selector;return Task.FromResult<object>(new{source="SYNTHETIC_OFFLINE_TEST",persisted=true});}
 }
}
