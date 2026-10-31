using System.Net;
using System.Text.Json;
using FugaTES.Callback.Http;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
namespace FugaTES.Testes;
public sealed class ToledoGuardianProbeTests
{
 [Theory][InlineData("/api/guardian/set26/callback/")][InlineData("/api/guardian/set26/callback/recebimento")]
 public async Task Authorized_GET_is_an_explicit_probe_without_receipt_or_database_call(string path)
 {
  await using var scope=await Host();using var response=await scope.Http.GetAsync(path);
  Assert.Equal(HttpStatusCode.OK,response.StatusCode);using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
  Assert.Equal("RECEIVER_READY",json.RootElement.GetProperty("status").GetString());Assert.True(json.RootElement.GetProperty("probe").GetBoolean());
  Assert.False(json.RootElement.GetProperty("persisted").GetBoolean());Assert.False(json.RootElement.TryGetProperty("receiptId",out _));Assert.Equal(0,scope.Store.Calls);
 }
 [Theory][InlineData("/api/guardian/set26/callback/")][InlineData("/api/guardian/set26/callback/recebimento")]
 public async Task A_GET_body_or_query_is_never_acknowledged_as_a_weighing(string path)
 {
  await using var scope=await Host();using var body=await scope.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get,path){Content=new StringContent("{\"IdPesagem\":123}")});
  Assert.Equal(HttpStatusCode.BadRequest,body.StatusCode);
  using var query=await scope.Http.GetAsync(path+"?IdPesagem=123");Assert.Equal(HttpStatusCode.BadRequest,query.StatusCode);Assert.Equal(0,scope.Store.Calls);
 }
 [Fact] public async Task Disabled_or_rejected_Toledo_probes_do_not_expose_a_generic_health_bypass()
 {
  await using(var rejected=await Host(accepted:false)){Assert.Equal(HttpStatusCode.Forbidden,(await rejected.Http.GetAsync("/api/guardian/set26/callback/")).StatusCode);Assert.Equal(0,rejected.Store.Calls);}
  await using(var disabled=await Host(configured:false)){Assert.Equal(HttpStatusCode.NotFound,(await disabled.Http.GetAsync("/api/guardian/set26/callback/")).StatusCode);}
 }
 [Fact] public async Task HEAD_probe_is_non_business_and_returns_no_body()
 {
  await using var scope=await Host();using var response=await scope.Http.SendAsync(new HttpRequestMessage(HttpMethod.Head,"/api/guardian/set26/callback/"));
  Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Empty(await response.Content.ReadAsStringAsync());Assert.Equal(0,scope.Store.Calls);
 }
 [Fact] public async Task Base_POST_is_not_an_alias_that_can_falsely_acknowledge_a_receipt()
 {
  await using var scope=await Host();using var response=await scope.Http.PostAsync("/api/guardian/set26/callback/",new StringContent("{}"));
  Assert.Equal(HttpStatusCode.MethodNotAllowed,response.StatusCode);Assert.Equal(0,scope.Store.Calls);
 }
 static async Task<Scope> Host(bool accepted=true,bool configured=true)
 {
  var builder=DevUnifiedReadApi.CreateReadOnlyHostBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");var store=new NoWrite();
  if(configured){builder.Services.AddSingleton<IRegistroPesagemToledoStore>(store);builder.Services.AddSingleton<IToledoCallbackAdmission>(new Admission(accepted));}
  var app=builder.Build();GuardianCallbackEndpoint.MapDual(app,"/api/guardian/set26/callback/recebimento");await app.StartAsync();
  var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};return new(app,http,store);
 }
 sealed class Admission(bool accepted):IToledoCallbackAdmission{public Task<ToledoCallbackActor> AuthorizeAsync(Microsoft.AspNetCore.Http.HttpContext c,CancellationToken ct)=>Task.FromResult(new ToledoCallbackActor(accepted,"OFFLINE_PROBE",accepted?200:403,accepted?"OK":"DENIED"));}
 sealed class NoWrite:IRegistroPesagemToledoStore{public int Calls;public Task<ReciboPesagemToledo> RegistrarAsync(PesagemToledoRecebida doc,string fingerprint,CancellationToken ct){Calls++;throw new InvalidOperationException("PROBE_MUST_NOT_WRITE");}}
 sealed record Scope(WebApplication App,HttpClient Http,NoWrite Store):IAsyncDisposable{public async ValueTask DisposeAsync(){Http.Dispose();await App.StopAsync();await App.DisposeAsync();}}
}
