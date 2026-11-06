using Microsoft.AspNetCore.Builder;
using System.Net;
using System.Net.Http.Json;
using FugaTES.Aplicacao.Contratos.Mm;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
using FugaTES.Callback.Http;
using FugaTES.Callback.Http.ManualRetry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
namespace FugaTES.Testes;
public sealed class MmRetryEndpointTests
{
 [Fact] public async Task Shared_authentication_is_required_and_operator_is_derived_from_verified_claims()
 {
  var db=new MmRetryExecutionTests.Memory();var builder=DevUnifiedReadApi.CreateReadOnlyHostBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");
  AcmCommandAuthenticationHandler.Configure(builder.Services,new(new string('A',64),MmRetryExecutionTests.Sid));
  builder.Services.AddSingleton(new MmRetryService(db,MmRetryExecutionTests.Snapshot,new MmRetryExecutionTests.Gate(),MmRetryExecutionTests.Options(),TimeProvider.System));
  await using var app=builder.Build();app.UseAuthentication();app.UseAuthorization();MmRetryEndpoint.MapCandidate(app);await app.StartAsync();
  using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
  var route="/api/v1/mm/manual-retry/"+db.Row.Pedido+"/"+db.Row.Item;
  Assert.Equal(HttpStatusCode.Unauthorized,(await http.GetAsync(route+"/preview")).StatusCode);Assert.Empty(db.Commands);
  http.DefaultRequestHeaders.Authorization=new("Bearer",new string('B',64));Assert.Equal(HttpStatusCode.Unauthorized,(await http.GetAsync(route+"/preview")).StatusCode);
  http.DefaultRequestHeaders.Authorization=new("Bearer",new string('A',64));var p=await http.GetFromJsonAsync<MmRetryPreview>(route+"/preview");Assert.True(p!.Decision.Allowed);Assert.Equal(MmRetryExecutionTests.Sid,p.OperatorSid);
  var forged=await http.PostAsJsonAsync(route,new{commandId=Guid.NewGuid(),pedido=p.Pedido,item=p.Item,snapshotDigest=p.SnapshotDigest,justification="Cadastros conferidos",operatorSid="S-1-5-21-1-2-3-999"});Assert.Equal(HttpStatusCode.BadRequest,forged.StatusCode);Assert.Empty(db.Commands);
  var id=Guid.NewGuid();var receipt=await http.PostAsJsonAsync(route,new MmRetryRequest(id,p.Pedido,p.Item,p.SnapshotDigest,"Cadastros conferidos"));Assert.Equal(HttpStatusCode.Accepted,receipt.StatusCode);
  var command=Assert.Single(db.Commands.Values);Assert.Equal(MmRetryExecutionTests.Sid,command.OperatorSid);Assert.Null(command.DispatchStartedUtc);Assert.Equal("PENDENTE",command.State);
  var status=await http.GetFromJsonAsync<MmRetryReceipt>("/api/v1/mm/manual-retry/commands/"+id);Assert.Equal(id,status!.CommandId);Assert.Equal("QUEUED",status.State);await app.StopAsync();
 }
}
