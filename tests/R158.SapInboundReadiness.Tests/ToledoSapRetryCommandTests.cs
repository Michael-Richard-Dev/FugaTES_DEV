using FugaTES.Aplicacao.Modelos.Mm;
using System.Net;
using System.Net.Http.Json;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Infraestrutura.ManualRetry;
namespace FugaTES.Testes;
public sealed class ToledoSapRetryCommandTests
{
 const string Sid="S-1-5-21-111-222-333-1001";
 static readonly Guid Receipt=Guid.NewGuid(),Integration=Guid.NewGuid();
 static ToledoSapRetryApproval Approval()=>new(Receipt,Integration,Sid,new('B',64),"OFFLINE_NOMINAL_APPROVAL",DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddMinutes(5));
 static DevolutivaToledoSapSnapshot Snapshot()=>new(Receipt,Integration,"FALHA_DEFINITIVA","FalhaAntesDoPost",503,1,false,false,true,false,true,new('A',64),new('B',64),DateTimeOffset.UtcNow);
 static ComandoReenvioToledoSap Command()=>new(Guid.NewGuid(),Receipt,Integration,new('B',64),"Offline known pre-POST failure recovery",true);
 [Theory][InlineData(false,Sid)][InlineData(true,"S-1-5-21-999-888-777-1001")]
 public async Task Missing_authentication_or_wrong_operator_never_admits_a_command(bool authenticated,string sid)
 {var store=new Store();var service=new ReenvioToledoSapService(store,new Approvals(),TimeProvider.System);Assert.Equal("BLOCKED",(await service.SolicitarAsync(Command(),new(authenticated,sid),default)).Estado);Assert.Equal(0,store.Admissions);}
 [Fact] public async Task Missing_nominal_approval_and_closed_window_block_before_store_admission()
 {foreach(var approval in new ToledoSapRetryApproval?[]{null,Approval() with{FimUtc=DateTimeOffset.UtcNow.AddSeconds(-1)}}){var store=new Store();var service=new ReenvioToledoSapService(store,new Approvals{Value=approval},TimeProvider.System);Assert.Equal("BLOCKED",(await service.SolicitarAsync(Command(),new(true,Sid),default)).Estado);Assert.Equal(0,store.Admissions);}}
 [Theory][InlineData("revision")][InlineData("receipt")][InlineData("integration")][InlineData("confirmation")][InlineData("reason")]
 public async Task Changed_target_stale_revision_or_missing_confirmation_is_blocked(string defect)
 {var cmd=Command();cmd=defect switch{"revision"=>cmd with{Revisao=new('C',64)},"receipt"=>cmd with{ReciboId=Guid.NewGuid()},"integration"=>cmd with{IntegracaoId=Guid.NewGuid()},"confirmation"=>cmd with{Confirmado=false},_=>cmd with{Justificativa=""}};var store=new Store();var service=new ReenvioToledoSapService(store,new Approvals(),TimeProvider.System);Assert.Equal("BLOCKED",(await service.SolicitarAsync(cmd,new(true,Sid),default)).Estado);Assert.Equal(0,store.Admissions);}
 [Fact] public async Task Same_target_and_revision_use_only_authenticated_durable_command_store()
 {var store=new Store();var service=new ReenvioToledoSapService(store,new Approvals(),TimeProvider.System);var preview=await service.PrepararAsync(Receipt,new(true,Sid),default);Assert.NotNull(preview);Assert.True(preview.Decisao.Permitido);var cmd=Command();var result=await service.SolicitarAsync(cmd,new(true,Sid),default);Assert.Equal("QUEUED",result.Estado);Assert.Equal(cmd,store.Command);Assert.Equal(Sid,store.Actor!.Sid);}
 [Fact] public async Task Persistence_failure_is_not_reported_as_success()
 {var service=new ReenvioToledoSapService(new Store{Fail=true},new Approvals(),TimeProvider.System);await Assert.ThrowsAsync<InvalidOperationException>(()=>service.SolicitarAsync(Command(),new(true,Sid),default));}
 [Fact] public async Task Existing_Bearer_client_routes_SAP_retry_to_its_separate_command_path_only()
 {var wire=new Wire();using var client=new AuthenticatedAcmRetryCommandClient(new(new Uri("http://127.0.0.1:18764"),true,"OFFLINE_APPROVAL",Sid,DateTimeOffset.UtcNow.AddMinutes(5)),new('F',64),TimeProvider.System,wire);var preview=await client.PrepararAsync(Receipt,default);var cmd=Command();var result=await client.SolicitarAsync(cmd,default);Assert.Equal("QUEUED",result.Estado);Assert.Equal(new[]{"GET","POST"},wire.Methods);Assert.All(wire.Paths,p=>Assert.StartsWith("/api/v1/toledo/sap/manual-retry/",p));Assert.Equal(2,wire.Authenticated);}
 [Fact] public async Task Unknown_command_HTTP_is_not_retried()
 {var wire=new Wire{Unknown=true};using var client=new AuthenticatedAcmRetryCommandClient(new(new Uri("http://127.0.0.1:18764"),true,"OFFLINE_APPROVAL",Sid,DateTimeOffset.UtcNow.AddMinutes(5)),new('F',64),TimeProvider.System,wire);Assert.Equal("UNKNOWN",(await client.SolicitarAsync(Command(),default)).Estado);Assert.Single(wire.Methods);}
 sealed class Approvals:IToledoSapRetryApprovals{public ToledoSapRetryApproval? Value=Approval();public Task<ToledoSapRetryApproval?> ObterAsync(Guid id,AcmRetryActor actor,CancellationToken ct)=>Task.FromResult(Value);}
 sealed class Store:IToledoSapRetryStore
 {public int Admissions;public bool Fail;public ComandoReenvioToledoSap? Command;public AcmRetryActor? Actor;
 public Task<DevolutivaToledoSapSnapshot?> LerAsync(Guid id,CancellationToken ct)=>Task.FromResult<DevolutivaToledoSapSnapshot?>(Snapshot());
 public Task<ReciboComandoToledoSap> AdmitirAsync(ComandoReenvioToledoSap c,AcmRetryActor actor,ToledoSapRetryApproval a,CancellationToken ct){if(Fail)throw new InvalidOperationException("OFFLINE_STORE_FAILURE");Admissions++;Command=c;Actor=actor;return Task.FromResult(new ReciboComandoToledoSap(c.ComandoId,c.ReciboId,"QUEUED"));}}
 sealed class Wire:HttpMessageHandler
 {public List<string> Methods=[],Paths=[];public int Authenticated;public bool Unknown;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Methods.Add(r.Method.Method);Paths.Add(r.RequestUri!.AbsolutePath);Assert.Equal("127.0.0.1",r.RequestUri.Host);Assert.NotNull(r.Headers.Authorization);Authenticated++;if(r.Method==HttpMethod.Get)return new(HttpStatusCode.OK){Content=JsonContent.Create(new PreviaReenvioToledoSap(Snapshot(),new(true,"OFFLINE_APPROVED")))};if(Unknown)return new(HttpStatusCode.ServiceUnavailable);var cmd=await r.Content!.ReadFromJsonAsync<ComandoReenvioToledoSap>(ct);return new(HttpStatusCode.Accepted){Content=JsonContent.Create(new ReciboComandoToledoSap(cmd!.ComandoId,cmd.ReciboId,"QUEUED"))};}}
}
