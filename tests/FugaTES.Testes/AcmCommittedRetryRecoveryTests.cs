using System.Reflection;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Callback.Http.ManualRetry;
using FugaTES.Persistencia.ManualRetry;

public sealed class AcmCommittedRetryRecoveryTests
{
 static readonly Guid Id=Guid.Parse("99999999-1111-2222-3333-444444444442"),Command=Guid.Parse("99999999-1111-2222-3333-444444444499");
 const string Sid="S-1-5-21-1-2-3-1001",H="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
 static AcmManualRetryCommandProfile Profile()=>new(){BaseConfigurationFile=@"C:\offline\base.json",CredentialFile=@"C:\offline\bearer",ApprovalsFile=@"C:\offline\approval.json",Passfile=@"C:\offline\pgpass",NormalConfigurationFile=@"C:\offline\normal.json",OperatorSid=Sid,WorkerIdentity="OFFLINE_SINGLE_WORKER",Contract="0099999975",Item=null};
 static AcmRetryApproval Approval()=>new(){RecoverLatestPending=true,ExpectedVersion=2,ExpectedReceivedUtc=DateTimeOffset.Parse("2026-10-31T14:00:00Z"),AllowSafeSupersession=true,Homologated=true,ExecutionAuthorized=true,ExclusiveWorker=true,IntegrationId=Id,Contract="0099999975",Item=null,Fingerprint=H,ObservedSnapshotDigest=H,OperatorSid=Sid,AuthorizationReference="OFFLINE_EXISTING_COMMAND_APPROVED",BusinessOriginEvidence="OFFLINE_EXISTING_SAP_RECEIPT",NotBeforeUtc=DateTimeOffset.UtcNow.AddMinutes(-1),UntilUtc=DateTimeOffset.UtcNow.AddMinutes(5),AllowedStage=AcmRetryStage.SoapOnly};
 static void Resume(AcmManualRetryCommandProfile p,Guid? id){var property=p.GetType().GetProperty("ResumeCommittedCommandId");Assert.NotNull(property);property.SetValue(p,id);}
 static AcmRetryDispatchQueue Queue(AcmManualRetryCommandProfile p,params AcmRetryApproval[] approvals)
 {var method=typeof(AcmManualRetryCommandHost).GetMethod("CreateDispatchQueue",BindingFlags.Public|BindingFlags.Static);Assert.NotNull(method);try{return (AcmRetryDispatchQueue)method.Invoke(null,[p,approvals])!;}catch(TargetInvocationException e) when(e.InnerException is not null){throw e.InnerException;}}
 [Fact] public async Task A_committed_transaction_is_not_rolled_back_after_acknowledged_commit()
 {var method=typeof(AcmManualRetryPostgreSql).GetMethod("RollbackUnlessCommittedAsync",BindingFlags.Static|BindingFlags.NonPublic);Assert.NotNull(method);await (Task)method.Invoke(null,[null,true,CancellationToken.None])!;}
 [Fact] public async Task Default_profile_never_wakes_a_worker_or_scans_backlog()
 {var q=Queue(Profile(),Approval());using var c=new CancellationTokenSource(100);await using var items=q.ReadAllAsync(c.Token).GetAsyncEnumerator();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>items.MoveNextAsync().AsTask());}
 [Fact] public async Task Explicit_resume_seeds_exactly_the_original_command_once_with_same_unit_approval()
 {var p=Profile();Resume(p,Command);var a=Approval();var q=Queue(p,a);using var c=new CancellationTokenSource();await using var items=q.ReadAllAsync(c.Token).GetAsyncEnumerator();Assert.True(await items.MoveNextAsync());Assert.Equal(Command,items.Current.CommandId);Assert.Same(a,items.Current.Approval);c.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>items.MoveNextAsync().AsTask());}
 [Theory][InlineData("empty")][InlineData("no_approval")][InlineData("not_homologated")][InlineData("not_authorized")][InlineData("wrong_contract")][InlineData("wrong_item")][InlineData("wrong_sid")][InlineData("rest_only")][InlineData("generic_retry")]
 public void Recovery_cannot_enable_an_empty_unapproved_or_unrelated_command_scope(string fault)
 {var p=Profile();Resume(p,fault=="empty"?Guid.Empty:Command);var a=Approval();a=fault switch{"not_homologated"=>a with{Homologated=false},"not_authorized"=>a with{ExecutionAuthorized=false},"wrong_contract"=>a with{Contract="OTHER"},"wrong_item"=>a with{Item="10"},"wrong_sid"=>a with{OperatorSid="S-1-5-21-9-8-7-1001"},"rest_only"=>a with{AllowedStage=AcmRetryStage.RestOnly},"generic_retry"=>a with{RecoverLatestPending=false},_=>a};Assert.Throws<InvalidOperationException>(()=>Queue(p,fault=="no_approval"?[]:[a]));}
}
