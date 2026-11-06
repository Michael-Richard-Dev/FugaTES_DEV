using System.Net.Http.Json;
using FugaTES.Persistencia.Consultas.UnifiedDev;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
using FugaTES.Callback.Http;
using FugaTES.Infraestrutura.Mm;
using Registry=R158.SapInboundReadiness.Tests.MmGuardianDispatchTests.Registry;
using Fixture=R158.SapInboundReadiness.Tests.MmGuardianProtocolTests;
namespace FugaTES.Testes;
public sealed class MmReviewRegressionTests
{
 [Fact] public async Task Stop_during_pending_HTTP_success_drains_instead_of_aborting_response()
 {
  var db=new Registry();var api=new Pending();using var stop=new CancellationTokenSource();
  var work=new EnvioAutomaticoMm(db,api,"https://example.invalid/mm").ProcessarAsync([Fixture.Linha()],stop.Token);
  await api.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));stop.Cancel();
  try{await Task.Delay(50);Assert.False(work.IsCompleted,"An admitted POST must finish under its own bounded drain deadline");}
  finally{api.Release.TrySetResult();try{await work;}catch(OperationCanceledException){}}
  Assert.Equal(EstadoProtocoloMm.Criado,Assert.Single(db.Rows.Values).Estado);Assert.Equal(1,api.Calls);
 }
 sealed class Pending:IEnviarProtocoloMmGateway
 {
  public int Calls;public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
  public async Task<ResultadoProtocoloMm> EnviarAsync(byte[] data,CancellationToken ct){Calls++;Entered.SetResult();await Release.Task.WaitAsync(ct);return new(EstadoProtocoloMm.Criado,200,42,"ok");}
 }
 [Theory][InlineData("sap-secret")][InlineData("guardian-secret")][InlineData("old-item")][InlineData("future-time")][InlineData("bad-hash")]
 public async Task Malicious_inner_snapshot_is_rejected_before_private_read_export(string attack)
 {
  var dir=Path.Combine(Path.GetTempPath(),"mm-untrusted-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
  try
  {
   var row=Fixture.Linha() with{DataCriacao=new DateOnly(2026,11,1)};
   var json=MapeadorProtocoloMm.Serializar(MapeadorProtocoloMm.Montar(row,"https://example.invalid/mm"));
   var record=new RegistroProtocoloMm(Guid.NewGuid(),MapeadorProtocoloMm.ChaveNatural(row),MapeadorProtocoloMm.Codigo(row.Pedido),MapeadorProtocoloMm.Codigo(row.Item),row.Centro,json,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))),EstadoProtocoloMm.Criado,200,42,null,DateTimeOffset.UtcNow,new string('A',64));
   var snapshot=new MmPurchaseOrderSnapshot{RuntimeMode="VM",Host="vm-fugatesbio",State="MM_SAP_COMPLETE",LastSuccessUtc=DateTimeOffset.UtcNow,RegistryReadUtc=DateTimeOffset.UtcNow,GuardianCallback="https://example.invalid/mm",GuardianRecords=[record]};
   snapshot=attack switch
   {
    "sap-secret"=>snapshot with{Items=[row with{Json="{\"password\":\"DO_NOT_EXPORT\"}"}]},
    "guardian-secret"=>snapshot with{GuardianRecords=[record with{JsonEnviado="{\"password\":\"DO_NOT_EXPORT\"}"}]},
    "old-item"=>snapshot with{Items=[row with{DataCriacao=new DateOnly(2026,10,31)}]},
    "future-time"=>snapshot with{LastSuccessUtc=DateTimeOffset.UtcNow.AddMinutes(4)},
    _=>snapshot with{GuardianRecords=[record with{Sha256=new string('0',64)}]}
   };
   File.WriteAllText(Path.Combine(dir,"snapshot.json"),JsonSerializer.Serialize(snapshot,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
   var builder=DevUnifiedReadApi.CreateReadOnlyHostBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");
   await using var app=builder.Build();DevUnifiedReadApi.Map(app,new SafeIdentity(),new string('A',64),new MmSnapshotReadSource(dir));await app.StartAsync();
   using var client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
   client.DefaultRequestHeaders.Add("X-R165AL-Read-Key",new string('A',64));var response=await client.GetAsync("/dev/read/mm");
   Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.DoesNotContain("DO_NOT_EXPORT",await response.Content.ReadAsStringAsync());await app.StopAsync();
  }
  finally{Directory.Delete(dir,true);}
 }
 [Fact] public async Task Valid_documents_roundtrip_but_arbitrary_message_is_not_exported()
 {
  using var http=new HttpClient(new PedidosCompraMmTests.Transport(_=>PedidosCompraMmTests.Page(PedidosCompraMmTests.Header())));
  var row=Assert.Single((await PedidosCompraMmTests.Query(http).ConsultarAsync(default)).Itens);
  var json=MapeadorProtocoloMm.Serializar(MapeadorProtocoloMm.Montar(row,"https://example.invalid/mm"));
  var record=new RegistroProtocoloMm(Guid.NewGuid(),MapeadorProtocoloMm.ChaveNatural(row),MapeadorProtocoloMm.Codigo(row.Pedido),MapeadorProtocoloMm.Codigo(row.Item),row.Centro,json,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))),EstadoProtocoloMm.Criado,200,42,"DO_NOT_EXPORT secret",DateTimeOffset.UtcNow,new string('A',64));
  var dir=Path.Combine(Path.GetTempPath(),"mm-valid-"+Guid.NewGuid().ToString("N"));var store=new MmFileSnapshotStore(dir);
  try
  {
   store.Write(new(){RuntimeMode="VM",Host="vm-fugatesbio",State="MM_SAP_COMPLETE",LastSuccessUtc=DateTimeOffset.UtcNow,RegistryReadUtc=DateTimeOffset.UtcNow,GuardianCallback="https://example.invalid/mm",Items=[row],GuardianRecords=[record]});
   var result=await new MmSnapshotReadSource(dir).ReadAsync(default);Assert.True(result.Fresh);Assert.Equal(row.Json,Assert.Single(result.Snapshot.Items).Json);
   var returned=Assert.Single(result.Snapshot.GuardianRecords);Assert.Equal(json,returned.JsonEnviado);Assert.Equal(record.Sha256,returned.Sha256);Assert.DoesNotContain("DO_NOT_EXPORT",returned.Mensagem);
  }
  finally{Directory.Delete(dir,true);}
 }
 sealed class SafeIdentity:IDevUnifiedReadStore
 {
  public Task<DevDatabaseIdentity> IdentityAsync(CancellationToken ct)=>Task.FromResult(new DevDatabaseIdentity("18/fugatesacmdev",5433,"fugates_dev","fugates_dev_read_app",true,DateTimeOffset.UtcNow));
  public Task<object> ReadAsync(string r,string? s,CancellationToken ct)=>throw new NotSupportedException();
 }
 [Fact] public void Future_success_is_not_fresh_even_inside_old_five_minute_tolerance()
 {Assert.False(new MmPurchaseOrderSnapshot{State="MM_SAP_COMPLETE",LastSuccessUtc=DateTimeOffset.UtcNow.AddMinutes(4)}.IsFresh(DateTimeOffset.UtcNow));}
}
