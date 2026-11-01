using System.Text.Json;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Callback.Http.ManualRetry;
public sealed class AcmPendingLatestRecoveryTests
{
 static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-10-31T20:00:00Z");
 const string H="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
 static readonly Guid Target=Guid.Parse("99999999-1111-2222-3333-444444444442"),Prior=Guid.Parse("99999999-1111-2222-3333-444444444441");
 static AcmRetryActor Actor=>new(true,"S-1-5-21-1-2-3-1001");
 static void Set(object value,string property,object? data){var p=value.GetType().GetProperty(property);Assert.NotNull(p);p.SetValue(value,data);}
 static Dictionary<string,object?> Predecessor()=>new(){["IntegrationId"]=Prior,["Version"]=1L,["Fingerprint"]=H,["CanonicalFingerprint"]=H,["IdentityVerified"]=true,["CorrelationId"]="OFFLINE_PREVIOUS_INBOUND",["AttemptFence"]=1L,["State"]="AGUARDANDO_REPROCESSAMENTO",["Fence"]=1L,["AttemptCount"]=1,["AttemptId"]=Guid.Parse("99999999-1111-2222-3333-555555555555"),["AttemptOutcome"]="FALHA",["Phase"]="RESULTADO_RECEBIDO",["JournalPresent"]=true,["Stage"]="R165U_PRE_SEND_BLOCKED",["ErrorCode"]="R170_PARTNER_MAPPING_REQUIRED",["JournalStatus"]="AGUARDANDO_DEPENDENCIA_EXTERNA",["JournalDeterminate"]=true,["AllAttemptsFailed"]=true};
 static AcmRetrySnapshot Pending(Action<Dictionary<string,object?>>? prior=null)
 {
  var s=new AcmRetrySnapshot{IntegrationId=Target,Contract="0099999975",Item=null,Version=2,Fingerprint=H,CanonicalFingerprint=H,CorrelationId="OFFLINE_AUTHENTICATED_INBOUND",State="PENDENTE",Fence=0,IdentityVerified=true};
  Set(s,"LatestVersion",true);Set(s,"ReceivedUtc",Now.AddHours(-3));Set(s,"Phase","NAO_INICIADO");
  var p=Predecessor();prior?.Invoke(p);var field=s.GetType().GetProperty("Predecessors");Assert.NotNull(field);
  field.SetValue(s,JsonSerializer.Deserialize(JsonSerializer.Serialize(new[]{p}),field.PropertyType));return s;
 }
 static AcmRetryApproval Approve(AcmRetrySnapshot s,bool pending=true)
 {
  var a=new AcmRetryApproval{Homologated=true,ExecutionAuthorized=true,ExclusiveWorker=true,IntegrationId=s.IntegrationId,Contract=s.Contract,Item=s.Item,Fingerprint=H,ObservedSnapshotDigest=s.Digest(),OperatorSid=Actor.Sid,AuthorizationReference="OFFLINE_NOMINAL_PENDING_RECOVERY",BusinessOriginEvidence="OFFLINE_OPERATOR_CONFIRMED_SOURCE",NotBeforeUtc=Now.AddMinutes(-1),UntilUtc=Now.AddMinutes(5),AllowedStage=AcmRetryStage.SoapOnly};
  Set(a,"RecoverLatestPending",pending);Set(a,"ExpectedVersion",2L);Set(a,"ExpectedReceivedUtc",Now.AddHours(-3));Set(a,"AllowSafeSupersession",true);return a;
 }
 [Fact] public void Latest_pending_sale_without_Item_is_eligible_only_under_exact_server_approval()
 {var s=Pending();var d=AcmManualRetryPolicy.Evaluate(s,Approve(s),Actor,Now);Assert.True(d.Eligible,d.Code);Assert.Equal("PENDING_LATEST_SAFE_ADMISSION",d.Code);Assert.Equal(AcmRetryStage.SoapOnly,d.Stage);Assert.Contains("substitu",d.Message,StringComparison.OrdinalIgnoreCase);}
 [Fact] public void Nominal_SOAP_only_pending_approval_is_explicit_and_never_reuses_REST_scope()
 {var s=Pending();var a=Approve(s) with{AllowedStage=(AcmRetryStage)3};var d=AcmManualRetryPolicy.Evaluate(s,a,Actor,Now);Assert.True(d.Eligible,d.Code);Assert.Equal((AcmRetryStage)3,d.Stage);Assert.Contains("SOAP",d.Message);Assert.Contains("protocolo",d.Message,StringComparison.OrdinalIgnoreCase);
  Assert.False(AcmManualRetryPolicy.Evaluate(s,a with{AllowedStage=AcmRetryStage.RestOnly},Actor,Now).Eligible);}
 [Fact] public void SOAP_only_profile_creates_only_a_bound_manual_scope()
 {var s=Pending();var a=Approve(s) with{AllowedStage=(AcmRetryStage)3};var p=new AcmManualRetryCommandProfile{BaseConfigurationFile=@"C:\offline\base.json",CredentialFile=@"C:\offline\command.key",ApprovalsFile=@"C:\offline\approval.json",Passfile=@"C:\offline\pgpass",NormalConfigurationFile=@"C:\offline\normal.json",OperatorSid=Actor.Sid,WorkerIdentity="OFFLINE_ONE_SHOT",Contract=s.Contract,Item=null};
  p.Validate([a]);
  var o=new FugaTES.Aplicacao.Servicos.Mvp.AcmR57NormalOptions{GuardianEnvironment="HML",GuardianIdentityEvidence="OFFLINE_GUARDIAN",PostgreSqlIdentityEvidence="OFFLINE_5433",JournalEvidence="OFFLINE_JOURNAL",IdentifierMappingEvidence="OFFLINE_MAPPING",DynamicEmitterSignatureId=133289};
  var result=AcmManualRetryCommandHost.ApproveNormal(p,a,o);Assert.True((bool)result.ManualRecoveryScope!.GetType().GetProperty("SoapOnly")!.GetValue(result.ManualRecoveryScope)!);
  Assert.Equal(Target,result.ManualRecoveryScope.IntegrationId);Assert.Null(result.ManualRecoveryScope.Item);Assert.False(result.AcmAutomatico.Enabled);Assert.False(result.CallbackApproved);
  // Failed SOAP-only recovery is now explicit too, but still requires exact version/receipt.
  p.Validate([a with{RecoverLatestPending=false}]);
  Assert.Throws<InvalidOperationException>(()=>p.Validate([a with{RecoverLatestPending=false,ExpectedVersion=0}]));
  Assert.Throws<InvalidOperationException>(()=>p.Validate([a with{RecoverLatestPending=false,ExpectedReceivedUtc=null}]));
 }

 [Fact] public void Existing_mode_does_not_silently_liberate_pending_or_null_Item()
 {var s=Pending();Assert.False(AcmManualRetryPolicy.Evaluate(s,Approve(s,false),Actor,Now).Eligible);}
 [Theory][InlineData("EM_PROCESSAMENTO")][InlineData("RESULTADO_INDETERMINADO")][InlineData("CONCLUIDA")][InlineData("AGUARDANDO_REPROCESSAMENTO")]
 public void Pending_recovery_does_not_consume_other_target_states(string state)
 {var s=Pending() with{State=state};Assert.False(AcmManualRetryPolicy.Evaluate(s,Approve(s),Actor,Now).Eligible);}
 [Theory][InlineData("HasProtocolOrCallback")][InlineData("HasPendingCommand")][InlineData("Synthetic")][InlineData("UnsafeAttempt")][InlineData("Indeterminate")][InlineData("JournalPresent")][InlineData("ContractHasExternal")]
 public void Target_external_unknown_busy_or_existing_journal_blocks(string flag)
 {var s=Pending();Set(s,flag,true);Assert.False(AcmManualRetryPolicy.Evaluate(s,Approve(s),Actor,Now).Eligible);}
 [Fact] public void Nonlatest_receipt_and_nonzero_fencing_or_attempt_cannot_be_first_admitted()
 {
  var s=Pending();Set(s,"LatestVersion",false);Assert.False(AcmManualRetryPolicy.Evaluate(s,Approve(s),Actor,Now).Eligible);
  s=Pending() with{Fence=1};Assert.False(AcmManualRetryPolicy.Evaluate(s,Approve(s),Actor,Now).Eligible);
  s=Pending();Set(s,"AttemptCount",1);Assert.False(AcmManualRetryPolicy.Evaluate(s,Approve(s),Actor,Now).Eligible);
 }
 [Theory][InlineData("HasExternal",true)][InlineData("HasPendingCommand",true)][InlineData("Synthetic",true)][InlineData("IdentityVerified",false)][InlineData("JournalDeterminate",false)][InlineData("AllAttemptsFailed",false)]
 public void Any_unsafe_predecessor_blocks_whole_admission(string field,bool value)
 {var s=Pending(p=>p[field]=value);Assert.False(AcmManualRetryPolicy.Evaluate(s,Approve(s),Actor,Now).Eligible);}
 [Theory][InlineData("State","EM_PROCESSAMENTO")][InlineData("State","CONCLUIDA")][InlineData("State","RESULTADO_INDETERMINADO")][InlineData("Stage","R165U_SOAP_ARMED")][InlineData("Stage","R165U_SOAP_CONFIRMED")][InlineData("ErrorCode","SOAP_TIMEOUT")][InlineData("ErrorCode","R165U_SOAP_LOOKUP_UNPROVEN")][InlineData("AttemptOutcome","INDETERMINADA")]
 public void Unsupported_predecessor_history_is_not_assumed_safe(string field,string value)
 {var s=Pending(p=>p[field]=value);Assert.False(AcmManualRetryPolicy.Evaluate(s,Approve(s),Actor,Now).Eligible);}
 [Fact] public void Multiple_old_attempts_have_no_unproven_previous_failure_adoption()
 {var s=Pending(p=>{p["AttemptCount"]=2;p["Fence"]=2L;});Assert.False(AcmManualRetryPolicy.Evaluate(s,Approve(s),Actor,Now).Eligible);}
 [Fact] public void Missing_or_changed_predecessor_fingerprint_version_or_lease_blocks()
 {
  foreach(var mutate in new Action<Dictionary<string,object?>>[]{p=>p["CanonicalFingerprint"]=new string('B',64),p=>p["Version"]=2L,p=>p["Worker"]="OTHER",p=>p["LeaseUntil"]=Now.AddMinutes(1)})
  {var s=Pending(mutate);Assert.False(AcmManualRetryPolicy.Evaluate(s,Approve(s),Actor,Now).Eligible);}
 }
 [Fact] public void Scope_time_identity_and_exact_snapshot_changes_are_rejected()
 {
  var s=Pending();var a=Approve(s);
  Assert.False(AcmManualRetryPolicy.Evaluate(s,a with{Item="10"},Actor,Now).Eligible);
  Assert.False(AcmManualRetryPolicy.Evaluate(s,a with{UntilUtc=Now},Actor,Now).Eligible);
  Assert.False(AcmManualRetryPolicy.Evaluate(s,a with{ObservedSnapshotDigest=new string('B',64)},Actor,Now).Eligible);
  Assert.False(AcmManualRetryPolicy.Evaluate(s,a,new(false,Actor.Sid),Now).Eligible);
  Set(a,"ExpectedVersion",1L);Assert.False(AcmManualRetryPolicy.Evaluate(s,a,Actor,Now).Eligible);
 }
 [Fact] public void No_recovery_without_explicit_supersession_or_business_origin_evidence()
 {var s=Pending();var a=Approve(s);Set(a,"AllowSafeSupersession",false);Assert.False(AcmManualRetryPolicy.Evaluate(s,a,Actor,Now).Eligible);Assert.False(AcmManualRetryPolicy.Evaluate(s,Approve(s) with{BusinessOriginEvidence=""},Actor,Now).Eligible);}
 [Fact] public void Pending_profile_accepts_null_as_exact_Item_not_as_missing_scope_or_REST_only()
 {
  var s=Pending();var a=Approve(s);var p=new AcmManualRetryCommandProfile{BaseConfigurationFile=@"C:\offline\base.json",CredentialFile=@"C:\offline\command.key",ApprovalsFile=@"C:\offline\approval.json",Passfile=@"C:\offline\pgpass",NormalConfigurationFile=@"C:\offline\normal.json",OperatorSid=Actor.Sid,WorkerIdentity="OFFLINE_ONE_SHOT",Contract=s.Contract,Item=null};
  p.Validate([a]);Assert.Throws<InvalidOperationException>(()=>p.Validate([a with{AllowedStage=AcmRetryStage.RestOnly}]));
 }
 [Theory][InlineData("CorrelationId","")][InlineData("CorrelationId"," ")][InlineData("AttemptFence","2")]
 public void Missing_predecessor_correlation_or_inconsistent_attempt_fence_blocks(string field,string value)
 {var s=Pending(p=>p[field]=field=="AttemptFence"?(object)long.Parse(value):value);Assert.False(AcmManualRetryPolicy.Evaluate(s,Approve(s),Actor,Now).Eligible);}
}
