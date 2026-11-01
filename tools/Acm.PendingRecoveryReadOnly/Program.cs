using System.Reflection;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using System.Text.Json;
using FugaTES.Callback.Http.ManualRetry;
using FugaTES.Persistencia.ManualRetry;
using FugaTES.Persistencia.MotorIntegracao;
using Npgsql;

// Technical precheck, deliberately contains no command/worker/service/Guardian composition.
var lifecycle=args.Length==5&&args[4]=="--transaction-lifecycle-only";
var verifyFinalizer=args.Length==5&&args[4]=="--verify-transaction-finalizer";
var diagnose=args.Length==5&&args[4]=="--diagnose-queued";
if((args.Length!=4&&!lifecycle&&!verifyFinalizer&&!diagnose)||args[0]!="--read-only-profile"||args[2]!="--integration"||!Guid.TryParse(args[3],out var id)||id==Guid.Empty)
{Console.WriteLine("{\"result\":\"BLOCKED_ARGUMENTS\"}");return 2;}
if(!OperatingSystem.IsLinux()||System.Net.Dns.GetHostName()!="vm-fugatesbio")
{Console.WriteLine("{\"result\":\"BLOCKED_HOST\"}");return 2;}
try
{
 var profile=JsonSerializer.Deserialize<AcmManualRetryCommandProfile>(PrivateCommandFile.Read(args[1],16384),AcmManualRetryCommandProfile.Json)
  ??throw new InvalidOperationException();
 PrivateCommandFile.ValidateMetadata(profile.Passfile,65536);
 var connection=new NpgsqlConnectionStringBuilder{Host="127.0.0.1",Port=5433,Database="fugates_dev",SearchPath="fugates",
  Username=NpgsqlDataSourceFactory.AcmApplicationRole,Passfile=profile.Passfile,Enlist=false,IncludeErrorDetail=false,
  Options="-c default_transaction_read_only=on",Timeout=5,CommandTimeout=8,ApplicationName="FugaTES.PendingRecovery.ReadOnly"};
 await using var source=NpgsqlDataSource.Create(connection.ConnectionString);
 using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
 if(lifecycle)
 {
  await using var c=await source.OpenConnectionAsync(timeout.Token);
  await AcmPostgreSqlIdentityGuard.VerifyAsync(c,timeout.Token);
  await using var tx=await c.BeginTransactionAsync(timeout.Token);
  await using(var query=new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='5s'; SELECT 1",c,tx)){await query.ExecuteScalarAsync(timeout.Token);}
  await tx.CommitAsync(timeout.Token);
  var connectionRemains=tx.Connection is not null;string? rollbackError=null;
  if(connectionRemains){try{await tx.RollbackAsync(timeout.Token);}catch(Exception ex){rollbackError=ex.GetType().Name;}}
  Console.WriteLine(JsonSerializer.Serialize(new{result="READ_ONLY_TRANSACTION_LIFECYCLE",readOnly=true,committedOnlySelect=true,connectionNonNullAfterCommit=connectionRemains,rollbackAfterCommitErrorType=rollbackError,businessWrites=0,externalPosts=0}));
  return 0;
 }
 if(verifyFinalizer)
 {
  var helper=typeof(AcmManualRetryPostgreSql).GetMethod("RollbackUnlessCommittedAsync",BindingFlags.NonPublic|BindingFlags.Static)??throw new InvalidOperationException("COMPLETION_GUARD_MISSING");
  await using var c=await source.OpenConnectionAsync(timeout.Token);await AcmPostgreSqlIdentityGuard.VerifyAsync(c,timeout.Token);
  await using(var tx=await c.BeginTransactionAsync(timeout.Token))
  {
   await using var q=new NpgsqlCommand("SET TRANSACTION READ ONLY; SELECT current_setting('transaction_read_only')",c,tx);
   if(await q.ExecuteScalarAsync(timeout.Token) is not string state||state!="on")throw new InvalidOperationException("READ_ONLY_REQUIRED");
   await tx.CommitAsync(timeout.Token);await (Task)helper.Invoke(null,[tx,true,timeout.Token])!;
  }
  await using(var tx=await c.BeginTransactionAsync(timeout.Token))
  {
   await using var q=new NpgsqlCommand("SET TRANSACTION READ ONLY; SELECT current_setting('transaction_read_only')",c,tx);
   if(await q.ExecuteScalarAsync(timeout.Token) is not string state||state!="on")throw new InvalidOperationException("READ_ONLY_REQUIRED");
   await (Task)helper.Invoke(null,[tx,false,timeout.Token])!;
  }
  Console.WriteLine(JsonSerializer.Serialize(new{result="READ_ONLY_TRANSACTION_FINALIZER_PASS",acknowledgedCommitNotRolledBack=true,uncommittedReadOnlyTransactionRolledBack=true,productionHelperInvoked=true,businessWrites=0,externalPosts=0}));return 0;
 }
 var s=await new AcmManualRetryPostgreSql(source).ReadAsync(id,timeout.Token);
 if(s is null||s.IntegrationId!=id||!s.IdentityVerified||s.Fingerprint!=s.CanonicalFingerprint)
 {Console.WriteLine("{\"result\":\"BLOCKED_IDENTITY\"}");return 1;}
 if(diagnose)
 {
  await using var c=await source.OpenConnectionAsync(timeout.Token);await AcmPostgreSqlIdentityGuard.VerifyAsync(c,timeout.Token);
  await using var tx=await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,timeout.Token);
  await using(var settings=new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='8s'",c,tx)){await settings.ExecuteNonQueryAsync(timeout.Token);}
  AcmManualRetryPostgreSql.Audit audit;Guid command;
  await using(var query=new NpgsqlCommand("select id,diagnostico from fugates.auditoria_integracao where integracao_id=@id and evento='ACM_MANUAL_RETRY_REQUESTED' order by criada_em limit 2",c,tx))
  {
   query.Parameters.AddWithValue("id",id);await using var read=await query.ExecuteReaderAsync(timeout.Token);
   if(!await read.ReadAsync(timeout.Token))throw new InvalidOperationException("DURABLE_COMMAND_MISSING");
   command=read.GetGuid(0);audit=JsonSerializer.Deserialize<AcmManualRetryPostgreSql.Audit>(read.GetString(1),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
   if(await read.ReadAsync(timeout.Token))throw new InvalidOperationException("DURABLE_COMMAND_AMBIGUOUS");
  }
  var approvals=JsonSerializer.Deserialize<AcmRetryApproval[]>(PrivateCommandFile.Read(profile.ApprovalsFile,262144),AcmManualRetryCommandProfile.Json)!;profile.Validate(approvals);var a=approvals.Single();
  var before=AcmManualRetryPolicy.Evaluate(audit.Before,a,audit.Actor,audit.At);
  var matches=typeof(AcmManualRetryPostgreSql).GetMethod("PendingStartMatches",BindingFlags.NonPublic|BindingFlags.Static)!;
  long started;await using(var query=new NpgsqlCommand("select count(*) from fugates.auditoria_integracao where integracao_id=@id and evento='ACM_MANUAL_RETRY_STARTED'",c,tx)){query.Parameters.AddWithValue("id",id);started=(long)(await query.ExecuteScalarAsync(timeout.Token))!;}
  await tx.RollbackAsync(timeout.Token);
  Console.WriteLine(JsonSerializer.Serialize(new{result="QUEUED_READ_ONLY_DIAGNOSIS",commandId=command,integrationId=id,version=s.Version,state=s.State,attempts=s.AttemptCount,startedEvents=started,
   originalSnapshotDigestVerified=audit.Before.Digest()==audit.Request.SnapshotDigest,nominalSnapshotDigestVerified=a.ObservedSnapshotDigest==audit.Request.SnapshotDigest,
   authorizationReferenceVerified=audit.AuthorizationReference==a.AuthorizationReference,operatorVerified=audit.Actor.Sid==a.OperatorSid,
   eligibleAtRecordedAdmission=before.Eligible,eligibilityCodeAtAdmission=before.Code,evaluatedAtRecordedAdmissionOnly=true,
   predecessorAndTargetStartFactsVerified=(bool)matches.Invoke(null,[s,audit.Before])!,
   authorizationCurrentlyOpen=AcmManualRetryPolicy.AuthorizationOpen(a,id,audit.Actor,DateTimeOffset.UtcNow),
   fingerprintVerified=s.Fingerprint==a.Fingerprint&&s.CanonicalFingerprint==s.Fingerprint,correlationVerified=s.CorrelationId==audit.Before.CorrelationId,
   hasExternalEffect=s.HasProtocolOrCallback||s.ContractHasExternal||s.GuardianRequestedCode is not null||s.GuardianConfirmedCode is not null||s.Reference is not null,
   hasUnsafeAttempt=s.UnsafeAttempt||s.Indeterminate,workerAbsent=s.Worker is null&&s.LeaseUntil is null,businessWrites=0,externalPosts=0}));return 0;
 }
 // Emit only typed technical facts, never raw H4/JSON, names, credentials or opaque diagnostics.
 // This gate rejects a prepared/armed/remote-bearing journal before serializing any metadata.
 if(s.State!="PENDENTE"||s.Phase!="NAO_INICIADO"||s.Fence!=0||s.AttemptCount!=0||s.AttemptId is not null||
  s.JournalPresent||s.Stage is not null||s.Metadata is not null||s.Synthetic||s.Indeterminate||s.UnsafeAttempt||s.HasPendingCommand||
  !s.LatestVersion||s.Worker is not null||s.LeaseUntil is not null||s.HasProtocolOrCallback||s.ContractHasExternal||
  s.GuardianRequestedCode is not null||s.GuardianConfirmedCode is not null||s.Reference is not null||s.ReceivedUtc is null||
  s.Predecessors.Length!=s.Version-1||s.Predecessors.Any(p=>!p.SafeToSupersede()))
 {Console.WriteLine("{\"result\":\"BLOCKED_PENDING_SNAPSHOT\",\"businessWrites\":0}");return 1;}
 Console.WriteLine(JsonSerializer.Serialize(new{result="READ_ONLY_SNAPSHOT_VERIFIED",snapshotDigest=s.Digest(),snapshot=s,
  database="fugates_dev",port=5433,role=NpgsqlDataSourceFactory.AcmApplicationRole,readOnly=true,businessWrites=0,externalPosts=0,
  authorizationCreated=false,workerStarted=false}));
 return 0;
}
catch(Exception e)
{Console.WriteLine(JsonSerializer.Serialize(new{result="BLOCKED_READ_ONLY_PROBE",errorType=e.GetType().Name,sqlState=(e as PostgresException)?.SqlState,businessWrites=0}));return 1;}
