using System.Text.Json;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Callback.Http.ManualRetry;
using FugaTES.Persistencia.ManualRetry;
using FugaTES.Persistencia.MotorIntegracao;
using Npgsql;

// Explicit, one-command audit maintenance. Never composes a host, motor, queue,
// SOAP/REST client or any business transport. --verify-only is read-only.
if(args.Length!=5||args[0]!="--profile"||args[2]!="--command"||!Guid.TryParse(args[3],out var command)||command==Guid.Empty||
 args[4] is not("--inspect-only" or "--verify-only" or "--reconcile"))
{Console.WriteLine("{\"result\":\"BLOCKED_ARGUMENTS\"}");return 2;}
if(!OperatingSystem.IsLinux()||System.Net.Dns.GetHostName()!="vm-fugatesbio")
{Console.WriteLine("{\"result\":\"BLOCKED_HOST\"}");return 2;}
try
{
 var profile=JsonSerializer.Deserialize<AcmManualRetryCommandProfile>(PrivateCommandFile.Read(args[1],16384),AcmManualRetryCommandProfile.Json)??throw new InvalidOperationException();
 var approvals=JsonSerializer.Deserialize<AcmRetryApproval[]>(PrivateCommandFile.Read(profile.ApprovalsFile,262144),AcmManualRetryCommandProfile.Json)??throw new InvalidOperationException();
 profile.Validate(approvals);
 var inspect=args[4]=="--inspect-only";
 AcmRetryApproval? approval=inspect?null:approvals.Single();
 if(!inspect&&(profile.ResumeCommittedCommandId is not null||approval!.RecoverLatestPending||approval.AllowedStage!=AcmRetryStage.SoapOnly))throw new InvalidOperationException("INERT_NOMINAL_RECONCILIATION_PROFILE_REQUIRED");
 PrivateCommandFile.ValidateMetadata(profile.Passfile,65536);
 var cs=new NpgsqlConnectionStringBuilder{Host="127.0.0.1",Port=5433,Database="fugates_dev",SearchPath="fugates",Username=NpgsqlDataSourceFactory.AcmApplicationRole,
  Passfile=profile.Passfile,Enlist=false,IncludeErrorDetail=false,Timeout=5,CommandTimeout=8,Options="-c default_transaction_read_only=on",ApplicationName="FugaTES.ManualRetry.PreSendReconcile"};
 await using var source=NpgsqlDataSource.Create(cs.ConnectionString);using var bounded=new CancellationTokenSource(TimeSpan.FromSeconds(30));var ct=bounded.Token;
 var store=new AcmManualRetryPostgreSql(source);
 AcmManualRetryPostgreSql.Audit audit;
 await using(var c=await source.OpenConnectionAsync(ct))
 {
  await AcmPostgreSqlIdentityGuard.VerifyAsync(c,ct);await using var tx=await c.BeginTransactionAsync(ct);
  await using(var settings=new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='8s'",c,tx)){await settings.ExecuteNonQueryAsync(ct);}
  await using(var q=new NpgsqlCommand("select diagnostico from fugates.auditoria_integracao where id=@command and evento='ACM_MANUAL_RETRY_REQUESTED'",c,tx))
  {q.Parameters.AddWithValue("command",command);audit=JsonSerializer.Deserialize<AcmManualRetryPostgreSql.Audit>((string)(await q.ExecuteScalarAsync(ct)??throw new InvalidOperationException("ORIGINAL_COMMAND_NOT_FOUND")),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;}
  await tx.RollbackAsync(ct);
 }
 var actor=new AcmRetryActor(true,profile.OperatorSid);
 if(audit.Request.CommandId!=command||audit.Actor!=actor||profile.Contract is null||audit.Before.Contract!=profile.Contract||audit.Before.Item!=profile.Item||
  audit.Before.Digest()!=audit.Request.SnapshotDigest)throw new InvalidOperationException("ORIGINAL_COMMAND_PROFILE_SCOPE_MISMATCH");
 var before=await store.ReadAsync(audit.Before.IntegrationId,ct)??throw new InvalidOperationException("TARGET_NOT_FOUND");
 if(before.Contract!=profile.Contract||before.Item!=profile.Item||!AcmManualRetryPolicy.IsKnownPreSendFailure(before))throw new InvalidOperationException("KNOWN_PRE_SEND_SNAPSHOT_REQUIRED");
 if(inspect)
 {
  Console.WriteLine(JsonSerializer.Serialize(new{result="KNOWN_PRE_SEND_INSPECTED_READ_ONLY",commandId=command,integrationId=before.IntegrationId,contract=before.Contract,
   version=before.Version,receivedUtc=before.ReceivedUtc,fingerprint=before.Fingerprint,state=before.State,snapshotDigest=before.Digest(),attempts=before.AttemptCount,
   fence=before.Fence,stage=before.Stage,error=before.ErrorCode,hasPendingCommand=before.HasPendingCommand,businessPosts=0,databaseWrites=0}));return 0;
 }
 if(approval is null||!AcmManualRetryPostgreSql.CanReconcileKnownPreSend(audit,before,approval,actor,DateTimeOffset.UtcNow))throw new InvalidOperationException("PRE_SEND_PROOF_OR_CURRENT_AUTHORIZATION_REQUIRED");
 if(args[4]=="--verify-only")
 {
  Console.WriteLine(JsonSerializer.Serialize(new{result="NOMINAL_PRE_SEND_RECONCILIATION_VERIFIED_READ_ONLY",commandId=command,integrationId=before.IntegrationId,version=before.Version,state=before.State,
   snapshotDigest=before.Digest(),attempts=before.AttemptCount,fence=before.Fence,stage=before.Stage,error=before.ErrorCode,businessPosts=0,databaseWrites=0}));return 0;
 }
 await store.ReconcileKnownPreSendAsync(command,approval,actor,DateTimeOffset.UtcNow,ct);
 var after=await store.ReadAsync(approval.IntegrationId,ct)??throw new InvalidOperationException("READBACK_MISSING_NO_RETRY");
 if(after.State!="AGUARDANDO_ACAO_MANUAL"||after.HasPendingCommand||after.Fence!=before.Fence||after.AttemptId!=before.AttemptId||
  after.AttemptCount!=before.AttemptCount||after.Fingerprint!=before.Fingerprint||after.Stage!=before.Stage||after.ErrorCode!=before.ErrorCode)
  throw new InvalidOperationException("RECONCILIATION_READBACK_UNVERIFIED_NO_RETRY");
 Console.WriteLine(JsonSerializer.Serialize(new{result="KNOWN_PRE_SEND_RECONCILED",commandId=command,integrationId=after.IntegrationId,version=after.Version,state=after.State,
  snapshotDigest=after.Digest(),attempts=after.AttemptCount,fence=after.Fence,originalAttemptPreserved=true,originalPayloadPreserved=true,businessPosts=0,externalDispatch=0}));return 0;
}
catch(Exception error)
{Console.WriteLine(JsonSerializer.Serialize(new{result="BLOCKED_RECONCILIATION_NO_RETRY",errorType=error.GetType().Name,errorCode=System.Text.RegularExpressions.Regex.IsMatch(error.Message,@"\A[A-Z][A-Z0-9_]{2,100}\z")?error.Message:null,sqlState=(error as PostgresException)?.SqlState}));return 1;}
