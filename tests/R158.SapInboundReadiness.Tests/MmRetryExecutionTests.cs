using FugaTES.Aplicacao.Contratos.Mm;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
using FugaTES.Aplicacao.Servicos.ManualRetry;
namespace FugaTES.Testes;
public sealed class MmRetryExecutionTests
{
 public const string Sid="S-1-5-21-1-2-3-1001";
 public static MmRetryOptions Options()=>new(){Enabled=true,OperatorSid=Sid,AuthorizationReference="SYNTHETIC_OFFLINE_TEST_ONLY",GuardianSignatureId=133289};
 public static Task<MmPurchaseOrderSnapshot> Snapshot(CancellationToken ct)=>Task.FromResult(new MmPurchaseOrderSnapshot{RuntimeMode="VM",Host="vm-fugatesbio",State="MM_SAP_COMPLETE",LastSuccessUtc=DateTimeOffset.UtcNow,Items=[MmRetryPolicyTests.Source()]});
 static MmRetryService Service(Memory db,Gate gate)=>new(db,Snapshot,gate,Options(),TimeProvider.System);
 static MmRetryExecutor Worker(Memory db,Gate gate,Sender sender)=>new(db,gate,sender,Options());
 static MmRetryRequest Request(MmRetryPreview p)=>new(Guid.NewGuid(),p.Pedido,p.Item,p.SnapshotDigest,"Cadastros corrigidos e conferidos");
 public static AcmRetryActor Actor()=>new(true,Sid);
 [Fact] public async Task Corrected_failure_is_admitted_audited_and_dispatched_once_with_original_bytes()
 {
  var db=new Memory();var gate=new Gate();var service=Service(db,gate);var preview=await service.PreviewAsync(db.Row.Pedido,db.Row.Item,Actor(),default);Assert.True(preview.Decision.Allowed);
  var request=Request(preview);Assert.True((await service.SubmitAsync(request,Actor(),default)).Accepted);Assert.True((await service.SubmitAsync(request,Actor(),default)).Accepted);Assert.Single(db.Commands);
  var original=db.Row;var sender=new Sender();Assert.True(await Worker(db,gate,sender).ProcessOneAsync([MmRetryPolicyTests.Source()],()=>true,_=>Task.FromResult(true),default));
  Assert.Equal(1,sender.Calls);Assert.Equal(original.JsonEnviado,System.Text.Encoding.UTF8.GetString(sender.Bytes!));Assert.Equal(original.ReservaId,db.Row.ReservaId);Assert.Equal(original.Sha256,db.Row.Sha256);Assert.Equal(EstadoProtocoloMm.Criado,db.Row.Estado);
  var command=Assert.Single(db.Commands.Values);Assert.Equal(Sid,command.OperatorSid);Assert.Equal(request.Justification,command.Justification);Assert.Equal(original,command.Previous);Assert.Equal("CONCLUIDO",command.State);
  Assert.False((await service.PreviewAsync(db.Row.Pedido,db.Row.Item,Actor(),default)).Decision.Allowed);
 }
 [Fact] public async Task Missing_master_blocks_admission_and_does_not_create_command()
 {var db=new Memory();var gate=new Gate{Confirmed=false};var p=await Service(db,gate).PreviewAsync(db.Row.Pedido,db.Row.Item,Actor(),default);Assert.False(p.Decision.Allowed);Assert.Empty(db.Commands);}
 [Fact] public async Task Concurrent_submits_keep_one_pending_command()
 {
  var db=new Memory{PauseEnqueue=true};var gate=new Gate();var s=Service(db,gate);var p=await s.PreviewAsync(db.Row.Pedido,db.Row.Item,Actor(),default);
  await Task.WhenAll(s.SubmitAsync(Request(p),Actor(),default),s.SubmitAsync(Request(p),Actor(),default));Assert.Single(db.Commands);
 }
 [Fact] public async Task Wrong_operator_cannot_preview_or_submit()
 {var db=new Memory();var s=Service(db,new());var p=await s.PreviewAsync(db.Row.Pedido,db.Row.Item,new(true,"S-1-5-21-1-2-3-999"),default);Assert.False(p.Decision.Allowed);Assert.Empty(db.Commands);}
 [Fact] public async Task Failure_to_persist_command_never_reports_queued()
 {
  var db=new Memory{FailEnqueue=true};var s=Service(db,new());var p=await s.PreviewAsync(db.Row.Pedido,db.Row.Item,Actor(),default);
  await Assert.ThrowsAsync<IOException>(()=>s.SubmitAsync(Request(p),Actor(),default));Assert.Empty(db.Commands);
 }
 async Task<(Memory,Gate,Sender,MmRetryExecutor)> Queued()
 {
  var d=new Memory();var g=new Gate();var s=Service(d,g);var p=await s.PreviewAsync(d.Row.Pedido,d.Row.Item,Actor(),default);await s.SubmitAsync(Request(p),Actor(),default);var sender=new Sender();return(d,g,sender,Worker(d,g,sender));
 }
 [Fact] public async Task Master_removed_after_admission_blocks_POST_and_preserves_previous_outcome()
 {var(d,g,s,w)=await Queued();var original=d.Row;g.Confirmed=false;await w.ProcessOneAsync([MmRetryPolicyTests.Source()],()=>true,_=>Task.FromResult(true),default);Assert.Equal(0,s.Calls);Assert.Equal(original,d.Row);Assert.Equal("BLOQUEADO",Assert.Single(d.Commands.Values).State);}
 [Fact] public async Task Lost_lease_does_not_claim_or_send()
 {var(d,g,s,w)=await Queued();Assert.False(await w.ProcessOneAsync([MmRetryPolicyTests.Source()],()=>true,_=>Task.FromResult(false),default));Assert.Equal(0,s.Calls);Assert.Equal("PENDENTE",Assert.Single(d.Commands.Values).State);}
 [Fact] public async Task Failure_to_commit_start_never_calls_Guardian()
 {var(d,g,s,w)=await Queued();d.FailMark=true;await w.ProcessOneAsync([MmRetryPolicyTests.Source()],()=>true,_=>Task.FromResult(true),default);Assert.Equal(0,s.Calls);Assert.NotEqual(EstadoProtocoloMm.Criado,d.Row.Estado);}
 [Fact] public async Task Uncertain_external_result_latches_key_and_prevents_replay()
 {
  var(d,g,s,w)=await Queued();s.Throw=true;await w.ProcessOneAsync([MmRetryPolicyTests.Source()],()=>true,_=>Task.FromResult(true),default);
  Assert.Equal(1,s.Calls);Assert.Equal("INDETERMINADO",Assert.Single(d.Commands.Values).State);Assert.True(await d.HasPendingAsync(d.Row.Chave,default));
  Assert.False(await w.ProcessOneAsync([MmRetryPolicyTests.Source()],()=>true,_=>Task.FromResult(true),default));Assert.Equal(1,s.Calls);
 }
 [Fact] public async Task Result_persistence_failure_leaves_processing_latch_not_false_success()
 {
  var(d,g,s,w)=await Queued();d.FailFinish=true;await Assert.ThrowsAsync<IOException>(()=>w.ProcessOneAsync([MmRetryPolicyTests.Source()],()=>true,_=>Task.FromResult(true),default));Assert.Equal(1,s.Calls);Assert.NotEqual(EstadoProtocoloMm.Criado,d.Row.Estado);Assert.True(await d.HasPendingAsync(d.Row.Chave,default));
 }
 [Fact] public async Task Source_change_after_admission_does_not_POST()
 {var(d,g,s,w)=await Queued();await w.ProcessOneAsync([MmRetryPolicyTests.Source() with{Material="CHANGED"}],()=>true,_=>Task.FromResult(true),default);Assert.Equal(0,s.Calls);}
 [Fact] public async Task HTTP201_without_protocol_is_not_a_success()
 {var(d,g,s,w)=await Queued();s.Result=new(EstadoProtocoloMm.Criado,201,null,"missing");await w.ProcessOneAsync([MmRetryPolicyTests.Source()],()=>true,_=>Task.FromResult(true),default);Assert.Equal(EstadoProtocoloMm.Indeterminado,d.Row.Estado);}
 [Fact] public async Task Terminal_precheck_reason_is_available_through_durable_status()
 {
  var(d,g,s,w)=await Queued();g.Confirmed=false;await w.ProcessOneAsync([MmRetryPolicyTests.Source()],()=>true,_=>Task.FromResult(true),default);
  var command=Assert.Single(d.Commands.Values);var status=await Service(d,g).StatusAsync(command.Id,Actor(),default);Assert.Equal("MM_PRECHECK_MASTERS_MISSING",status!.Code);
 }
 public sealed class Gate:IMmMasterdataGate
 {public bool Confirmed=true;public Task<MmMasterdataCheck> CheckAsync(byte[] b,CancellationToken ct)=>Task.FromResult(new MmMasterdataCheck(Confirmed,Confirmed?"MM_PRECHECK_CONFIRMED":"MM_PRECHECK_MASTERS_MISSING","20000162","1000247",DateTimeOffset.UtcNow));}
 public sealed class Sender:IEnviarProtocoloMmGateway
 {public int Calls;public byte[]? Bytes;public bool Throw;public ResultadoProtocoloMm Result=new(EstadoProtocoloMm.Criado,201,82,"ok");public Task<ResultadoProtocoloMm> EnviarAsync(byte[] b,CancellationToken ct){Calls++;Bytes=b;return Throw?Task.FromException<ResultadoProtocoloMm>(new TimeoutException("SYNTHETIC")):Task.FromResult(Result);}}
 public sealed class Memory:IMmRetryStore
 {
  readonly object sync=new();public RegistroProtocoloMm Row=MmRetryPolicyTests.Record();public Dictionary<Guid,MmRetryCommand> Commands=new();public bool FailEnqueue,FailMark,FailFinish,PauseEnqueue;
  int enqueuers;readonly TaskCompletionSource release=new(TaskCreationOptions.RunContinuationsAsynchronously);
  public Task<RegistroProtocoloMm?> ReadRegistryAsync(string p,string i,CancellationToken ct)=>Task.FromResult<RegistroProtocoloMm?>(Row.Pedido==p&&Row.Item==i?Row:null);
  public Task<bool> HasPendingAsync(string key,CancellationToken ct){lock(sync)return Task.FromResult(Commands.Values.Any(x=>x.Previous.Chave==key&&x.State is "PENDENTE" or "EM_PROCESSAMENTO" or "INDETERMINADO"));}
  public Task<MmRetryCommand?> FindCommandAsync(Guid id,CancellationToken ct){lock(sync)return Task.FromResult(Commands.GetValueOrDefault(id));}
  public async Task<MmRetryReceipt> EnqueueAsync(MmRetryCommand c,CancellationToken ct)
  {if(PauseEnqueue){if(Interlocked.Increment(ref enqueuers)==2)release.SetResult();await release.Task.WaitAsync(ct);}lock(sync){if(FailEnqueue)throw new IOException("SYNTHETIC");if(Commands.ContainsKey(c.Id)||Commands.Values.Any(x=>x.Previous.Chave==c.Previous.Chave&&x.State is "PENDENTE" or "EM_PROCESSAMENTO" or "INDETERMINADO"))return new MmRetryReceipt(c.Id,c.Previous.Pedido,c.Previous.Item,"BLOCKED","MM_RETRY_ALREADY_PENDING");Commands.Add(c.Id,c);return c.Receipt();}}
  public Task<MmRetryCommand?> ClaimAsync(CancellationToken ct)
  {lock(sync){var job=Commands.Values.FirstOrDefault(x=>x.State=="PENDENTE");if(job is null)return Task.FromResult<MmRetryCommand?>(null);job=job with{Fence=Guid.NewGuid(),State="EM_PROCESSAMENTO"};Commands[job.Id]=job;return Task.FromResult<MmRetryCommand?>(job);}}
  public Task<bool> MarkDispatchAsync(MmRetryCommand c,CancellationToken ct)
  {lock(sync){if(FailMark)return Task.FromResult(false);Commands[c.Id]=c with{DispatchStartedUtc=DateTimeOffset.UtcNow};return Task.FromResult(true);}}
  public Task FinishAsync(MmRetryCommand c,ResultadoProtocoloMm r,bool dispatched,CancellationToken ct)
  {lock(sync){if(FailFinish)throw new IOException("SYNTHETIC");if(dispatched)Row=Row with{Estado=r.Estado,Http=r.Http,ProtocoloId=r.ProtocoloId,Mensagem=r.Mensagem,AtualizadoUtc=DateTimeOffset.UtcNow};Commands[c.Id]=Commands[c.Id] with{OutcomeCode=MmRetryReason.IsPublic(r.Mensagem)?r.Mensagem:null,State=r.Estado==EstadoProtocoloMm.Criado?"CONCLUIDO":r.Estado==EstadoProtocoloMm.Indeterminado?"INDETERMINADO":"BLOQUEADO"};return Task.CompletedTask;}}
 }
}
