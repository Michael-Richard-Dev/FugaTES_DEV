using System.Net;
using System.Text;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Callback.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace FugaTES.Testes;

public sealed partial class ToledoSapCallbackAcknowledgementTests
{
 [Theory][InlineData(false)][InlineData(true)]
 public async Task SapConfirmed_mode_never_acknowledges_receipt_only_or_missing_observer(bool admitted)
 {
  var builder=DevUnifiedReadApi.CreateReadOnlyHostBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");
  builder.Configuration["ToledoSapReturn:GuardianAckMode"]="SapConfirmed";
  var store=new Store(admitted);
  builder.Services.AddSingleton<IRegistroPesagemToledoStore>(store);
  builder.Services.AddScoped<RegistrarPesagemToledoCasoDeUso>();
  builder.Services.AddSingleton<IToledoCallbackAdmission,ToledoDeliveryBpAdmissionTests.Admission>();
  await using var app=builder.Build();GuardianCallbackEndpoint.MapDual(app,"/callback");await app.StartAsync();
  using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
  var response=await http.PostAsync("/callback",new StringContent(ToledoDeliveryBpAdmissionTests.Payload(""),Encoding.UTF8,"application/json"));
  Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
  Assert.DoesNotContain("RECEIVED_SAP_QUEUED",await response.Content.ReadAsStringAsync());
  Assert.Equal(1,store.Commits);await app.StopAsync();
 }
 internal sealed class Store(bool admitted):IRegistroPesagemToledoStore
 {
  public readonly Guid Receipt=Guid.NewGuid(),Integration=Guid.NewGuid();public int Commits;public string OriginalHash="";
  public Task? CommitGate;public bool FailCommit;public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
  public Task<bool> JaRecebidoAsync(PesagemToledoRecebida d,string h,CancellationToken ct)=>Task.FromResult(false);
  public async Task<ReciboPesagemToledo> RegistrarAsync(PesagemToledoRecebida d,string h,CancellationToken ct)
  {OriginalHash=h;Entered.TrySetResult();if(CommitGate is not null)await CommitGate.WaitAsync(ct);if(FailCommit)throw new PesagemToledoStorageException();var n=Interlocked.Increment(ref Commits);return new(Receipt,DateTimeOffset.UtcNow,n>1){SapIntegracaoId=admitted?Integration:null};}
 }
}
