using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Aplicacao.Servicos.Recebimento;
namespace FugaTES.Testes;
public sealed class ToledoSapWindowAuthorizationTests
{
 const string Sid="S-1-5-21-111-222-333-1001";
 static ToledoSapOperatorWindow Window()=>new(Sid,"OFFLINE_PRIVATE_SOURCE","OFFLINE_OPERATOR_AUTHORIZATION",DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddMinutes(5));
 [Fact] public async Task Authenticated_operator_can_confirm_only_a_current_eligible_admitted_target_and_revision()
 {var store=new Store();var resolver=new ToledoSapWindowRetryApprovals(store,store,Window(),TimeProvider.System);var service=new ReenvioToledoSapService(store,resolver,TimeProvider.System);var preview=await service.PrepararAsync(store.Row.ReciboId,new(true,Sid),default);Assert.NotNull(preview);var s=preview.Snapshot;var cmd=new ComandoReenvioToledoSap(Guid.NewGuid(),s.ReciboId,s.IntegracaoId,s.Revisao,"Operator confirmed the recorded pre-POST failure",true);Assert.Equal("QUEUED",(await service.SolicitarAsync(cmd,new(true,Sid),default)).Estado);Assert.Equal(1,store.Commands);Assert.NotNull(store.LastApproval);Assert.Equal(s.ReciboId,store.LastApproval.ReciboId);Assert.Equal(s.Revisao,store.LastApproval.Revisao);}
 [Theory][InlineData("source")][InlineData("historical")][InlineData("completed")][InlineData("unknown")][InlineData("http400")][InlineData("synthetic")][InlineData("unauthenticated")][InlineData("other-operator")][InlineData("expired")]
 public async Task No_operator_window_can_authorize_ineligible_or_unproven_receipts(string defect)
 {var s=new Store();var w=Window();var actor=new AcmRetryActor(true,Sid);if(defect is "source" or "historical")s.Proven=false;if(defect=="completed")s.Row=s.Row with{Estado="CONCLUIDA"};if(defect=="unknown")s.Row=s.Row with{ResultadoIndeterminado=true};if(defect=="http400")s.Row=s.Row with{Codigo="RejeicaoHttp",HttpStatus=400};if(defect=="synthetic")s.Row=s.Row with{Sintetico=true};if(defect=="unauthenticated")actor=actor with{Authenticated=false};if(defect=="other-operator")actor=actor with{Sid="S-1-5-21-9-8-7-1001"};if(defect=="expired")w=w with{FimUtc=DateTimeOffset.UtcNow.AddSeconds(-1)};Assert.Null(await new ToledoSapWindowRetryApprovals(s,s,w,TimeProvider.System).ObterAsync(s.Row.ReciboId,actor,default));Assert.Equal(0,s.Commands);}
 [Fact] public async Task Stale_revision_or_missing_confirmation_still_blocks_the_command()
 {var s=new Store();var service=new ReenvioToledoSapService(s,new ToledoSapWindowRetryApprovals(s,s,Window(),TimeProvider.System),TimeProvider.System);var row=s.Row;var cmd=new ComandoReenvioToledoSap(Guid.NewGuid(),row.ReciboId,row.IntegracaoId,new('C',64),"Offline operator recovery justification",true);Assert.Equal("BLOCKED",(await service.SolicitarAsync(cmd,new(true,Sid),default)).Estado);cmd=cmd with{Revisao=row.Revisao,Confirmado=false};Assert.Equal("BLOCKED",(await service.SolicitarAsync(cmd,new(true,Sid),default)).Estado);Assert.Equal(0,s.Commands);}
 sealed class Store:IToledoSapRetryStore,IToledoSapAdmissionProof
 {
  public DevolutivaToledoSapSnapshot Row=new(Guid.NewGuid(),Guid.NewGuid(),"FALHA_DEFINITIVA","FalhaAntesDoPost",503,1,false,false,true,false,true,new('A',64),new('B',64),DateTimeOffset.UtcNow);public bool Proven=true;public int Commands;public ToledoSapRetryApproval? LastApproval;
  public Task<DevolutivaToledoSapSnapshot?> LerAsync(Guid id,CancellationToken ct)=>Task.FromResult<DevolutivaToledoSapSnapshot?>(id==Row.ReciboId?Row:null);
  public Task<bool> ProvarAdmissaoAsync(Guid receipt,Guid integration,ToledoSapOperatorWindow w,CancellationToken ct)=>Task.FromResult(Proven&&receipt==Row.ReciboId&&integration==Row.IntegracaoId&&w.OrigemAdmitida=="OFFLINE_PRIVATE_SOURCE");
  public Task<ReciboComandoToledoSap> AdmitirAsync(ComandoReenvioToledoSap c,AcmRetryActor actor,ToledoSapRetryApproval approval,CancellationToken ct){Commands++;LastApproval=approval;return Task.FromResult(new ReciboComandoToledoSap(c.ComandoId,c.ReciboId,"QUEUED"));}
 }
}
