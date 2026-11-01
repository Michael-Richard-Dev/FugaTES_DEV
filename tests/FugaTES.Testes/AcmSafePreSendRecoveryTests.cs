using System.Reflection;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Callback.Http.ManualRetry;
using FugaTES.Persistencia.ManualRetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public sealed class AcmSafePreSendRecoveryTests
{
 const string Sid="S-1-5-21-1-2-3-1001",H="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
 static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-01T12:00:00Z");
 static readonly Guid Id=Guid.Parse("77777777-1111-2222-3333-444444444442"),Attempt=Guid.Parse("77777777-1111-2222-3333-444444444443");
 static AcmRetrySnapshot Failed()=>new(){IntegrationId=Id,Contract="0099999975",Item=null,Version=2,Fingerprint=H,CanonicalFingerprint=H,CorrelationId="OFFLINE_SAP_CONTRACT",IdentityVerified=true,LatestVersion=true,ReceivedUtc=Now.AddDays(-1),State="AGUARDANDO_REPROCESSAMENTO",Phase="RESULTADO_RECEBIDO",Fence=1,AttemptId=Attempt,AttemptCount=1,AttemptOutcome="FALHA",JournalPresent=true,JournalStatus="AGUARDANDO_DEPENDENCIA_EXTERNA",Stage="R165U_PRE_SEND_BLOCKED",ErrorCode="R171_EMITTER_RESOLVER_UNAVAILABLE",HasPendingCommand=true};
 static AcmRetryApproval Approval(AcmRetrySnapshot s)=>new(){IntegrationId=Id,Contract=s.Contract,Item=s.Item,ExpectedVersion=s.Version,ExpectedReceivedUtc=s.ReceivedUtc,Fingerprint=H,ObservedSnapshotDigest=s.Digest(),CorrectedFailureDigest=s.Digest(),ExternalCorrectionEvidence="OFFLINE_VERIFIED_COMPOSITION_FIX",BusinessOriginEvidence="OFFLINE_SAP_RECEIPT",OperatorSid=Sid,Homologated=true,ExecutionAuthorized=true,ExclusiveWorker=true,AuthorizationReference="OFFLINE_UNIT_APPROVAL",NotBeforeUtc=Now.AddMinutes(-1),UntilUtc=Now.AddMinutes(5),AllowedStage=AcmRetryStage.SoapOnly};
 static bool Known(AcmRetrySnapshot s)
 {var m=typeof(AcmManualRetryPolicy).GetMethod("IsKnownPreSendFailure");Assert.NotNull(m);return (bool)m.Invoke(null,[s])!;}
 [Fact] public void Known_local_pre_send_failure_is_not_an_unknown_external_outcome()=>Assert.True(Known(Failed()));
 [Theory][InlineData("active")][InlineData("lease")][InlineData("unknown")][InlineData("unsafe")][InlineData("armed")][InlineData("rest_armed")][InlineData("remote")][InlineData("callback")][InlineData("requested")][InlineData("confirmed")][InlineData("reference")][InlineData("metadata")][InlineData("success")][InlineData("phase")][InlineData("journal")][InlineData("empty_error")][InlineData("timeout")][InlineData("simulation")][InlineData("not_latest")]
 public void Unsafe_or_remote_bearing_result_cannot_be_reclassified(string fault)
 {var s=Failed();s=fault switch{"active"=>s with{Worker="worker"},"lease"=>s with{LeaseUntil=Now},"unknown"=>s with{Indeterminate=true},"unsafe"=>s with{UnsafeAttempt=true},"armed"=>s with{Stage="R165U_SOAP_ARMED"},"rest_armed"=>s with{Stage="R165U_REST_ARMED"},"remote"=>s with{ContractHasExternal=true},"callback"=>s with{HasProtocolOrCallback=true},"requested"=>s with{GuardianRequestedCode="0099999975"},"confirmed"=>s with{GuardianConfirmedCode="0099999975"},"reference"=>s with{Reference="REMOTE"},"metadata"=>s with{Metadata="U:remote"},"success"=>s with{AttemptOutcome="SUCESSO"},"phase"=>s with{Phase="ENVIO_INICIADO"},"journal"=>s with{JournalPresent=false},"empty_error"=>s with{ErrorCode=null},"timeout"=>s with{ErrorCode="SOAP_TIMEOUT"},"simulation"=>s with{Synthetic=true},_=>s with{LatestVersion=false}};Assert.False(Known(s));}
 [Fact] public void Exact_corrected_manual_failure_with_null_item_can_resume_soap_only()
 {var s=Failed() with{State="AGUARDANDO_ACAO_MANUAL",HasPendingCommand=false};var d=AcmManualRetryPolicy.Evaluate(s,Approval(s),new(true,Sid),Now);Assert.True(d.Eligible,d.Code);Assert.Equal(AcmRetryStage.SoapOnly,d.Stage);Assert.Equal("SAFE_PRE_DISPATCH",d.Code);}
 [Theory][InlineData("version")][InlineData("received")][InlineData("correction")][InlineData("origin")][InlineData("expired")][InlineData("pending")][InlineData("completed")][InlineData("400")]
 public void Retry_still_requires_current_exact_scope_and_proven_correction(string fault)
 {var s=Failed() with{State="AGUARDANDO_ACAO_MANUAL",HasPendingCommand=false};if(fault=="pending")s=s with{HasPendingCommand=true};if(fault=="completed")s=s with{State="CONCLUIDA"};if(fault=="400")s=s with{ErrorCategory="GUARDIAN_REST_HTTP_400"};var a=Approval(s);a=fault switch{"version"=>a with{ExpectedVersion=1},"received"=>a with{ExpectedReceivedUtc=Now},"correction"=>a with{CorrectedFailureDigest=null,ExternalCorrectionEvidence=null},"origin"=>a with{BusinessOriginEvidence=""},"expired"=>a with{UntilUtc=Now},_=>a};Assert.False(AcmManualRetryPolicy.Evaluate(s,a,new(true,Sid),Now).Eligible);}
 [Fact] public void Real_command_composition_registers_dynamic_resolver_before_constructing_gate_and_preparation()
 {
  var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
   ["HostIntegracao:PersistenceMode"]="PostgreSql",["HostIntegracao:ExternalMode"]="Guardian",["HostIntegracao:PollInterval"]="00:00:05",["HostIntegracao:ShutdownGracePeriod"]="00:00:10",
   ["ConnectionStrings:FugaTES"]="Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_acm_hml_app;SearchPath=fugates",
   ["MotorIntegracao:LeaseDuration"]="00:01:00",["MotorIntegracao:LeaseRenewInterval"]="00:00:10",["MotorIntegracao:RetryBackoff"]="00:01:00",["TimeoutProcessamentoIntegracao:Timeout"]="00:00:30",
   ["Guardian:BaseUrl"]="https://offline.invalid/",["Guardian:Timeout"]="00:00:05",["Guardian:Email"]="offline",["Guardian:Password"]="OFFLINE_NOT_A_CREDENTIAL",["Guardian:Signature"]="133289",
   ["Sap:Enabled"]="false",["AcmR57:Enabled"]="true",["AcmR57:Normal:Enabled"]="true",["AcmR57:Normal:DynamicEmitterValidationEnabled"]="false",["FUGATES_INTEGRATION_WORKER_ENABLED"]="false"}).Build();
  var normal=new AcmR57NormalOptions{Enabled=true,ExecutionAuthorized=true,DynamicEmitterValidationEnabled=true,DynamicEmitterSignatureId=133289};
  var services=new ServiceCollection();services.AddSingleton<IConfiguration>(config);
  var m=typeof(AcmManualRetryCommandHost).GetMethod("ConfigureApprovedRuntime");Assert.NotNull(m);m.Invoke(null,[services,config,normal]);
  using var root=services.BuildServiceProvider();using var scope=root.CreateScope();
  Assert.Same(normal,scope.ServiceProvider.GetRequiredService<AcmR57NormalOptions>());
  Assert.NotNull(scope.ServiceProvider.GetRequiredService<AcmGuardianEmitterResolver>());
  var preparation=scope.ServiceProvider.GetRequiredService<IAcmR57Preparacao>();
  var field=preparation.GetType().GetFields(BindingFlags.Instance|BindingFlags.NonPublic).Single(f=>f.FieldType==typeof(AcmGuardianEmitterResolver));
  Assert.NotNull(field.GetValue(preparation));Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAcmR57RuntimeGate>());
  Assert.DoesNotContain(services,d=>d.ServiceType==typeof(IHostedService));
 }

 static AcmManualRetryPostgreSql.Audit OriginalAudit(AcmRetrySnapshot current)
 {
  var before=current with{State="PENDENTE",Phase="NAO_INICIADO",Fence=0,AttemptId=null,AttemptCount=0,AttemptOutcome=null,JournalPresent=false,JournalStatus=null,Stage=null,ErrorCode=null,HasPendingCommand=false,
   Predecessors=[new(){IntegrationId=Guid.Parse("77777777-1111-2222-3333-444444444441"),Version=1,CorrelationId="OFFLINE_PRIOR_SAP",Fingerprint=H,CanonicalFingerprint=H,IdentityVerified=true,State="PENDENTE",Phase="NAO_INICIADO"}]};
  var original=Approval(before) with{RecoverLatestPending=true,AllowSafeSupersession=true,NotBeforeUtc=Now.AddMinutes(-4),UntilUtc=Now.AddMinutes(-1),AuthorizationReference="OFFLINE_ORIGINAL_APPROVAL"};
  var request=new AcmRetryRequest(Guid.Parse("77777777-1111-2222-3333-444444444449"),Id,before.Digest(),"Solicito cadastro somente SOAP do contrato aprovado",true);
  return new(request,new(true,Sid),original.AuthorizationReference,Now.AddMinutes(-2),before,AcmRetryStage.SoapOnly,null,original);
 }
 static bool Reconcile(AcmManualRetryPostgreSql.Audit old,AcmRetrySnapshot current,AcmRetryApproval a)
 {var m=typeof(AcmManualRetryPostgreSql).GetMethod("CanReconcileKnownPreSend");Assert.NotNull(m);return (bool)m.Invoke(null,[old,current,a,new AcmRetryActor(true,Sid),Now])!;}
 [Fact]public void Reconciliation_requires_new_explicit_scope_and_preserves_the_original_admission()
 {var s=Failed();var old=OriginalAudit(s);var oldDecision=AcmManualRetryPolicy.Evaluate(old.Before,old.Approval,old.Actor,old.At);Assert.True(oldDecision.Eligible,oldDecision.Code);Assert.True(Reconcile(old,s,Approval(s)));Assert.Equal("PENDENTE",old.Before.State);}
 [Theory][InlineData("confirm")][InlineData("justification")][InlineData("original_unapproved")][InlineData("original_synthetic")][InlineData("original_digest")][InlineData("original_actor")][InlineData("original_fence")][InlineData("wrong_version")][InlineData("wrong_received")][InlineData("stale_digest")][InlineData("missing_correction")][InlineData("closed_window")][InlineData("missing_pending")][InlineData("active_worker")][InlineData("external_effect")][InlineData("wrong_stage")]
 public void Reconciliation_cannot_turn_an_unknown_remote_or_unapproved_history_into_a_retry(string fault)
 {
  var s=Failed();var old=OriginalAudit(s);var a=Approval(s);
  old=fault switch{
   "confirm"=>old with{Request=old.Request with{Confirm=false}},
   "justification"=>old with{Request=old.Request with{Justification=""}},
   "original_unapproved"=>old with{Approval=old.Approval with{ExecutionAuthorized=false}},
   "original_synthetic"=>old with{Before=old.Before with{Synthetic=true}},
   "original_digest"=>old with{Request=old.Request with{SnapshotDigest=new string('B',64)}},
   "original_actor"=>old with{Actor=new(true,"S-1-5-21-9-9-9-1001")},
   "original_fence"=>old with{Before=old.Before with{Fence=2}},_=>old};
  if(fault=="original_synthetic")old=old with{Request=old.Request with{SnapshotDigest=old.Before.Digest()}};
  a=fault switch{"wrong_version"=>a with{ExpectedVersion=1},"wrong_received"=>a with{ExpectedReceivedUtc=Now},"stale_digest"=>a with{ObservedSnapshotDigest=new string('B',64)},"missing_correction"=>a with{ExternalCorrectionEvidence=null},"closed_window"=>a with{UntilUtc=Now},"wrong_stage"=>a with{AllowedStage=AcmRetryStage.SoapThenRest},_=>a};
  s=fault switch{"missing_pending"=>s with{HasPendingCommand=false},"active_worker"=>s with{Worker="ACTIVE"},"external_effect"=>s with{ContractHasExternal=true},_=>s};
  Assert.False(Reconcile(old,s,a));
 }

 [Theory][InlineData("match",true)][InlineData("superseded",false)][InlineData("received",false)][InlineData("version",false)][InlineData("no_received",false)]
 public void SOAP_start_revalidates_latest_version_and_original_receipt_after_admission(string fault,bool expected)
 {var s=Failed();var a=Approval(s);s=fault switch{"superseded"=>s with{LatestVersion=false},"received"=>s with{ReceivedUtc=Now},"version"=>s with{Version=3},"no_received"=>s with{ReceivedUtc=null},_=>s};var m=typeof(AcmManualRetryPolicy).GetMethod("ExactSoapRecoveryScope");Assert.NotNull(m);Assert.Equal(expected,(bool)m.Invoke(null,[s,a])!);}

 static AcmRetrySnapshot SoapConfirmed()=>Failed() with{Stage="R165U_PROTOCOL_BLOCKED",ErrorCode="ACM_MANUAL_SOAP_ONLY_PROTOCOL_NOT_AUTHORIZED",GuardianRequestedCode="0099999975",GuardianConfirmedCode="0099999975",Reference="OFFLINE_CONFIRMED_REFERENCE",Metadata="U:"+H+"::321"};
 static bool ProtocolPending(AcmRetrySnapshot s,AcmRetryStage stage)
 {var m=typeof(AcmManualRetryPolicy).GetMethod("IsKnownSoapOnlyProtocolPending");Assert.NotNull(m);return (bool)m.Invoke(null,[s,stage])!;}
 [Fact]public void SOAP_success_with_protocol_not_authorized_is_known_and_never_retries_SOAP()
 {Assert.True(ProtocolPending(SoapConfirmed(),AcmRetryStage.SoapOnly));Assert.False(ProtocolPending(SoapConfirmed(),AcmRetryStage.SoapThenRest));}
 [Theory][InlineData("armed")][InlineData("unknown")][InlineData("mismatch")][InlineData("no_ref")][InlineData("no_code")][InlineData("no_sequence")][InlineData("bad_hash")][InlineData("protocol")][InlineData("active")][InlineData("wrong_error")]
 public void SOAP_only_result_requires_durable_confirmed_identity_and_no_protocol_or_ambiguity(string fault)
 {var s=SoapConfirmed();s=fault switch{"armed"=>s with{Stage="R165U_SOAP_ARMED"},"unknown"=>s with{Indeterminate=true},"mismatch"=>s with{GuardianConfirmedCode="OTHER"},"no_ref"=>s with{Reference=null},"no_code"=>s with{GuardianConfirmedCode=null},"no_sequence"=>s with{Metadata="U:"+H+"::"},"bad_hash"=>s with{Metadata="U:bad::321"},"protocol"=>s with{HasProtocolOrCallback=true},"active"=>s with{Worker="worker"},_=>s with{ErrorCode="UNKNOWN"}};Assert.False(ProtocolPending(s,AcmRetryStage.SoapOnly));}
}
