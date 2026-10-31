using System.Net;
using FugaTES.Aplicacao.Servicos.Recebimento;
using System.Text.Json.Nodes;
using FugaTES.Callback.Http;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
namespace FugaTES.Testes;
public sealed class ToledoDeliveryBpAdmissionTests
{
 internal static string Payload(string? code="90000733")
 {
  var root=JsonNode.Parse(ToledoSapOData201Tests.Payload)!;root["DataPesagem"]="10/30/2026 3:00:00 PM";var item=root["to_Pesagem"]![0]!;
  item["IdParceiroEntrega"]=code;item["ParceiroNome"]="SYNTHETIC ORIGINAL PARTNER";item["ParceiroEntregaNome"]="UNTRUSTED GUARDIAN NAME";
  item["PercentualDesdobro"]="100";item["PesoAntesDesconto"]="20000.0";item["PesoAposDesconto"]="20000.0";return root.ToJsonString();
 }
 [Fact] public async Task Explicit_BP_without_SAP_validation_capability_cannot_ACK_or_persist()
 {
  var builder=DevUnifiedReadApi.CreateReadOnlyHostBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");var store=new Store();
  builder.Services.AddSingleton<IRegistroPesagemToledoStore>(store);builder.Services.AddScoped<RegistrarPesagemToledoCasoDeUso>();builder.Services.AddSingleton<IToledoCallbackAdmission,Admission>();
  await using var app=builder.Build();GuardianCallbackEndpoint.MapDual(app,"/callback");await app.StartAsync();
  using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
  var response=await http.PostAsync("/callback",new StringContent(Payload(),System.Text.Encoding.UTF8,"application/json"));
  Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.Contains("TOLEDO_DELIVERY_BP_VALIDATION_UNAVAILABLE",await response.Content.ReadAsStringAsync());Assert.Equal(0,store.Writes);await app.StopAsync();
 }
 [Theory][InlineData(ToledoSupplierStatus.Found,200)][InlineData(ToledoSupplierStatus.NotFound,422)][InlineData(ToledoSupplierStatus.NotSupplier,422)][InlineData(ToledoSupplierStatus.Blocked,422)][InlineData(ToledoSupplierStatus.Unavailable,503)][InlineData(ToledoSupplierStatus.InvalidResponse,502)]
 public async Task Endpoint_returns_business_explanation_or_accepts_only_after_validation(ToledoSupplierStatus status,int expected)
 {
  var builder=DevUnifiedReadApi.CreateReadOnlyHostBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");var store=new Store();
  builder.Services.AddSingleton<IRegistroPesagemToledoStore>(store);builder.Services.AddScoped<RegistrarPesagemToledoCasoDeUso>();builder.Services.AddSingleton<IToledoCallbackAdmission,Admission>();
  builder.Services.AddSingleton<IToledoSupplierLookup>(new ToledoDeliveryPartnerTests.Supplier{Status=status});
  await using var app=builder.Build();GuardianCallbackEndpoint.MapDual(app,"/callback");await app.StartAsync();
  using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
  var response=await http.PostAsync("/callback",new StringContent(Payload(),System.Text.Encoding.UTF8,"application/json"));
  Assert.Equal(expected,(int)response.StatusCode);Assert.Equal(expected==200?1:0,store.Writes);
  if(expected!=200){var body=await response.Content.ReadAsStringAsync();Assert.Contains("IdParceiroEntrega",body);Assert.DoesNotContain("UNTRUSTED GUARDIAN NAME",body);using var parsed=System.Text.Json.JsonDocument.Parse(body);Assert.False(parsed.RootElement.GetProperty("persisted").GetBoolean());}
  await app.StopAsync();
 }
 [Fact] public async Task Previously_committed_identical_receipt_does_not_lookup_again_or_replace_frozen_proof()
 {
  var raw=Payload();using var j=System.Text.Json.JsonDocument.Parse(raw);var store=new Store{Existing=true};var lookup=new ToledoDeliveryPartnerTests.Supplier{Status=ToledoSupplierStatus.Unavailable};
  var document=ToledoPesagemCallbackDto.Deserializar(j.RootElement).Mapear() with{OrigemAutenticada="OFFLINE"};
  var receipt=await new RegistrarPesagemToledoCasoDeUso(store,lookup).ExecutarAsync(document,default);
  Assert.True(receipt.Idempotente);Assert.Equal(0,lookup.Calls);Assert.Null(store.Last!.DeliveryPartners);
 }
 [Fact] public async Task A_bad_second_item_prevents_the_whole_receipt_from_reaching_storage()
 {
  var node=JsonNode.Parse(Payload())!;var items=node["to_Pesagem"]!.AsArray();items[0]!["NrIndiceTotal"]=2;var second=items[0]!.DeepClone();second["Indice"]=2;second["IdParceiroEntrega"]="90000999";items.Add(second);
  using var j=System.Text.Json.JsonDocument.Parse(node.ToJsonString());var store=new Store();
  await Assert.ThrowsAsync<ToledoDeliveryPartnerException>(()=>new RegistrarPesagemToledoCasoDeUso(store,new SelectiveLookup()).ExecutarAsync(ToledoPesagemCallbackDto.Deserializar(j.RootElement).Mapear() with{OrigemAutenticada="OFFLINE"},default));Assert.Equal(0,store.Writes);
 }
 sealed class SelectiveLookup:IToledoSupplierLookup
 {public Task<ToledoSupplierResult> FindAsync(string code,CancellationToken ct)=>code=="90000999"?Task.FromResult(new ToledoSupplierResult(ToledoSupplierStatus.NotFound)):new ToledoDeliveryPartnerTests.Supplier().FindAsync(code,ct);}
 internal sealed class Store:IRegistroPesagemToledoStore
 {public int Writes;public bool Existing;public PesagemToledoRecebida? Last;
 public Task<bool> JaRecebidoAsync(PesagemToledoRecebida d,string h,CancellationToken ct)=>Task.FromResult(Existing);
 public Task<ReciboPesagemToledo> RegistrarAsync(PesagemToledoRecebida d,string h,CancellationToken ct){Last=d;Writes++;return Task.FromResult(new ReciboPesagemToledo(Guid.NewGuid(),DateTimeOffset.UtcNow,Existing));}}
 internal sealed class Admission:IToledoCallbackAdmission
 {public Task<ToledoCallbackActor> AuthorizeAsync(Microsoft.AspNetCore.Http.HttpContext c,CancellationToken t)=>Task.FromResult(new ToledoCallbackActor(true,"OFFLINE_ADMITTED",200,"OK"));}
}
