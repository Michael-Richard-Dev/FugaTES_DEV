using System.Data;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Persistencia.MotorIntegracao;
using Npgsql;
namespace FugaTES.Persistencia.ManualRetry;
public sealed partial class AcmManualRetryPostgreSql
{
 // Atomic local adoption of a freshly verified manual external creation. Never claims work,
 // changes a source payload/attempt, composes a gateway or grants future dispatch authority.
 public async Task<AcmExternalContractReconciliationResult> ReconcileExternalContractAsync(AcmExternalContractReconciliationProof proof,IAcmR57NormalPreparacao sourcePreparation,CancellationToken ct)
 {
  if(proof.OperationId==Guid.Empty||proof.IntegrationId==Guid.Empty||proof.Desired is null)throw new InvalidOperationException("EXTERNAL_RECONCILIATION_PROOF_REQUIRED");
  await using var c=await source.OpenConnectionAsync(ct);await AcmPostgreSqlIdentityGuard.VerifyAsync(c,ct);
  await using var tx=await c.BeginTransactionAsync(IsolationLevel.Serializable,ct);var committed=false;
  try
  {
   await Settings(c,tx,true,ct);await LockVersions(c,tx,proof.IntegrationId,proof.SapCode,ct);
   var before=await Snapshot(c,tx,proof.IntegrationId,ct)??throw new InvalidOperationException("SOURCE_NOT_FOUND");
   // One immutable audit ID makes a lost COMMIT acknowledgement inspectable without a new mutation.
   await using(var prior=Cmd(c,tx,"select integracao_id,evento,diagnostico from fugates.auditoria_integracao where id=@operation",proof.IntegrationId))
   {
    prior.Parameters.AddWithValue("operation",proof.OperationId);await using var r=await prior.ExecuteReaderAsync(ct);
    if(await r.ReadAsync(ct))
    {
     using var d=JsonDocument.Parse(r.GetString(2));var a=d.RootElement;
     if(r.GetGuid(0)!=proof.IntegrationId||r.GetString(1)!="ACM_EXTERNAL_CONTRACT_RECONCILED"||a.GetProperty("GuardianCode").GetString()!=proof.ExternalCode||a.GetProperty("Sequence").GetInt64()!=proof.Sequence||a.GetProperty("Fingerprint").GetString()!=proof.Fingerprint||!before.IdentityVerified||!before.LatestVersion||before.Version!=proof.Version||before.Fingerprint!=proof.Fingerprint||before.CanonicalFingerprint!=proof.Fingerprint||before.GuardianConfirmedCode!=proof.ExternalCode||before.Metadata!=proof.Metadata()||before.State!="AGUARDANDO_ACAO_MANUAL")throw new InvalidOperationException("EXTERNAL_RECONCILIATION_AUDIT_CONFLICT");
     await r.DisposeAsync();await tx.CommitAsync(ct);committed=true;return new("ALREADY_RECONCILED",proof.IntegrationId,proof.SapCode,proof.ExternalCode,proof.Sequence,proof.OperationId,before.AttemptCount);
    }
   }
   // Rebuild from the locked immutable H4 payload and the trusted server preparation,
   // not from either of the externally supplied comparison documents.
   VersaoContratoSapToledo sourceVersion;
   await using(var sourceRow=Cmd(c,tx,"select numero_contrato,versao,fingerprint_sha256,payload_contrato::text,criado_em from fugates.contrato_sap_toledo_versao where integracao_id=@id",proof.IntegrationId))
   {
    await using var read=await sourceRow.ExecuteReaderAsync(ct);if(!await read.ReadAsync(ct))throw new InvalidOperationException("LOCKED_SOURCE_MISSING");
    sourceVersion=new(ResultadoRegistroVersaoContrato.JaExistente,read.GetString(0),read.GetInt64(1),read.GetString(2),proof.IntegrationId,JsonSerializer.Deserialize<ContratoSapToledoSnapshot>(read.GetString(3))!){RecebidoEmUtc=read.GetFieldValue<DateTimeOffset>(4)};
   }
   var sourcePrepared=sourcePreparation.PrepararContrato(sourceVersion);
   if(!AcmExternalContractReconciliationPolicy.CanReconcile(before,proof,sourcePrepared,await ServerNow(c,tx,ct)))throw new InvalidOperationException("EXTERNAL_RECONCILIATION_STATE_OR_PROOF_CHANGED");
   var reference=proof.Desired.NumeroReferencia;
   await using(var advisory=Cmd(c,tx,"select pg_advisory_xact_lock(hashtextextended(@reference,0))",proof.IntegrationId))
   {advisory.Parameters.AddWithValue("reference",reference);await advisory.ExecuteNonQueryAsync(ct);}
   await using(var collision=Cmd(c,tx,"select exists(select 1 from fugates.contrato_sap_toledo_versao where integracao_id<>@id and (guardian_numero_referencia=@reference or guardian_codigo_solicitado=@code or guardian_codigo_confirmado=@code))",proof.IntegrationId))
   {collision.Parameters.AddWithValue("reference",reference);collision.Parameters.AddWithValue("code",proof.ExternalCode);if(await collision.ExecuteScalarAsync(ct) is true)throw new InvalidOperationException("EXTERNAL_RECONCILIATION_IDENTITY_COLLISION");}
   await using(var h4=Cmd(c,tx,"""
    update fugates.contrato_sap_toledo_versao set guardian_numero_referencia=@reference,guardian_codigo_solicitado=@code,guardian_codigo_confirmado=@code
    where integracao_id=@id and numero_contrato=@contract and versao=@version and fingerprint_sha256=@fingerprint
      and guardian_numero_referencia is null and guardian_codigo_solicitado is null and guardian_codigo_confirmado is null
      and guardian_protocolo_id is null and guardian_callback_id is null;
    """,proof.IntegrationId))
   {h4.Parameters.AddWithValue("reference",reference);h4.Parameters.AddWithValue("code",proof.ExternalCode);h4.Parameters.AddWithValue("contract",proof.SapCode);h4.Parameters.AddWithValue("version",proof.Version);h4.Parameters.AddWithValue("fingerprint",proof.Fingerprint);if(await h4.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("H4_RECONCILIATION_CONFLICT");}
   await using(var journal=Cmd(c,tx,"""
    update fugates.projecao_resultado_fluxo_mvp set status_funcional='AGUARDANDO_DEPENDENCIA_EXTERNA',etapa_funcional='R165U_PROTOCOL_BLOCKED',
      resultado_funcional=@metadata,erro_codigo='ACM_EXTERNAL_CONTRACT_RECONCILED_PROTOCOL_PENDING',erro_categoria=null,erro_etapa='R165U_PROTOCOL_BLOCKED',
      erro_reprocessavel=false,erro_resultado_indeterminado=false,atualizado_em=clock_timestamp()
    where integracao_id=@id and etapa_funcional='R165U_PRE_SEND_BLOCKED' and erro_codigo=@oldError and not erro_resultado_indeterminado;
    """,proof.IntegrationId))
   {journal.Parameters.AddWithValue("metadata",proof.Metadata());journal.Parameters.AddWithValue("oldError",before.ErrorCode!);if(await journal.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("JOURNAL_RECONCILIATION_CONFLICT");}
   await using(var integration=Cmd(c,tx,"""
    update fugates.integracao set estado='AGUARDANDO_ACAO_MANUAL',proxima_tentativa_em=null,tipo_disparo_pendente=null,atualizado_em=clock_timestamp()
    where id=@id and estado=@state and fase_envio='RESULTADO_RECEBIDO' and fencing_token=@fence and tentativa_atual_id=@attempt
      and worker_proprietario is null and lease_expira_em is null;
    """,proof.IntegrationId))
   {integration.Parameters.AddWithValue("state",before.State);integration.Parameters.AddWithValue("fence",before.Fence);integration.Parameters.AddWithValue("attempt",before.AttemptId!.Value);if(await integration.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("CONCURRENT_RECONCILIATION_CLAIM");}
   var diagnostic=JsonSerializer.Serialize(new{OperationId=proof.OperationId,At=await ServerNow(c,tx,ct),proof.OperatorSid,proof.AuthorizationReference,proof.IdentifierMappingEvidence,proof.BackupReference,
    SapCode=proof.SapCode,GuardianCode=proof.ExternalCode,proof.Sequence,proof.Fingerprint,proof.SoapResponseHash,proof.ObservedUtc,MappingHash=proof.MappingHash(),Before=before,
    ExternalBusinessPosts=0,SourcePayloadChanged=false,AttemptHistoryChanged=false,ProtocolAuthorized=false});
   await using(var audit=Cmd(c,tx,"""
    insert into fugates.auditoria_integracao(id,integracao_id,evento,criada_em,worker,fencing_token,tentativa_id,correlation_id,diagnostico)
    values(@operation,@id,'ACM_EXTERNAL_CONTRACT_RECONCILED',clock_timestamp(),@operator,@fence,@attempt,@correlation,@diagnostic);
    """,proof.IntegrationId))
   {audit.Parameters.AddWithValue("operation",proof.OperationId);audit.Parameters.AddWithValue("operator",proof.OperatorSid);audit.Parameters.AddWithValue("fence",before.Fence);audit.Parameters.AddWithValue("attempt",before.AttemptId!.Value);audit.Parameters.AddWithValue("correlation",before.CorrelationId);audit.Parameters.AddWithValue("diagnostic",diagnostic);if(await audit.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("RECONCILIATION_AUDIT_NOT_DURABLE");}
   await tx.CommitAsync(ct);committed=true;
   return new("RECONCILED_PROTOCOL_PENDING",proof.IntegrationId,proof.SapCode,proof.ExternalCode,proof.Sequence,proof.OperationId,before.AttemptCount);
  }
  finally{await RollbackUnlessCommittedAsync(tx,committed,CancellationToken.None);}
 }
}
