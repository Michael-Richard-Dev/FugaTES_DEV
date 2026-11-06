using System.Net;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Callback.Http;
using FugaTES.Persistencia.Recebimento;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;

namespace FugaTES.Testes;

public sealed partial class ToledoSapCallbackAcknowledgementTests
{
 static ToledoSapCallbackSnapshot Proof(Store store)
 {
  var original=ToledoSapCallbackOutcomeProofTests.Proof();
  return original with{Receipt=store.Receipt,Integration=store.Integration,OriginalHash=store.OriginalHash,
   IntegrationHash=ToledoSapWork.Fingerprint(store.Receipt,store.OriginalHash),Correlation=$"toledo-sap:{store.Receipt:D}"};
 }
 [Fact]public async Task Response_waits_for_both_receipt_commit_and_final_journal_commit()
 {
  var commit=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var store=new Store(true){CommitGate=commit.Task};
  bool finalized=false;var observed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  var reader=new ToledoSapCallbackOutcomeProofTests.Reader((_,_,_)=>{Assert.True(store.Commits>0);observed.TrySetResult();var p=Proof(store);return Task.FromResult<ToledoSapCallbackSnapshot?>(finalized?p:p with{State="EM_PROCESSAMENTO",Active=true,AttemptOutcome="EM_EXECUCAO"});});
  await using var host=await Host.Start(store,reader);var responseTask=host.Post();
  await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.False(responseTask.IsCompleted);Assert.Equal(0,reader.Calls);
  commit.SetResult();await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.False(responseTask.IsCompleted);
  finalized=true;using var response=await responseTask;
  Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal("OK",await response.Content.ReadAsStringAsync());
  Assert.Equal("text/plain",response.Content.Headers.ContentType!.MediaType);Assert.Equal("utf-8",response.Content.Headers.ContentType.CharSet);
  Assert.Equal(store.Receipt.ToString("D"),Assert.Single(response.Headers.GetValues("X-FugaTES-Receipt-Id")));
 }
 [Fact]public async Task Concurrent_duplicate_callbacks_observe_the_same_committed_integration()
 {
  var store=new Store(true);var reader=new ToledoSapCallbackOutcomeProofTests.Reader((r,i,_)=>{Assert.Equal(store.Receipt,r);Assert.Equal(store.Integration,i);return Task.FromResult<ToledoSapCallbackSnapshot?>(Proof(store));});
  await using var host=await Host.Start(store,reader);
  var responses=await Task.WhenAll(host.Post(),host.Post());
  foreach(var response in responses){using(response){Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal("OK",await response.Content.ReadAsStringAsync());}}
  Assert.Equal(2,store.Commits);Assert.Equal(2,reader.Calls); // Observation only: neither reader nor endpoint has a send gateway.
 }
 [Fact]public async Task HTTP201_without_business_proof_and_unknown_outcome_are_not_OK()
 {
  var store=new Store(true);var reader=new ToledoSapCallbackOutcomeProofTests.Reader((_,_,_)=>Task.FromResult<ToledoSapCallbackSnapshot?>(Proof(store) with{State="RESULTADO_INDETERMINADO"}));
  await using var host=await Host.Start(store,reader);using var response=await host.Post();
  Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
  Assert.Equal("INDETERMINATE",body.RootElement.GetProperty("status").GetString());Assert.True(body.RootElement.GetProperty("persisted").GetBoolean());
 }
 [Fact]public async Task SAP_validation_error_explains_failure_without_exposing_raw_SAP_response()
 {
  var store=new Store(true);var reader=new ToledoSapCallbackOutcomeProofTests.Reader((_,_,_)=>{
   var p=Proof(store);return Task.FromResult<ToledoSapCallbackSnapshot?>(p with{State="FALHA_DEFINITIVA",AttemptOutcome="FALHA",Audits=[p.Audits[0],p.Audits[1] with{Result=new(ResultadoToledoSapCodigo.RejeicaoHttp,400,true){DetailCode="SYNTHETIC_SECRET_NOT_FOR_ACK"}}]});});
  await using var host=await Host.Start(store,reader);using var response=await host.Post();var raw=await response.Content.ReadAsStringAsync();
  Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);Assert.DoesNotContain("SYNTHETIC_SECRET",raw);using var body=JsonDocument.Parse(raw);
  Assert.Equal(400,body.RootElement.GetProperty("sapHttpStatus").GetInt32());Assert.Contains("SAP",body.RootElement.GetProperty("message").GetString());
 }
 [Fact]public async Task Timeout_returns_pending_with_receipt_reference_without_false_success()
 {
  var store=new Store(true);var reader=new ToledoSapCallbackOutcomeProofTests.Reader((_,_,_)=>Task.FromResult<ToledoSapCallbackSnapshot?>(Proof(store) with{State="EM_PROCESSAMENTO",Active=true}));
  await using var host=await Host.Start(store,reader,new(TimeSpan.FromMilliseconds(100),TimeSpan.FromMilliseconds(5)));using var response=await host.Post();
  Assert.Equal(HttpStatusCode.GatewayTimeout,response.StatusCode);using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
  Assert.Equal("PENDING",body.RootElement.GetProperty("status").GetString());Assert.Equal(store.Receipt,body.RootElement.GetProperty("receiptId").GetGuid());Assert.Equal(1,store.Commits);
 }
 [Fact]public async Task Receipt_commit_failure_cannot_be_reported_as_OK_or_as_persisted()
 {
  var store=new Store(true){FailCommit=true};var reader=new ToledoSapCallbackOutcomeProofTests.Reader((_,_,_)=>throw new Exception("MUST_NOT_READ"));
  await using var host=await Host.Start(store,reader);using var response=await host.Post();
  Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.Equal(0,reader.Calls);Assert.Equal(0,store.Commits);
  Assert.Contains("TOLEDO_STORAGE_UNAVAILABLE",await response.Content.ReadAsStringAsync());
 }
 [Fact]public async Task Legacy_receipt_ACK_remains_JSON_outside_explicit_SapConfirmed_mode()
 {
  var store=new Store(true);await using var host=await Host.Start(store,null,mode:null);using var response=await host.Post();
  Assert.Equal(HttpStatusCode.OK,response.StatusCode);using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal("RECEIVED_SAP_QUEUED",body.RootElement.GetProperty("status").GetString());
 }
 [Theory][InlineData("SapConfirmed",false)][InlineData("TYPO",true)]
 public void Unknown_mode_or_disabled_worker_cannot_silently_fall_back_to_receipt_ACK(string mode,bool enabled)
 {
  var config=new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ToledoSapReturn:GuardianAckMode",mode},{"ToledoSapReturn:Enabled",enabled.ToString()}}).Build();
  Assert.Throws<InvalidOperationException>(()=>{_ = ToledoSapReturnComposition.Configure(new Microsoft.Extensions.DependencyInjection.ServiceCollection(),config);});
 }
 sealed class Host(WebApplication app,HttpClient http):IAsyncDisposable
 {
  public Task<HttpResponseMessage> Post()=>http.PostAsync("/callback",new StringContent(ToledoDeliveryBpAdmissionTests.Payload(""),Encoding.UTF8,"application/json"));
  public static async Task<Host> Start(Store store,IToledoSapCallbackOutcomeRead? reader,ToledoSapCallbackAckOptions? options=null,string? mode="SapConfirmed")
  {
   var b=DevUnifiedReadApi.CreateReadOnlyHostBuilder();b.WebHost.UseUrls("http://127.0.0.1:0");b.Configuration["ToledoSapReturn:GuardianAckMode"]=mode;
   b.Services.AddSingleton<IRegistroPesagemToledoStore>(store);b.Services.AddScoped<RegistrarPesagemToledoCasoDeUso>();b.Services.AddSingleton<IToledoCallbackAdmission,ToledoDeliveryBpAdmissionTests.Admission>();
   if(reader is not null){b.Services.AddSingleton(reader);b.Services.AddSingleton(options??new ToledoSapCallbackAckOptions(TimeSpan.FromSeconds(2),TimeSpan.FromMilliseconds(5)));b.Services.AddScoped<ToledoSapCallbackAcknowledgement>();}
   var app=b.Build();GuardianCallbackEndpoint.MapDual(app,"/callback");await app.StartAsync();
   var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()),Timeout=TimeSpan.FromSeconds(5)};
   return new(app,http);
  }
  public async ValueTask DisposeAsync(){http.Dispose();await app.StopAsync();await app.DisposeAsync();}
 }
}
