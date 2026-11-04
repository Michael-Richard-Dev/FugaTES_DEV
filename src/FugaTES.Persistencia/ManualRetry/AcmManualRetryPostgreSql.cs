using System.Data;
using System.Text.Json;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Persistencia.MotorIntegracao;
using Npgsql;
using NpgsqlTypes;

namespace FugaTES.Persistencia.ManualRetry;

// Candidate only. Not registered in the GET-only DEV API or any running host.
// Existing tables, parameterized commands, one transaction for admission/audit/queue.
public sealed partial class AcmManualRetryPostgreSql(NpgsqlDataSource source):IAcmManualRetryStore
{
    const string Requested="ACM_MANUAL_RETRY_REQUESTED",Started="ACM_MANUAL_RETRY_STARTED",Finished="ACM_MANUAL_RETRY_FINISHED";
    static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true};
    // Npgsql keeps Connection non-null after COMMIT. Never rollback an acknowledged commit.
    static Task RollbackUnlessCommittedAsync(NpgsqlTransaction tx,bool committed,CancellationToken ct)
        => !committed&&tx.Connection is not null?tx.RollbackAsync(ct):Task.CompletedTask;
    public sealed record Audit(AcmRetryRequest Request,AcmRetryActor Actor,string AuthorizationReference,DateTimeOffset At,AcmRetrySnapshot Before,AcmRetryStage ResumeStage,string? ResumeMetadata,AcmRetryApproval Approval);
    static NpgsqlCommand Cmd(NpgsqlConnection c,NpgsqlTransaction tx,string sql,Guid id)
    {var cmd=new NpgsqlCommand(sql,c,tx){CommandTimeout=10};cmd.Parameters.AddWithValue("id",id);return cmd;}
    static async Task Settings(NpgsqlConnection c,NpgsqlTransaction tx,bool write,CancellationToken ct)
    {await using var cmd=new NpgsqlCommand((write?"set transaction read write;":"set transaction read only;")+"set local lock_timeout='2s';set local statement_timeout='10s';set local timezone='UTC';",c,tx);await cmd.ExecuteNonQueryAsync(ct);}
    static async Task<DateTimeOffset> ServerNow(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken ct)
    {await using var cmd=new NpgsqlCommand("select clock_timestamp()",c,tx);await using var r=await cmd.ExecuteReaderAsync(ct);await r.ReadAsync(ct);return r.GetFieldValue<DateTimeOffset>(0);}
    static async Task Lock(NpgsqlConnection c,NpgsqlTransaction tx,Guid id,CancellationToken ct)
    {
        // Same lock order as motor/journal. SERIALIZABLE also protects absent dependent rows.
        foreach(var sql in new[]{"select id from fugates.integracao where id=@id for update",
            "select integracao_id from fugates.contrato_sap_toledo_versao where integracao_id=@id for update",
            "select integracao_id from fugates.projecao_resultado_fluxo_mvp where integracao_id=@id for update",
            "select id from fugates.tentativa_integracao where integracao_id=@id order by fencing_token for update"})
        {await using var cmd=Cmd(c,tx,sql,id);await cmd.ExecuteNonQueryAsync(ct);}
    }
    public async Task<AcmRetrySnapshot?> ReadAsync(Guid id,CancellationToken ct)
    {
        await using var c=await source.OpenConnectionAsync(ct);await AcmPostgreSqlIdentityGuard.VerifyAsync(c,ct);
        await using var tx=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        try{await Settings(c,tx,false,ct);return await Snapshot(c,tx,id,ct);}
        finally{await tx.RollbackAsync(CancellationToken.None);}
    }
    public async Task<Guid?> PendingCommandAsync(AcmRetryApproval approval,CancellationToken ct)
    {
        if(!approval.Homologated||!approval.ExecutionAuthorized||!approval.ExclusiveWorker)return null;
        await using var c=await source.OpenConnectionAsync(ct);await AcmPostgreSqlIdentityGuard.VerifyAsync(c,ct);
        await using var tx=await c.BeginTransactionAsync(ct);
        try
        {
            await Settings(c,tx,false,ct);
            await using var cmd=Cmd(c,tx,"""
                select a.id from fugates.auditoria_integracao a where a.integracao_id=@id
                 and a.evento='ACM_MANUAL_RETRY_REQUESTED' and (case when a.evento in ('ACM_MANUAL_RETRY_REQUESTED','ACM_MANUAL_RETRY_STARTED','ACM_MANUAL_RETRY_FINISHED','ACM_MANUAL_RETRY_UNKNOWN') then a.diagnostico::jsonb end)->>'AuthorizationReference'=@authorization
                 and (case when a.evento in ('ACM_MANUAL_RETRY_REQUESTED','ACM_MANUAL_RETRY_STARTED','ACM_MANUAL_RETRY_FINISHED','ACM_MANUAL_RETRY_UNKNOWN') then a.diagnostico::jsonb end)->'Request'->>'SnapshotDigest'=@digest
                 and not exists(select 1 from fugates.auditoria_integracao s where s.integracao_id=a.integracao_id
                  and s.evento in ('ACM_MANUAL_RETRY_STARTED','ACM_MANUAL_RETRY_FINISHED') and (case when s.evento in ('ACM_MANUAL_RETRY_REQUESTED','ACM_MANUAL_RETRY_STARTED','ACM_MANUAL_RETRY_FINISHED','ACM_MANUAL_RETRY_UNKNOWN') then s.diagnostico::jsonb end)->>'CommandId'=a.id::text)
                 order by a.criada_em,a.id limit 2
                """,approval.IntegrationId);
            cmd.Parameters.AddWithValue("authorization",approval.AuthorizationReference);cmd.Parameters.AddWithValue("digest",approval.ObservedSnapshotDigest);
            await using var reader=await cmd.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))return null;var id=reader.GetGuid(0);
            if(await reader.ReadAsync(ct))throw new InvalidOperationException("ACM_RETRY_COMMAND_AMBIGUOUS");return id;
        }
        finally{await tx.RollbackAsync(CancellationToken.None);}
    }
    static async Task<AcmRetrySnapshot?> Snapshot(NpgsqlConnection c,NpgsqlTransaction tx,Guid id,CancellationToken ct)
    {
        const string sql="""
          select jsonb_build_object('Payload',v.payload_contrato,'Facts',jsonb_build_object(
           'IntegrationId',i.id,'Contract',v.numero_contrato,'Item',v.payload_contrato->>'Item','Version',v.versao,
           'Fingerprint',i.fingerprint_sha256,'CorrelationId',i.correlation_id,'State',i.estado,'Fence',i.fencing_token,
           'LatestVersion',v.versao=(select ultima_versao from fugates.contrato_sap_toledo_identidade where numero_contrato=v.numero_contrato),
           'ReceivedUtc',v.criado_em,'Phase',i.fase_envio,
           'AttemptCount',(select count(*) from fugates.tentativa_integracao a where a.integracao_id=i.id),
           'JournalPresent',p.integracao_id is not null,
           'ContractHasExternal',exists(select 1 from fugates.correlacao_guardian_sap g where ltrim(g.documento_logistico_referencia,'0')=ltrim(i.chave_negocio,'0')),
           'IdentityVerified',i.origem='SAP' and i.destino='TOLEDO' and i.operacao='SAP_CONTRATO_TOLEDO'
             and v.numero_contrato=i.chave_negocio and v.fingerprint_sha256=i.fingerprint_sha256,
           'Synthetic',i.id='e502b0a0-57c5-49f3-976c-9e1fe7e355e3'::uuid or ltrim(v.numero_contrato,'0')='986801'
             or i.correlation_id ilike 'r165al%' or i.correlation_id ilike 'r165ai%'
             or coalesce(p.resultado_funcional,'') ilike '%SIMULAD%' or coalesce(p.fluxo,'') ilike '%SIMULAD%',
           'AttemptId',i.tentativa_atual_id,'AttemptOutcome',t.desfecho,'Worker',i.worker_proprietario,'LeaseUntil',i.lease_expira_em,
           'UnsafeAttempt',exists(select 1 from fugates.tentativa_integracao a where a.integracao_id=i.id and a.desfecho in ('EM_EXECUCAO','INDETERMINADA','INTERROMPIDA')),
           'HasPendingCommand',exists(select 1 from fugates.auditoria_integracao a where a.integracao_id=i.id and a.evento='ACM_MANUAL_RETRY_REQUESTED'
             and not exists(select 1 from fugates.auditoria_integracao f where f.integracao_id=i.id and f.evento='ACM_MANUAL_RETRY_FINISHED'
               and (case when f.evento in ('ACM_MANUAL_RETRY_REQUESTED','ACM_MANUAL_RETRY_STARTED','ACM_MANUAL_RETRY_FINISHED','ACM_MANUAL_RETRY_UNKNOWN') then f.diagnostico::jsonb end)->>'CommandId'=a.id::text)),
           'JournalStatus',p.status_funcional,'Stage',p.etapa_funcional,'Metadata',p.resultado_funcional,
           'ErrorCode',p.erro_codigo,'ErrorCategory',p.erro_categoria,'RetryableFailure',coalesce(p.erro_reprocessavel,false),
           'Indeterminate',case when p.integracao_id is null and not exists(select 1 from fugates.tentativa_integracao a where a.integracao_id=i.id) then false else coalesce(p.erro_resultado_indeterminado,true) end,
           'JournalUpdatedUtc',coalesce(p.atualizado_em,i.atualizado_em),
           'GuardianRequestedCode',v.guardian_codigo_solicitado,'GuardianConfirmedCode',v.guardian_codigo_confirmado,
           'Reference',v.guardian_numero_referencia,
           'HasProtocolOrCallback',v.guardian_protocolo_id is not null or v.guardian_numero_ticket is not null
             or v.guardian_callback_id is not null or v.guardian_callback_sequencial is not null
             or p.protocolo_guardian is not null or p.numero_ticket_guardian is not null
             or exists(select 1 from fugates.correlacao_guardian_evento e where e.integracao_id=i.id)
             or exists(select 1 from fugates.correlacao_guardian_sap g where g.documento_logistico_referencia=i.chave_negocio)
          ))::text
          from fugates.integracao i join fugates.contrato_sap_toledo_versao v on v.integracao_id=i.id
          left join fugates.projecao_resultado_fluxo_mvp p on p.integracao_id=i.id
          left join fugates.tentativa_integracao t on t.id=i.tentativa_atual_id and t.integracao_id=i.id
          where i.id=@id
          """;
        await using var cmd=Cmd(c,tx,sql,id);await using var r=await cmd.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct))return null;
        using var doc=JsonDocument.Parse(r.GetString(0));var facts=doc.RootElement.GetProperty("Facts").Deserialize<AcmRetrySnapshot>(Json)!;
        var payload=doc.RootElement.GetProperty("Payload").Deserialize<ContratoSapToledoSnapshot>(Json)!;
        if(await r.ReadAsync(ct))throw new InvalidOperationException("ACM_RETRY_H4_AMBIGUOUS");
        await r.DisposeAsync();
        return facts with {CanonicalFingerprint=new ContratoSapToledoCanonicalizer().Canonicalizar(payload).FingerprintSha256,
            IdentityVerified=facts.IdentityVerified&&payload.NumeroContrato==facts.Contract&&payload.Item==facts.Item&&FugaTES.Aplicacao.Configuracao.CentrosAcm.EhPermitido(payload.Centro)&&
                FugaTES.Aplicacao.Configuracao.AcmInboundWindowOptions.EscopoContratoAprovado(payload.TipoContrato,payload.TipoPesagem)&&payload.StatusContrato=="T",
            Predecessors=await Predecessors(c,tx,facts,ct)};
    }
    // Same contract identity lock as inbound admission; all integration rows precede H4/journal/attempt locks.
    static Task LockVersions(NpgsqlConnection c,NpgsqlTransaction tx,AcmRetryApproval a,CancellationToken ct)
        => LockVersions(c,tx,a.IntegrationId,a.Contract,ct);
    static async Task LockVersions(NpgsqlConnection c,NpgsqlTransaction tx,Guid targetId,string targetContract,CancellationToken ct)
    {
        await using(var identity=Cmd(c,tx,"select numero_contrato from fugates.contrato_sap_toledo_identidade where numero_contrato=@contract for update",targetId))
        {identity.Parameters.AddWithValue("contract",targetContract);if(await identity.ExecuteScalarAsync(ct) is not string contract||contract!=targetContract)throw new InvalidOperationException("ACM_PENDING_IDENTITY_MISSING");}
        var ids=new List<Guid>();
        await using(var rows=Cmd(c,tx,"select i.id from fugates.integracao i join fugates.contrato_sap_toledo_versao v on v.integracao_id=i.id where v.numero_contrato=@contract order by i.id for update of i",targetId))
        {rows.Parameters.AddWithValue("contract",targetContract);await using var read=await rows.ExecuteReaderAsync(ct);while(await read.ReadAsync(ct))ids.Add(read.GetGuid(0));}
        if(!ids.Contains(targetId))throw new InvalidOperationException("ACM_PENDING_TARGET_MISSING");
        foreach(var id in ids)await Lock(c,tx,id,ct);
    }
    static async Task<AcmRetryPredecessor[]> Predecessors(NpgsqlConnection c,NpgsqlTransaction tx,AcmRetrySnapshot target,CancellationToken ct)
    {
        const string sql="""
          select jsonb_build_object('Payload',v.payload_contrato,'Facts',jsonb_build_object(
           'IntegrationId',i.id,'Version',v.versao,'Fingerprint',i.fingerprint_sha256,'State',i.estado,
           'CorrelationId',i.correlation_id,'AttemptFence',t.fencing_token,
           'IdentityVerified',i.origem='SAP' and i.destino='TOLEDO' and i.operacao='SAP_CONTRATO_TOLEDO'
             and v.numero_contrato=i.chave_negocio and v.fingerprint_sha256=i.fingerprint_sha256,
           'Fence',i.fencing_token,'Worker',i.worker_proprietario,'LeaseUntil',i.lease_expira_em,'Phase',i.fase_envio,
           'AttemptCount',(select count(*) from fugates.tentativa_integracao a where a.integracao_id=i.id),
           'AttemptId',i.tentativa_atual_id,'AttemptOutcome',t.desfecho,
           'AllAttemptsFailed',not exists(select 1 from fugates.tentativa_integracao a where a.integracao_id=i.id and
             (a.desfecho is distinct from 'FALHA' or a.fase_envio is distinct from 'RESULTADO_RECEBIDO' or a.fencing_token is distinct from i.fencing_token)),
           'JournalPresent',p.integracao_id is not null,'Stage',p.etapa_funcional,'ErrorCode',p.erro_codigo,'JournalStatus',p.status_funcional,
           'JournalDeterminate',coalesce(p.erro_resultado_indeterminado=false and p.decisao_funcional_pendente is null
             and coalesce(p.erro_categoria,'') !~* '(TIMEOUT|INDETERMIN|OUTCOME_UNKNOWN)',false),
           'Synthetic',i.id='e502b0a0-57c5-49f3-976c-9e1fe7e355e3'::uuid or ltrim(v.numero_contrato,'0')='986801'
             or i.correlation_id ilike 'r165al%' or i.correlation_id ilike 'r165ai%'
             or coalesce(p.resultado_funcional,'') ilike '%SIMULAD%' or coalesce(p.fluxo,'') ilike '%SIMULAD%',
           'HasPendingCommand',exists(select 1 from fugates.auditoria_integracao a where a.integracao_id=i.id and a.evento='ACM_MANUAL_RETRY_REQUESTED'
             and not exists(select 1 from fugates.auditoria_integracao f where f.integracao_id=i.id and f.evento='ACM_MANUAL_RETRY_FINISHED'
               and (case when f.evento='ACM_MANUAL_RETRY_FINISHED' then f.diagnostico::jsonb end)->>'CommandId'=a.id::text)),
           'HasExternal',v.guardian_codigo_solicitado is not null or v.guardian_codigo_confirmado is not null or v.guardian_numero_referencia is not null
             or v.guardian_protocolo_id is not null or v.guardian_numero_ticket is not null or v.guardian_callback_id is not null or v.guardian_callback_sequencial is not null
             or p.protocolo_guardian is not null or p.numero_ticket_guardian is not null or p.resultado_funcional is not null
             or exists(select 1 from fugates.correlacao_guardian_evento e where e.integracao_id=i.id)
             or exists(select 1 from fugates.correlacao_guardian_sap g where ltrim(g.documento_logistico_referencia,'0')=ltrim(i.chave_negocio,'0'))
          ))::text
          from fugates.contrato_sap_toledo_versao v join fugates.integracao i on i.id=v.integracao_id
          left join fugates.projecao_resultado_fluxo_mvp p on p.integracao_id=i.id
          left join fugates.tentativa_integracao t on t.id=i.tentativa_atual_id and t.integracao_id=i.id
          where v.numero_contrato=@contract and v.versao<@version order by v.versao
          """;
        await using var cmd=Cmd(c,tx,sql,target.IntegrationId);cmd.Parameters.AddWithValue("contract",target.Contract);cmd.Parameters.AddWithValue("version",target.Version);
        await using var r=await cmd.ExecuteReaderAsync(ct);var result=new List<AcmRetryPredecessor>();
        while(await r.ReadAsync(ct))
        {
            using var d=JsonDocument.Parse(r.GetString(0));var facts=d.RootElement.GetProperty("Facts").Deserialize<AcmRetryPredecessor>(Json)!;
            var payload=d.RootElement.GetProperty("Payload").Deserialize<ContratoSapToledoSnapshot>(Json)!;
            result.Add(facts with{CanonicalFingerprint=new ContratoSapToledoCanonicalizer().Canonicalizar(payload).FingerprintSha256,
                IdentityVerified=facts.IdentityVerified&&payload.NumeroContrato==target.Contract&&payload.Item==target.Item&&FugaTES.Aplicacao.Configuracao.CentrosAcm.EhPermitido(payload.Centro)&&
                    FugaTES.Aplicacao.Configuracao.AcmInboundWindowOptions.EscopoContratoAprovado(payload.TipoContrato,payload.TipoPesagem)&&payload.StatusContrato=="T"});
        }
        return result.ToArray();
    }
    static async Task Supersede(NpgsqlConnection c,NpgsqlTransaction tx,AcmRetrySnapshot s,AcmRetryRequest request,AcmRetryActor actor,CancellationToken ct)
    {
        foreach(var p in s.Predecessors.OrderBy(x=>x.IntegrationId))
        {
            if(!p.SafeToSupersede())throw new InvalidOperationException("ACM_PREDECESSOR_NOT_SAFE");
            const string sql="""
             update fugates.integracao set estado='CANCELADA',tipo_disparo_pendente=null,proxima_tentativa_em=null,atualizado_em=clock_timestamp()
             where id=@id and estado=@state and fencing_token=@fence and fingerprint_sha256=@hash
              and worker_proprietario is null and lease_expira_em is null and tentativa_atual_id is not distinct from @attempt
             """;
            await using var update=Cmd(c,tx,sql,p.IntegrationId);update.Parameters.AddWithValue("state",p.State);update.Parameters.AddWithValue("fence",p.Fence);
            update.Parameters.AddWithValue("hash",p.Fingerprint);update.Parameters.AddWithValue("attempt",NpgsqlDbType.Uuid,(object?)p.AttemptId??DBNull.Value);
            if(await update.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("ACM_SUPERSESSION_STALE_VERSION");
            await Append(c,tx,Guid.NewGuid(),p.IntegrationId,"ACM_VERSION_SUPERSEDED_PRE_SEND",actor.Sid,p.Fence,p.AttemptId,p.CorrelationId,
                new{CommandId=request.CommandId,TargetIntegrationId=s.IntegrationId,TargetVersion=s.Version,TargetFingerprint=s.Fingerprint,
                    Previous=p,OperatorSid=actor.Sid,Justification=request.Justification,Meaning="SUPERSEDED_WITHOUT_EXTERNAL_SEND_NO_HISTORY_DELETED"},ct);
        }
    }
    static bool PendingStartMatches(AcmRetrySnapshot s,AcmRetrySnapshot before)
    {
        if(!s.IdentityVerified||!s.LatestVersion||s.ReceivedUtc!=before.ReceivedUtc||s.Phase!="NAO_INICIADO"||s.AttemptCount!=0||
            s.JournalPresent||s.Stage is not null||s.Metadata is not null||s.Indeterminate||s.GuardianRequestedCode is not null||
            s.GuardianConfirmedCode is not null||s.Reference is not null||s.ContractHasExternal||s.Predecessors.Length!=before.Predecessors.Length)return false;
        foreach(var p in before.Predecessors)
        {
            var current=s.Predecessors.SingleOrDefault(x=>x.IntegrationId==p.IntegrationId);
            if(current is null||current.State!="CANCELADA"||(current with{State=p.State})!=p)return false;
        }
        return true;
    }
    static async Task<Audit?> GetAudit(NpgsqlConnection c,NpgsqlTransaction tx,Guid commandId,CancellationToken ct)
    {
        await using var cmd=Cmd(c,tx,"select diagnostico from fugates.auditoria_integracao where id=@id and evento='ACM_MANUAL_RETRY_REQUESTED'",commandId);
        return await cmd.ExecuteScalarAsync(ct) is string json?JsonSerializer.Deserialize<Audit>(json,Json):null;
    }
    static async Task Append(NpgsqlConnection c,NpgsqlTransaction tx,Guid eventId,Guid integrationId,string kind,string actor,long fence,Guid? attempt,string correlation,object value,CancellationToken ct)
    {
        const string sql="""
         insert into fugates.auditoria_integracao(id,integracao_id,evento,criada_em,worker,fencing_token,tentativa_id,correlation_id,diagnostico)
         values(@event,@id,@kind,clock_timestamp(),@actor,@fence,@attempt,@correlation,@value)
         """;
        await using var cmd=Cmd(c,tx,sql,integrationId);
        cmd.Parameters.AddWithValue("event",eventId);cmd.Parameters.AddWithValue("kind",kind);cmd.Parameters.AddWithValue("actor",actor);
        cmd.Parameters.AddWithValue("fence",fence);cmd.Parameters.AddWithValue("attempt",NpgsqlDbType.Uuid,(object?)attempt??DBNull.Value);
        cmd.Parameters.AddWithValue("correlation",correlation);cmd.Parameters.AddWithValue("value",JsonSerializer.Serialize(value));await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<AcmRetryAdmission> AdmitAsync(AcmRetryRequest request,AcmRetryActor actor,AcmRetryApproval approval,DateTimeOffset now,CancellationToken ct)
    {
        if(System.Transactions.Transaction.Current is not null)throw new InvalidOperationException("ACM_RETRY_AMBIENT_TRANSACTION_REJECTED");
        AcmRetryAdmission Block(string code)=>new("BLOCKED",code,request.CommandId,request.IntegrationId);
        if(!AcmManualRetryPolicy.AuthorizationOpen(approval,request.IntegrationId,actor,now)||!request.Confirm||!AcmManualRetryService.SafeJustification(request.Justification)||request.CommandId==Guid.Empty)return Block("CONFIRMATION_OR_REASON_REQUIRED");
        await using var c=await source.OpenConnectionAsync(ct);await AcmPostgreSqlIdentityGuard.VerifyAsync(c,ct);
        await using var tx=await c.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        var committed=false;
        try
        {
            await Settings(c,tx,true,ct);
            if(approval.RecoverLatestPending||approval.AllowedStage==AcmRetryStage.SoapOnly)await LockVersions(c,tx,approval,ct);else await Lock(c,tx,request.IntegrationId,ct);
            now=await ServerNow(c,tx,ct);
            var previous=await GetAudit(c,tx,request.CommandId,ct);
            if(previous is not null)return previous.Request==request&&previous.Actor==actor&&previous.AuthorizationReference==approval.AuthorizationReference
                ?new("ALREADY_RECORDED","READ_EXISTING_COMMAND_NO_RESEND",request.CommandId,request.IntegrationId):Block("DIVERGENT_COMMAND_REPLAY");
            var s=await Snapshot(c,tx,request.IntegrationId,ct);
            if(s is null)return Block("NOT_FOUND");
            var decision=AcmManualRetryPolicy.Evaluate(s,approval,actor,now);
            if(!decision.Eligible)return Block(decision.Code);
            if(s.Digest()!=request.SnapshotDigest)return Block("STALE_PREVIEW");
            await Append(c,tx,request.CommandId,s.IntegrationId,Requested,actor.Sid,s.Fence,s.AttemptId,s.CorrelationId,
                new Audit(request,actor,approval.AuthorizationReference,now,s,decision.Stage,decision.ResumeMetadata,approval),ct);
            if(approval.RecoverLatestPending)await Supersede(c,tx,s,request,actor,ct);
            // Failed-attempt recovery retains its existing checkpoint update. First admission never invents a prior journal.
            if(!approval.RecoverLatestPending)await using(var projection=Cmd(c,tx,"""
                update fugates.projecao_resultado_fluxo_mvp set status_funcional=@status,etapa_funcional=@stage,
                 resultado_funcional=@metadata,erro_codigo=null,erro_categoria=null,erro_etapa=null,
                 erro_reprocessavel=null,erro_resultado_indeterminado=false,atualizado_em=clock_timestamp()
                where integracao_id=@id and etapa_funcional=@oldstage and atualizado_em=@updated
                """,s.IntegrationId))
            {
                projection.Parameters.AddWithValue("status",decision.Stage==AcmRetryStage.RestOnly?"EM_PROCESSAMENTO":"AGUARDANDO_DEPENDENCIA_EXTERNA");
                projection.Parameters.AddWithValue("stage",decision.Stage==AcmRetryStage.RestOnly?"R165U_SOAP_CONFIRMED":"R165U_PRE_SEND_BLOCKED");
                projection.Parameters.AddWithValue("metadata",NpgsqlDbType.Text,(object?)(decision.ResumeMetadata??s.Metadata)??DBNull.Value);
                projection.Parameters.AddWithValue("oldstage",s.Stage!);projection.Parameters.AddWithValue("updated",s.JournalUpdatedUtc);
                if(await projection.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("ACM_RETRY_STALE_JOURNAL");
            }
            await using(var queue=Cmd(c,tx,"""
                update fugates.integracao set estado='AGUARDANDO_REPROCESSAMENTO',tipo_disparo_pendente='REPROCESSAMENTO',
                 proxima_tentativa_em=clock_timestamp(),atualizado_em=clock_timestamp()
                where id=@id and estado=@state and fencing_token=@fence and fingerprint_sha256=@hash
                  and worker_proprietario is null and lease_expira_em is null
                """,s.IntegrationId))
            {
                queue.Parameters.AddWithValue("state",s.State);queue.Parameters.AddWithValue("fence",s.Fence);queue.Parameters.AddWithValue("hash",s.Fingerprint);
                if(await queue.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("ACM_RETRY_STALE_INTEGRATION");
            }
            await tx.CommitAsync(ct);committed=true;return new("QUEUED","ADMISSION_COMMITTED_NOT_BUSINESS_SUCCESS",request.CommandId,s.IntegrationId);
        }
        finally{await RollbackUnlessCommittedAsync(tx,committed,CancellationToken.None);}
    }
    public async Task<AcmRetryWork?> StartAsync(Guid commandId,AcmRetryApproval approval,string worker,DateTimeOffset now,CancellationToken ct)
    {
        if(System.Transactions.Transaction.Current is not null)throw new InvalidOperationException("ACM_RETRY_AMBIENT_TRANSACTION_REJECTED");
        if(!approval.Homologated||!approval.ExecutionAuthorized||!approval.ExclusiveWorker||now<approval.NotBeforeUtc||now>=approval.UntilUtc||!AcmManualRetryPolicy.Evidence(worker))return null;
        await using var c=await source.OpenConnectionAsync(ct);await AcmPostgreSqlIdentityGuard.VerifyAsync(c,ct);
        await using var tx=await c.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        var committed=false;
        try
        {
            await Settings(c,tx,true,ct);
            if(approval.RecoverLatestPending||approval.AllowedStage==AcmRetryStage.SoapOnly)await LockVersions(c,tx,approval,ct);else await Lock(c,tx,approval.IntegrationId,ct);
            now=await ServerNow(c,tx,ct);
            if(now<approval.NotBeforeUtc||now>=approval.UntilUtc)return null;
            var audit=await GetAudit(c,tx,commandId,ct);var s=await Snapshot(c,tx,approval.IntegrationId,ct);
            if(audit is null||s is null||(approval.AllowedStage==AcmRetryStage.SoapOnly&&!AcmManualRetryPolicy.ExactSoapRecoveryScope(s,approval))||audit.Before.IntegrationId!=approval.IntegrationId||audit.Actor.Sid!=approval.OperatorSid||
                audit.AuthorizationReference!=approval.AuthorizationReference||!AcmManualRetryPolicy.Evaluate(audit.Before,approval,audit.Actor,now).Eligible||
                audit.Before.Digest()!=audit.Request.SnapshotDigest||s.Version!=audit.Before.Version||s.AttemptId!=audit.Before.AttemptId||s.CorrelationId!=audit.Before.CorrelationId||
                audit.Before.Fingerprint!=approval.Fingerprint||s.Fingerprint!=approval.Fingerprint||
                s.CanonicalFingerprint!=s.Fingerprint||s.Synthetic||s.UnsafeAttempt||s.HasProtocolOrCallback||s.Fence!=audit.Before.Fence||
                s.State!="AGUARDANDO_REPROCESSAMENTO"||s.Worker is not null||s.LeaseUntil is not null||
                (!approval.RecoverLatestPending&&(s.Stage!=(audit.ResumeStage==AcmRetryStage.RestOnly?"R165U_SOAP_CONFIRMED":"R165U_PRE_SEND_BLOCKED")||
                 s.Metadata!=(audit.ResumeMetadata??audit.Before.Metadata)))||
                (approval.RecoverLatestPending&&!PendingStartMatches(s,audit.Before)))return null;
            if(approval.RecoverLatestPending)
            {
                foreach(var predecessor in audit.Before.Predecessors)
                {
                    await using var proof=Cmd(c,tx,"select count(*) from fugates.auditoria_integracao where integracao_id=@id and evento='ACM_VERSION_SUPERSEDED_PRE_SEND' and (case when evento='ACM_VERSION_SUPERSEDED_PRE_SEND' then diagnostico::jsonb end)->>'CommandId'=@command and (case when evento='ACM_VERSION_SUPERSEDED_PRE_SEND' then diagnostico::jsonb end)->>'TargetIntegrationId'=@target",predecessor.IntegrationId);
                    proof.Parameters.AddWithValue("command",commandId.ToString("D"));proof.Parameters.AddWithValue("target",approval.IntegrationId.ToString("D"));
                    if(await proof.ExecuteScalarAsync(ct) is not long n||n!=1)return null;
                }
            }
            await using var exists=Cmd(c,tx,"select exists(select 1 from fugates.auditoria_integracao where integracao_id=@id and evento='ACM_MANUAL_RETRY_STARTED' and (case when evento in ('ACM_MANUAL_RETRY_STARTED','ACM_MANUAL_RETRY_FINISHED') then diagnostico::jsonb end)->>'CommandId'=@command)",s.IntegrationId);
            exists.Parameters.AddWithValue("command",commandId.ToString("D"));if(await exists.ExecuteScalarAsync(ct) is true)return null;
            await Append(c,tx,Guid.NewGuid(),s.IntegrationId,Started,worker,s.Fence,s.AttemptId,s.CorrelationId,new{CommandId=commandId,Worker=worker,OperatorSid=audit.Actor.Sid,At=now},ct);
            await tx.CommitAsync(ct);committed=true;return new(commandId,s.IntegrationId,s.Fence,worker);
        }
        finally{await RollbackUnlessCommittedAsync(tx,committed,CancellationToken.None);}
    }
    static async Task<string> ReadStartedWorkerAsync(NpgsqlConnection c,NpgsqlTransaction tx,Guid commandId,Guid integrationId,CancellationToken ct)
    {
        await using var cmd=Cmd(c,tx,"select worker from fugates.auditoria_integracao where integracao_id=@id and evento='ACM_MANUAL_RETRY_STARTED' and (case when evento='ACM_MANUAL_RETRY_STARTED' then diagnostico::jsonb end)->>'CommandId'=@command",integrationId);
        cmd.Parameters.AddWithValue("command",commandId.ToString("D"));await using var reader=await cmd.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct)||reader.IsDBNull(0))throw new InvalidOperationException("ACM_RECONCILIATION_START_MISSING");
        var worker=reader.GetString(0);if(await reader.ReadAsync(ct)||!AcmManualRetryPolicy.Evidence(worker))throw new InvalidOperationException("ACM_RECONCILIATION_START_AMBIGUOUS");return worker;
    }
    public Task CompleteAsync(AcmRetryWork work,string result,DateTimeOffset now,CancellationToken ct)
        =>CompleteCoreAsync(work.CommandId,work.IntegrationId,work,null,null,result,now,ct);
    // Explicit maintenance of an already completed LOCAL attempt. This never starts
    // the motor, calls a gateway, changes the original payload or removes UNKNOWN.
    public Task ReconcileKnownPreSendAsync(Guid commandId,AcmRetryApproval approval,AcmRetryActor actor,DateTimeOffset now,CancellationToken ct)
        =>CompleteCoreAsync(commandId,approval.IntegrationId,null,approval,actor,"RECONCILED_KNOWN_PRE_SEND_NO_EXTERNAL_DISPATCH",now,ct);
    public static bool CanReconcileKnownPreSend(Audit audit,AcmRetrySnapshot s,AcmRetryApproval a,AcmRetryActor actor,DateTimeOffset now)
        =>AcmManualRetryPolicy.AuthorizationOpen(a,s.IntegrationId,actor,now)&&!a.RecoverLatestPending&&a.AllowedStage==AcmRetryStage.SoapOnly&&
          a.Contract==s.Contract&&a.Item==s.Item&&a.ExpectedVersion==s.Version&&a.ExpectedReceivedUtc==s.ReceivedUtc&&
          a.Fingerprint==s.Fingerprint&&a.ObservedSnapshotDigest==s.Digest()&&a.CorrectedFailureDigest==s.Digest()&&
          AcmManualRetryPolicy.Evidence(a.ExternalCorrectionEvidence)&&AcmManualRetryPolicy.Evidence(a.BusinessOriginEvidence)&&
          AcmManualRetryPolicy.IsKnownPreSendFailure(s)&&s.HasPendingCommand&&audit.Actor==actor&&
          audit.Request.Confirm&&AcmManualRetryService.SafeJustification(audit.Request.Justification)&&audit.Request.CommandId!=Guid.Empty&&
          audit.ResumeStage==AcmRetryStage.SoapOnly&&audit.Approval.AllowedStage==AcmRetryStage.SoapOnly&&
          audit.AuthorizationReference==audit.Approval.AuthorizationReference&&
          AcmManualRetryPolicy.Evaluate(audit.Before,audit.Approval,audit.Actor,audit.At).Eligible&&
          audit.Request.IntegrationId==s.IntegrationId&&audit.Before.IntegrationId==s.IntegrationId&&audit.Before.IdentityVerified&&
          audit.Before.Contract==s.Contract&&audit.Before.Item==s.Item&&audit.Before.Version==s.Version&&audit.Before.ReceivedUtc==s.ReceivedUtc&&
          audit.Before.Fingerprint==s.Fingerprint&&audit.Before.CorrelationId==s.CorrelationId&&
          audit.Before.Digest()==audit.Request.SnapshotDigest&&s.Fence==audit.Before.Fence+1&&s.AttemptId!=audit.Before.AttemptId;
    async Task CompleteCoreAsync(Guid commandId,Guid integrationId,AcmRetryWork? suppliedWork,AcmRetryApproval? reconciliationApproval,AcmRetryActor? actor,string result,DateTimeOffset now,CancellationToken ct)
    {
        if(System.Transactions.Transaction.Current is not null)throw new InvalidOperationException("ACM_RETRY_AMBIENT_TRANSACTION_REJECTED");
        await using var c=await source.OpenConnectionAsync(ct);await AcmPostgreSqlIdentityGuard.VerifyAsync(c,ct);
        await using var tx=await c.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        var committed=false;
        try
        {
            await Settings(c,tx,true,ct);
            if(reconciliationApproval is not null)await LockVersions(c,tx,reconciliationApproval,ct);else await Lock(c,tx,integrationId,ct);
            var audit=await GetAudit(c,tx,commandId,ct);
            if(audit is null)throw new InvalidOperationException("ACM_RETRY_RESULT_COMMAND_MISSING");
            var work=suppliedWork??new AcmRetryWork(commandId,integrationId,audit.Before.Fence,
                await ReadStartedWorkerAsync(c,tx,commandId,integrationId,ct));
            if(audit.Request.CommandId!=commandId||audit.Before.IntegrationId!=work.IntegrationId||audit.Before.Fence!=work.PreviousFence)
                throw new InvalidOperationException("ACM_RETRY_RESULT_COMMAND_MISMATCH");
            await using var completed=Cmd(c,tx,"select exists(select 1 from fugates.auditoria_integracao where integracao_id=@id and evento='ACM_MANUAL_RETRY_FINISHED' and (case when evento in ('ACM_MANUAL_RETRY_STARTED','ACM_MANUAL_RETRY_FINISHED') then diagnostico::jsonb end)->>'CommandId'=@command)",work.IntegrationId);
            completed.Parameters.AddWithValue("command",work.CommandId.ToString("D"));if(await completed.ExecuteScalarAsync(ct) is true)return;
            await using var started=Cmd(c,tx,"select count(*) from fugates.auditoria_integracao where integracao_id=@id and evento='ACM_MANUAL_RETRY_STARTED' and (case when evento in ('ACM_MANUAL_RETRY_STARTED','ACM_MANUAL_RETRY_FINISHED') then diagnostico::jsonb end)->>'CommandId'=@command and worker=@worker",work.IntegrationId);
            started.Parameters.AddWithValue("command",work.CommandId.ToString("D"));started.Parameters.AddWithValue("worker",work.Worker);
            if(await started.ExecuteScalarAsync(ct) is not long count||count!=1)throw new InvalidOperationException("ACM_RETRY_STARTED_AUDIT_MISSING");
            var s=await Snapshot(c,tx,work.IntegrationId,ct)??throw new InvalidOperationException("ACM_RETRY_RESULT_MISSING");
            now=await ServerNow(c,tx,ct);
            if(reconciliationApproval is not null&&(actor is null||!CanReconcileKnownPreSend(audit,s,reconciliationApproval,actor,now)))
                throw new InvalidOperationException("ACM_PRE_SEND_RECONCILIATION_NOT_AUTHORIZED");
            if(reconciliationApproval is not null)
            {
                await using var pending=Cmd(c,tx,"select count(*) from fugates.auditoria_integracao a where a.integracao_id=@id and a.evento='ACM_MANUAL_RETRY_REQUESTED' and not exists(select 1 from fugates.auditoria_integracao f where f.integracao_id=a.integracao_id and f.evento='ACM_MANUAL_RETRY_FINISHED' and (case when f.evento='ACM_MANUAL_RETRY_FINISHED' then f.diagnostico::jsonb end)->>'CommandId'=a.id::text)",s.IntegrationId);
                if(await pending.ExecuteScalarAsync(ct) is not long pendingCount||pendingCount!=1)
                    throw new InvalidOperationException("ACM_RECONCILIATION_PENDING_COMMAND_AMBIGUOUS");
            }
            var preSend=result!="UNKNOWN"&&AcmManualRetryPolicy.IsKnownPreSendFailure(s)&&s.Fence==work.PreviousFence+1&&s.AttemptId!=audit.Before.AttemptId;
            var soapOnlyConfirmed=result!="UNKNOWN"&&s.Fence==work.PreviousFence+1&&s.AttemptId!=audit.Before.AttemptId&&
                AcmManualRetryPolicy.IsKnownSoapOnlyProtocolPending(s,audit.ResumeStage);
            var known=preSend||soapOnlyConfirmed||s.Fence==work.PreviousFence+1&&s.AttemptId.HasValue&&s.AttemptId!=audit.Before.AttemptId&&s.AttemptOutcome is "SUCESSO" or "FALHA"&&s.State is "CONCLUIDA" or "FALHA_DEFINITIVA" or "AGUARDANDO_ACAO_MANUAL";
            if((preSend||soapOnlyConfirmed)&&s.State=="AGUARDANDO_REPROCESSAMENTO")
            {
                await using var manual=Cmd(c,tx,"""
                    update fugates.integracao set estado='AGUARDANDO_ACAO_MANUAL',tipo_disparo_pendente=null,
                     proxima_tentativa_em=null,atualizado_em=clock_timestamp()
                    where id=@id and estado='AGUARDANDO_REPROCESSAMENTO' and fase_envio='RESULTADO_RECEBIDO'
                     and fencing_token=@fence and tentativa_atual_id=@attempt and fingerprint_sha256=@hash
                     and worker_proprietario is null and lease_expira_em is null
                    """,s.IntegrationId);
                manual.Parameters.AddWithValue("fence",s.Fence);manual.Parameters.AddWithValue("attempt",s.AttemptId!.Value);manual.Parameters.AddWithValue("hash",s.Fingerprint);
                if(await manual.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("ACM_PRE_SEND_MANUAL_STATE_STALE");
                s=s with{State="AGUARDANDO_ACAO_MANUAL"};
            }
            if(reconciliationApproval is not null)
                await Append(c,tx,Guid.NewGuid(),s.IntegrationId,"ACM_MANUAL_PRE_SEND_RECONCILED",actor!.Sid,s.Fence,s.AttemptId,s.CorrelationId,
                    new{CommandId=commandId,At=now,OriginalUnknownPreserved=true,AuthorizationReference=reconciliationApproval.AuthorizationReference,
                        CorrectionEvidence=reconciliationApproval.ExternalCorrectionEvidence,SnapshotDigest=reconciliationApproval.ObservedSnapshotDigest,
                        Meaning="KNOWN_LOCAL_FAILURE_NO_EXTERNAL_DISPATCH"},ct);
            await Append(c,tx,Guid.NewGuid(),s.IntegrationId,known?Finished:"ACM_MANUAL_RETRY_UNKNOWN",work.Worker,s.Fence,s.AttemptId,s.CorrelationId,
                new{CommandId=work.CommandId,At=now,MotorResult=result,State=s.State,AttemptId=s.AttemptId,Fence=s.Fence,Stage=s.Stage,Known=known,SoapConfirmedProtocolPending=soapOnlyConfirmed},ct);
            await tx.CommitAsync(ct);committed=true;
        }
        finally{await RollbackUnlessCommittedAsync(tx,committed,CancellationToken.None);}
    }
}
