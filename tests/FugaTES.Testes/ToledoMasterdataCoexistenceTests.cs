using System.Net;
using System.Text;
using FugaTES.Callback.Http;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
namespace FugaTES.Testes;

public sealed class ToledoMasterdataCoexistenceTests
{
 [Fact] public async Task One_existing_route_serves_full_Toledo_and_six_field_R41_with_Masterdata_retained_without_workers()
 {
  var variable="FUGATES_TEST_MASTERDATA_"+Guid.NewGuid().ToString("N");Environment.SetEnvironmentVariable(variable,new string('M',64));
  var master=new MasterStore();var toledo=new ToledoStore();var legacy=new LegacyStore();
  var builder=WebApplication.CreateBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");builder.Logging.ClearProviders();
  builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{
    ["MasterdataCallback:Enabled"]="true",["MasterdataCallback:StoreDirectory"]=Path.GetTempPath(),
    ["MasterdataCallback:AuthenticationHeaderName"]="X-Test-Masterdata",["MasterdataCallback:SharedSecretEnvironmentVariable"]=variable,
    ["MasterdataCallback:AllowedRemoteAddresses:0"]="127.0.0.1",["MasterdataCallback:AllowLoopbackHttp"]="true"});
  builder.Services.AddSingleton<IRegistroPesagemToledoStore>(toledo);builder.Services.AddScoped<RegistrarPesagemToledoCasoDeUso>();
  builder.Services.AddSingleton<IToledoCallbackAdmission,OfflineToledoAdmission>();builder.Services.AddSingleton<IRegistroEventoGuardianStore>(legacy);builder.Services.AddScoped<ProcessarEventoGuardianCasoDeUso>();
  await using var app=builder.Build();
  // Same map composition as the coordinated Program patch; use the existing Masterdata store boundary as a test double.
  MasterdataCallbackEndpoint.Map(app,MasterdataCallbackOptions.Parse(app.Configuration),master);
  GuardianCallbackEndpoint.MapDual(app,GuardianCallbackEndpoint.Route);
  await app.StartAsync();
  try
  {
   var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
   using var client=new HttpClient(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}){BaseAddress=new Uri(address)};
   using var t=await client.PostAsync(GuardianCallbackEndpoint.Route,new StringContent(ToledoCallbackTests.ValidJson,Encoding.UTF8,"application/json"));Assert.Equal(HttpStatusCode.OK,t.StatusCode);
   using var old=await client.PostAsync(GuardianCallbackEndpoint.Route,new StringContent("""{"ID":98765,"Sequencial":12345,"EstadoCodigo":5,"PesoBruto":"40000.0","Tara":"20000.0","PesoLiquido":"20000.00"}""",Encoding.UTF8,"application/json"));Assert.Equal(HttpStatusCode.OK,old.StatusCode);Assert.Equal("OK",await old.Content.ReadAsStringAsync());
   using var m=new HttpRequestMessage(HttpMethod.Post,MasterdataCallbackEndpoint.Route){Content=new StringContent("{\"synthetic\":true}",Encoding.UTF8,"application/json")};m.Headers.Add("X-Test-Masterdata",new string('M',64));
   using var reply=await client.SendAsync(m);Assert.Equal(HttpStatusCode.OK,reply.StatusCode);Assert.Equal(1,master.Count);Assert.Equal(1,toledo.Count);Assert.Equal(1,legacy.Count);
   Assert.DoesNotContain(app.Services.GetServices<IHostedService>(),s=>s.GetType().Namespace?.StartsWith("FugaTES")==true);
  }
  finally{await app.StopAsync();Environment.SetEnvironmentVariable(variable,null);}
 }
 [Fact] public void Coordinated_map_contains_exactly_the_existing_Masterdata_and_callback_routes()
 {
  var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();using var app=builder.Build();
  UnifiedCallbackRoutes.Map(app,new(true,GuardianCallbackEndpoint.Route,"InMemory"));
  var endpoints=((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources.SelectMany(s=>s.Endpoints).OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().ToArray();
  var posts=endpoints.Where(e=>e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Contains("POST")).Select(e=>e.RoutePattern.RawText).ToArray();
  Assert.Equal(2,posts.Length);Assert.Contains(MasterdataCallbackEndpoint.Route,posts);Assert.Contains(GuardianCallbackEndpoint.Route,posts);
  var probes=endpoints.Where(e=>!e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Contains("POST")).ToArray();Assert.Equal(2,probes.Length);
  Assert.All(probes,e=>Assert.Equal(new[]{"GET","HEAD"},e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods));
 }
 sealed class MasterStore:IMasterdataCallbackReceiptStore{public int Count;public Task PersistAsync(Guid id,DateTimeOffset when,ReadOnlyMemory<byte> body,CancellationToken ct){Count++;return Task.CompletedTask;}}
 sealed class ToledoStore:IRegistroPesagemToledoStore{public int Count;public Task<ReciboPesagemToledo> RegistrarAsync(PesagemToledoRecebida doc,string fingerprint,CancellationToken ct){Count++;return Task.FromResult(new ReciboPesagemToledo(Guid.NewGuid(),DateTimeOffset.UtcNow,false));}}
 sealed class LegacyStore:IRegistroEventoGuardianStore{public int Count;public Task<StatusRegistroEventoGuardian> RegistrarAsync(EventoGuardianRecebimento e,string f,CancellationToken ct){Count++;return Task.FromResult(StatusRegistroEventoGuardian.Novo);}}
}
