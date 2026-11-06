using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Callback.Http;
using FugaTES.Persistencia.Recebimento;

namespace FugaTES.Testes;

public sealed class ToledoSapCallbackOutcomeProofTests
{
 internal static readonly Guid Receipt=Guid.Parse("10000000-0000-0000-0000-000000000001"),Integration=Guid.Parse("20000000-0000-0000-0000-000000000001"),Attempt=Guid.Parse("30000000-0000-0000-0000-000000000001");
 internal static readonly string OriginalHash=new('A',64),RequestHash=new('B',64);
 internal static ToledoSapCallbackSnapshot Proof(bool already=false)
 {
  var result=new RetornoToledoSap(already?ResultadoToledoSapCodigo.RecebimentoJaConfirmadoSap:ResultadoToledoSapCodigo.RecebimentoConfirmadoSap,already?200:201,!already)
   {EntityId="https://sap.invalid/ReceberPesagem('SYNTHETIC')",RequestSha256=RequestHash,ResponseSha256=new('C',64),PayloadVersion="TOLEDO_SAP_ODATA_V1"};
  return new(Receipt,Integration,OriginalHash,ToledoSapWork.Fingerprint(Receipt,OriginalHash),$"toledo-sap:{Receipt:D}")
  {Integrations=1,Admitted=true,State="CONCLUIDA",SendPhase="RESULTADO_RECEBIDO",Attempt=Attempt,Fence=7,AttemptOutcome="SUCESSO",AttemptPhase="RESULTADO_RECEBIDO",
   Audits=[new("toledo_sap_documento_preparado",Attempt,7,"SYNTHETIC_WORKER",RequestHash,result.PayloadVersion,null),new("toledo_sap_resultado",Attempt,7,"SYNTHETIC_WORKER",RequestHash,result.PayloadVersion,result)]};
 }
 internal static ToledoSapCallbackAck? Evaluate(ToledoSapCallbackSnapshot? s)=>ToledoSapCallbackAcknowledgement.Evaluate(s,Receipt,Integration,OriginalHash);
 [Theory][InlineData(false)][InlineData(true)]
 public void Only_matching_durable_business_confirmation_can_produce_OK(bool already)
 {var result=Evaluate(Proof(already));Assert.True(result!.Confirmed);Assert.Equal(already?200:201,result.SapHttpStatus);}
 [Theory]
 [InlineData("receipt")][InlineData("integration")][InlineData("originalHash")][InlineData("integrationHash")][InlineData("correlation")]
 [InlineData("multipleIntegrations")][InlineData("unprovenOrigin")][InlineData("synthetic")][InlineData("invalidEvidence")]
 [InlineData("missingAttempt")][InlineData("missingFence")][InlineData("sendPhase")][InlineData("attemptPhase")][InlineData("attemptFailure")]
 [InlineData("missingPrepared")][InlineData("missingResult")][InlineData("duplicateResult")][InlineData("oldAttempt")][InlineData("oldFence")]
 [InlineData("foreignWorker")][InlineData("blankWorker")][InlineData("requestHash")][InlineData("payloadVersion")]
 [InlineData("responseHash")][InlineData("rawHttp201")][InlineData("unprovenExisting")]
 public void Incomplete_ambiguous_or_stale_success_cannot_ACK(string defect)
 {
  var p=Proof();var audits=p.Audits.ToArray();var result=audits[1].Result!;
  var bad=defect switch
  {
   "receipt"=>p with{Receipt=Guid.NewGuid()},"integration"=>p with{Integration=Guid.NewGuid()},"originalHash"=>p with{OriginalHash=new('D',64)},
   "integrationHash"=>p with{IntegrationHash=new('D',64)},"correlation"=>p with{Correlation="foreign"},"multipleIntegrations"=>p with{Integrations=2},
   "unprovenOrigin"=>p with{Admitted=false},"synthetic"=>p with{Synthetic=true},"invalidEvidence"=>p with{EvidenceValid=false},
   "missingAttempt"=>p with{Attempt=null},"missingFence"=>p with{Fence=0},"sendPhase"=>p with{SendPhase="ENVIO_INICIADO"},
   "attemptPhase"=>p with{AttemptPhase="ENVIO_INICIADO"},"attemptFailure"=>p with{AttemptOutcome="FALHA"},
   "missingPrepared"=>p with{Audits=[audits[1]]},"missingResult"=>p with{Audits=[audits[0]]},"duplicateResult"=>p with{Audits=[audits[0],audits[1],audits[1]]},
   "oldAttempt"=>p with{Audits=[audits[0],audits[1] with{Attempt=Guid.NewGuid()}]},"oldFence"=>p with{Audits=[audits[0],audits[1] with{Fence=6}]},
   "foreignWorker"=>p with{Audits=[audits[0],audits[1] with{Worker="OTHER_WORKER"}]},"blankWorker"=>p with{Audits=[audits[0],audits[1] with{Worker=""}]},
   "requestHash"=>p with{Audits=[audits[0] with{RequestSha256=new('D',64)},audits[1]]},"payloadVersion"=>p with{Audits=[audits[0] with{PayloadVersion="other"},audits[1]]},
   "responseHash"=>p with{Audits=[audits[0],audits[1] with{Result=result with{ResponseSha256=null}}]},
   "rawHttp201"=>p with{Audits=[audits[0],audits[1] with{Result=result with{Codigo=ResultadoToledoSapCodigo.AceiteHttpSemConfirmacaoEmpresarial}}]},
   "unprovenExisting"=>p with{Audits=[audits[0],audits[1] with{Result=result with{Codigo=ResultadoToledoSapCodigo.RecebimentoJaConfirmadoSap,HttpStatus=200,PostIniciado=true}}]},
   _=>throw new ArgumentException(defect)
  };
  Assert.False(Evaluate(bad)!.Confirmed);
 }
 [Theory][InlineData("PENDENTE")][InlineData("EM_PROCESSAMENTO")][InlineData("AGUARDANDO_REPROCESSAMENTO")]
 public void Result_audit_before_final_journal_commit_is_still_pending(string state)
 {Assert.Null(Evaluate(Proof() with{State=state,Active=true,AttemptOutcome="EM_EXECUCAO"}));}
 [Fact]public void Missing_readback_is_not_success()=>Assert.Equal(503,Evaluate(null)!.HttpStatus);
 [Fact]public void SAP_validation_rejection_is_a_safe_nonpositive_response()
 {
  var p=Proof();var failed=new RetornoToledoSap(ResultadoToledoSapCodigo.RejeicaoHttp,400,true);
  var result=Evaluate(p with{State="FALHA_DEFINITIVA",AttemptOutcome="FALHA",Audits=[p.Audits[0],p.Audits[1] with{Result=failed}]});
  Assert.Equal(422,result!.HttpStatus);Assert.Equal(400,result.SapHttpStatus);Assert.False(result.Confirmed);
 }
 [Fact]public void Unknown_result_is_not_proof_of_SAP_failure_or_absence()
 {var result=Evaluate(Proof() with{State="RESULTADO_INDETERMINADO"});Assert.Equal(409,result!.HttpStatus);Assert.False(result.Confirmed);}
 [Fact]public void Production_wait_is_exactly45seconds_and_bounded()
 {Assert.Equal(TimeSpan.FromSeconds(45),ToledoSapCallbackAckOptions.Approved.WaitTimeout);Assert.True(ToledoSapCallbackAckOptions.Approved.Valid);Assert.False(new ToledoSapCallbackAckOptions(TimeSpan.FromSeconds(46),TimeSpan.FromSeconds(1)).Valid);}
 [Fact]public async Task Observation_timeout_does_not_claim_success()
 {
  var reader=new Reader((_,_,_)=>Task.FromResult<ToledoSapCallbackSnapshot?>(Proof() with{State="EM_PROCESSAMENTO",Active=true}));
  var result=await new ToledoSapCallbackAcknowledgement(reader,new(TimeSpan.FromMilliseconds(50),TimeSpan.FromMilliseconds(5))).WaitAsync(new(Receipt,DateTimeOffset.UtcNow,false){SapIntegracaoId=Integration},OriginalHash,TimeSpan.Zero,default);
  Assert.Equal(504,result.HttpStatus);Assert.False(result.Confirmed);Assert.True(reader.Calls>0);
 }
 [Fact]public async Task Expired_total_request_budget_does_not_start_another_read_or_dispatch()
 {
  var reader=new Reader((_,_,_)=>throw new Exception("MUST_NOT_BE_CALLED"));
  var result=await new ToledoSapCallbackAcknowledgement(reader,ToledoSapCallbackAckOptions.Approved).WaitAsync(new(Receipt,DateTimeOffset.UtcNow,false){SapIntegracaoId=Integration},OriginalHash,TimeSpan.FromSeconds(46),default);
  Assert.Equal(504,result.HttpStatus);Assert.Equal(0,reader.Calls);
 }
 [Fact]public async Task Reader_failure_is_sanitized_and_never_OK()
 {
  var reader=new Reader((_,_,_)=>throw new Exception("SYNTHETIC_SECRET_MUST_NOT_LEAK"));
  var result=await new ToledoSapCallbackAcknowledgement(reader,ToledoSapCallbackAckOptions.Approved).WaitAsync(new(Receipt,DateTimeOffset.UtcNow,false){SapIntegracaoId=Integration},OriginalHash,TimeSpan.Zero,default);
  Assert.Equal(503,result.HttpStatus);Assert.DoesNotContain("SYNTHETIC_SECRET",result.Message);
 }
 [Fact]public async Task Observation_deadline_also_bounds_a_reader_with_delayed_cancellation_cleanup()
 {
  var cleanup=new TaskCompletionSource<ToledoSapCallbackSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
  var reader=new Reader((_,_,_)=>cleanup.Task);
  try
  {
   var wait=new ToledoSapCallbackAcknowledgement(reader,new(TimeSpan.FromMilliseconds(50),TimeSpan.FromMilliseconds(5))).WaitAsync(new(Receipt,DateTimeOffset.UtcNow,false){SapIntegracaoId=Integration},OriginalHash,TimeSpan.Zero,default);
   var result=await wait.WaitAsync(TimeSpan.FromSeconds(1));Assert.Equal(504,result.HttpStatus);
  }
  finally{cleanup.TrySetResult(null);}
 }
 [Fact]public async Task Disconnect_cancels_only_read_observation()
 {
  var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  var reader=new Reader(async(_,_,ct)=>{entered.SetResult();await Task.Delay(Timeout.Infinite,ct);return null;});
  using var request=new CancellationTokenSource();var wait=new ToledoSapCallbackAcknowledgement(reader,ToledoSapCallbackAckOptions.Approved).WaitAsync(new(Receipt,DateTimeOffset.UtcNow,false){SapIntegracaoId=Integration},OriginalHash,TimeSpan.Zero,request.Token);
  await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));request.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await wait);Assert.Equal(1,reader.Calls);
 }
 internal sealed class Reader(Func<Guid,Guid,CancellationToken,Task<ToledoSapCallbackSnapshot?>> read):IToledoSapCallbackOutcomeRead
 {public int Calls;public Task<ToledoSapCallbackSnapshot?> ReadAsync(Guid receipt,Guid integration,CancellationToken ct){Interlocked.Increment(ref Calls);return read(receipt,integration,ct);}}
}
