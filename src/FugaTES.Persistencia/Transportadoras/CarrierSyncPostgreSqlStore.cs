using System.Data;
using FugaTES.Aplicacao.Transportadoras;
using Npgsql;
using NpgsqlTypes;
namespace FugaTES.Persistencia.Transportadoras;

public sealed class CarrierSyncPostgreSqlStore(NpgsqlDataSource source, string? lockConnectionString=null) : ICarrierSyncStore
{
    public const string CheckpointSql = """
        update fugates.carrier_sync_checkpoint set watermark=@watermark,upper_bound=@upper,page_cursor=@page,visited_pages=@visited
        where singleton
          and not exists(select 1 from fugates.carrier_sync_state where pending_attempt_id is not null or status is distinct from 'SYNCED')
          and row(watermark,upper_bound,page_cursor,visited_pages) is distinct from row(@watermark,@upper,@page,@visited)
        """;
    public const string ReserveSql = """
        insert into fugates.carrier_sync_state(bp,content_hash,status,action,result_code,sap_checked_utc,pending_attempt_id)
        values(@bp,@hash,'WRITE_IN_FLIGHT',@action,'WRITE_IN_FLIGHT',@at,@id)
        on conflict(bp) do update set content_hash=@hash,status='WRITE_IN_FLIGHT',action=@action,result_code='WRITE_IN_FLIGHT',pending_attempt_id=@id
        where carrier_sync_state.pending_attempt_id is null and carrier_sync_state.status not in('WRITE_IN_FLIGHT','INDETERMINATE')
        returning bp;
        """;
    public static NpgsqlConnectionStringBuilder LockConnectionOptions(string connectionString) => new(connectionString) { Pooling = false, Multiplexing = false };
    public async Task<ICarrierCycleLease?> TentarAdquirirCicloAsync(CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(lockConnectionString))throw new InvalidOperationException("CARRIER_LOCK_CONNECTION_PROFILE_REQUIRED");
        var connection = new NpgsqlConnection(LockConnectionOptions(lockConnectionString).ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("select pg_try_advisory_lock(1179993940,3)",connection);
            if (await command.ExecuteScalarAsync(ct) is not true) { await connection.DisposeAsync(); return null; }
            return new Lease(connection);
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task<CarrierStoredBp?> CarregarEstadoAsync(string bp,CancellationToken ct)
    {
        CarrierPersistencePolicy.ValidateBp(bp);
        await using var command=source.CreateCommand("select "+CarrierSyncReadRepository.BpFields+" from fugates.carrier_sync_state where bp=@bp"); command.Parameters.AddWithValue("bp",bp);
        await using var reader=await command.ExecuteReaderAsync(ct); return await reader.ReadAsync(ct)?CarrierSyncReadRepository.ReadBp(reader):null;
    }
    public async Task<CarrierStoredCheckpoint> CarregarCheckpointAsync(CancellationToken ct)
    {
        await using var command=source.CreateCommand("select watermark,upper_bound,page_cursor,visited_pages from fugates.carrier_sync_checkpoint where singleton");
        await using var reader=await command.ExecuteReaderAsync(ct); if(!await reader.ReadAsync(ct))throw new InvalidOperationException("CARRIER_CHECKPOINT_MISSING");
        return new(CarrierSyncReadRepository.Date(reader,0),CarrierSyncReadRepository.Date(reader,1),reader.IsDBNull(2)?null:reader.GetString(2)) { VisitedPages=reader.GetFieldValue<string[]>(3) };
    }
    public async Task<bool> EnfileirarPedidoAsync(Guid requestId,string bp,string authenticatedSid,DateTimeOffset requestedAt,CancellationToken ct)
    {
        CarrierPersistencePolicy.ValidateBp(bp); if(requestId==Guid.Empty || string.IsNullOrWhiteSpace(authenticatedSid) || authenticatedSid.Length>180)throw new ArgumentException("CARRIER_REQUEST_INVALID");
        await using var command=source.CreateCommand("insert into fugates.carrier_sync_request(id,bp,authenticated_sid,requested_utc) values(@id,@bp,@sid,@at) on conflict(id) do nothing");
        command.Parameters.AddWithValue("id",requestId); command.Parameters.AddWithValue("bp",bp); command.Parameters.AddWithValue("sid",authenticatedSid); command.Parameters.AddWithValue("at",requestedAt.ToUniversalTime()); return await command.ExecuteNonQueryAsync(ct)==1;
    }
    private sealed class Lease : ICarrierCycleLease
    {
        private readonly NpgsqlConnection connection;
        private readonly CancellationTokenSource lost=new();
        private readonly SemaphoreSlim gate=new(1,1);
        private bool disposed;
        public Lease(NpgsqlConnection connection) { this.connection=connection; connection.StateChange+=StateChanged; }
        private void StateChanged(object? sender,StateChangeEventArgs args) { if(args.CurrentState!=ConnectionState.Open)lost.Cancel(); }
        public CancellationToken PerdaToken=>lost.Token;
        private async Task Ensure(CancellationToken ct)
        {
            if(disposed || lost.IsCancellationRequested || connection.State!=ConnectionState.Open)throw new InvalidOperationException("CARRIER_LEASE_LOST");
            await using var command=new NpgsqlCommand("select exists(select 1 from pg_locks where locktype='advisory' and classid=1179993940 and objid=3 and objsubid=2 and granted and pid=pg_backend_pid())",connection);
            if(await command.ExecuteScalarAsync(ct) is not true) { lost.Cancel(); throw new InvalidOperationException("CARRIER_LEASE_LOST"); }
        }
        private async Task<T> Run<T>(Func<Task<T>> operation,CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try { await Ensure(ct); return await operation(); }
            catch { lost.Cancel(); throw; }
            finally { gate.Release(); }
        }
        private Task Exec(string sql,Action<NpgsqlCommand> bind,CancellationToken ct)=>Run(async()=>{ await using var cmd=new NpgsqlCommand(sql,connection); bind(cmd); var affected=await cmd.ExecuteNonQueryAsync(ct); if(affected!=1)throw new InvalidOperationException("CARRIER_PERSISTENCE_NOT_CONFIRMED"); return affected; },ct);
        public Task VerificarAtivaAsync(CancellationToken ct)=>Run(()=>Task.FromResult(true),ct);
        public Task IniciarAsync(Guid cycleId,DateTimeOffset startedUtc,CancellationToken ct)=>Run(async()=>{
            await using var tx=await connection.BeginTransactionAsync(ct);
            await using(var interrupted=new NpgsqlCommand("update fugates.carrier_sync_cycle set status='INTERRUPTED',finished_utc=@at where status='RUNNING' and finished_utc is null",connection,tx)) {
                interrupted.Parameters.AddWithValue("at",startedUtc.ToUniversalTime()); await interrupted.ExecuteNonQueryAsync(ct);
            }
            await using(var cycle=new NpgsqlCommand("insert into fugates.carrier_sync_cycle(id,started_utc,status) values(@id,@at,'RUNNING')",connection,tx)) {
                cycle.Parameters.AddWithValue("id",cycleId); cycle.Parameters.AddWithValue("at",startedUtc.ToUniversalTime());
                if(await cycle.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("CARRIER_PERSISTENCE_NOT_CONFIRMED");
            }
            await tx.CommitAsync(ct);return true;
        },ct);
        public Task<bool> RegistrarAntesDoWriteAsync(CarrierPendingWrite attempt,CancellationToken ct)
        {
            CarrierPersistencePolicy.ValidateBp(attempt.Bp); CarrierPersistencePolicy.ValidateHash(attempt.ExpectedHash);
            if(attempt.AttemptId==Guid.Empty || attempt.CycleId==Guid.Empty || attempt.Action is not("CREATE" or "UPDATE"))throw new ArgumentException("CARRIER_ATTEMPT_INVALID");
            return Run(async()=>{
                await using var tx=await connection.BeginTransactionAsync(ct);
                await using var reserve=new NpgsqlCommand(ReserveSql,connection,tx);
                BindAttempt(reserve,attempt);
                if(await reserve.ExecuteScalarAsync(ct) is null) { await tx.RollbackAsync(ct); return false; }
                await using var insert=new NpgsqlCommand("insert into fugates.carrier_sync_attempt(id,cycle_id,bp,action,expected_hash,guardian_code,started_utc) values(@id,@cycle,@bp,@action,@hash,@guardian,@at)",connection,tx); BindAttempt(insert,attempt); await insert.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return true;
            },ct);
        }
        private static void BindAttempt(NpgsqlCommand c,CarrierPendingWrite a) { c.Parameters.AddWithValue("id",a.AttemptId); c.Parameters.AddWithValue("cycle",a.CycleId); c.Parameters.AddWithValue("bp",a.Bp); c.Parameters.AddWithValue("action",a.Action); c.Parameters.AddWithValue("hash",a.ExpectedHash); c.Parameters.AddWithValue("at",a.StartedUtc.ToUniversalTime()); Nullable(c,"guardian",NpgsqlDbType.Varchar,a.GuardianCode); }
        public Task ConcluirBpAsync(CarrierStoredBp result,CancellationToken ct)
        {
            CarrierPersistencePolicy.ValidateBp(result.Bp); CarrierPersistencePolicy.ValidateHash(result.ContentHash); CarrierPersistencePolicy.ValidateCode(result.Status); CarrierPersistencePolicy.ValidateCode(result.Action); CarrierPersistencePolicy.ValidateCode(result.ResultCode);
            if(result.Name.Length>250 || result.Name.Any(char.IsControl))throw new ArgumentException("CARRIER_NAME_INVALID");
            return Run(async()=>{
                await using var tx=await connection.BeginTransactionAsync(ct);
                await using(var current=new NpgsqlCommand("select pending_attempt_id from fugates.carrier_sync_state where bp=@bp for update",connection,tx)) {
                    current.Parameters.AddWithValue("bp",result.Bp);
                    var pending=await current.ExecuteScalarAsync(ct);
                    if(pending is Guid existing && !result.PendingAttemptId.HasValue) {
                        if(!result.GuardianSyncedUtc.HasValue || result.Status!="SYNCED")throw new InvalidOperationException("CARRIER_READBACK_CONFIRMATION_REQUIRED");
                        await using var resolve=new NpgsqlCommand("update fugates.carrier_sync_attempt set resolved_utc=now(),status='RESOLVED_READBACK' where id=@id and expected_hash=@hash and started_utc<=@confirmed and resolved_utc is null",connection,tx);
                        resolve.Parameters.AddWithValue("id",existing); resolve.Parameters.AddWithValue("hash",result.ContentHash); resolve.Parameters.AddWithValue("confirmed",result.GuardianSyncedUtc.Value.ToUniversalTime());
                        if(await resolve.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("CARRIER_READBACK_HASH_MISMATCH");
                    }
                }
                await using var c=new NpgsqlCommand("""
                    insert into fugates.carrier_sync_state(bp,name,guardian_code,content_hash,status,action,result_code,sap_checked_utc,guardian_synced_utc,pending_attempt_id)
                    values(@bp,@name,@guardian,@hash,@status,@action,@code,@sap,@sync,@pending)
                    on conflict(bp) do update set name=@name,guardian_code=@guardian,content_hash=@hash,status=@status,action=@action,result_code=@code,sap_checked_utc=@sap,guardian_synced_utc=@sync,pending_attempt_id=@pending
                    """,connection,tx);
                c.Parameters.AddWithValue("bp",result.Bp); c.Parameters.AddWithValue("name",result.Name); c.Parameters.AddWithValue("hash",result.ContentHash); c.Parameters.AddWithValue("status",result.Status); c.Parameters.AddWithValue("action",result.Action); c.Parameters.AddWithValue("code",result.ResultCode); c.Parameters.AddWithValue("sap",result.SapCheckedUtc.ToUniversalTime()); Nullable(c,"guardian",NpgsqlDbType.Varchar,result.GuardianCode); Nullable(c,"sync",NpgsqlDbType.TimestampTz,result.GuardianSyncedUtc?.ToUniversalTime()); Nullable(c,"pending",NpgsqlDbType.Uuid,result.PendingAttemptId);
                await c.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return true;
            },ct);
        }
        public Task ConfirmarPaginaAsync(CarrierStoredCheckpoint checkpoint,CancellationToken ct)
        {
            if(checkpoint.VisitedPages.Length>1000)throw new ArgumentException("CARRIER_CURSOR_LIMIT");
            foreach(var hash in checkpoint.VisitedPages)CarrierPersistencePolicy.ValidateHash(hash);
            return Run(async()=>{
                await using var tx=await connection.BeginTransactionAsync(ct);
                await using var cmd=new NpgsqlCommand(CheckpointSql,connection,tx);
                Nullable(cmd,"watermark",NpgsqlDbType.TimestampTz,checkpoint.Watermark?.ToUniversalTime());
                Nullable(cmd,"upper",NpgsqlDbType.TimestampTz,checkpoint.UpperBound?.ToUniversalTime());
                Nullable(cmd,"page",NpgsqlDbType.Text,checkpoint.PageCursor);
                cmd.Parameters.AddWithValue("visited",NpgsqlDbType.Array | NpgsqlDbType.Text,checkpoint.VisitedPages);
                var affected=await cmd.ExecuteNonQueryAsync(ct);
                if(affected==0)
                {
                    await using var guard=new NpgsqlCommand("select exists(select 1 from fugates.carrier_sync_checkpoint where singleton and row(watermark,upper_bound,page_cursor,visited_pages) is not distinct from row(@watermark,@upper,@page,@visited)) and not exists(select 1 from fugates.carrier_sync_state where pending_attempt_id is not null or status is distinct from 'SYNCED')",connection,tx);
                    foreach(NpgsqlParameter parameter in cmd.Parameters)guard.Parameters.AddWithValue(parameter.ParameterName,parameter.NpgsqlDbType,parameter.Value!);
                    if(await guard.ExecuteScalarAsync(ct) is not true)throw new InvalidOperationException("CARRIER_CHECKPOINT_UNRECONCILED_OR_MISSING");
                    // Same confirmed cursor is idempotent: no second physical UPDATE.
                }
                else if(affected!=1)throw new InvalidOperationException("CARRIER_PERSISTENCE_NOT_CONFIRMED");
                await tx.CommitAsync(ct);return true;
            },ct);
        }
        public Task RegistrarEtapaAsync(CarrierStoredStep step,CancellationToken ct)
        {
            CarrierPersistencePolicy.ValidateBp(step.Bp); CarrierPersistencePolicy.ValidateStep(step.Step); CarrierPersistencePolicy.ValidateCode(step.ResultCode);
            return Exec("insert into fugates.carrier_sync_step(cycle_id,bp,step,result_code,occurred_utc) values(@cycle,@bp,@step,@code,@at)",c=>{c.Parameters.AddWithValue("cycle",step.CycleId);c.Parameters.AddWithValue("bp",step.Bp);c.Parameters.AddWithValue("step",step.Step);c.Parameters.AddWithValue("code",step.ResultCode);c.Parameters.AddWithValue("at",step.OccurredUtc.ToUniversalTime());},ct);
        }
        public Task HeartbeatAsync(DateTimeOffset now,DateTimeOffset nextCycle,CancellationToken ct)=>Exec("update fugates.carrier_sync_checkpoint set enabled=true,heartbeat_utc=@now,next_cycle_utc=@next where singleton",c=>{c.Parameters.AddWithValue("now",now.ToUniversalTime());c.Parameters.AddWithValue("next",nextCycle.ToUniversalTime());},ct);
        public Task ConcluirAsync(CarrierStoredCycle cycle,CancellationToken ct)
        {
            CarrierPersistencePolicy.ValidateCode(cycle.Status); if(new[]{cycle.Consulted,cycle.Created,cycle.Updated,cycle.Unchanged,cycle.Pending,cycle.Failed}.Any(n=>n<0))throw new ArgumentException("CARRIER_COUNTER_INVALID");
            return Exec("update fugates.carrier_sync_cycle set finished_utc=@at,consulted=@consulted,created=@created,updated=@updated,unchanged=@unchanged,pending=@pending,failed=@failed,status=@status where id=@id",c=>{c.Parameters.AddWithValue("id",cycle.Id); Nullable(c,"at",NpgsqlDbType.TimestampTz,cycle.FinishedUtc?.ToUniversalTime());c.Parameters.AddWithValue("consulted",cycle.Consulted);c.Parameters.AddWithValue("created",cycle.Created);c.Parameters.AddWithValue("updated",cycle.Updated);c.Parameters.AddWithValue("unchanged",cycle.Unchanged);c.Parameters.AddWithValue("pending",cycle.Pending);c.Parameters.AddWithValue("failed",cycle.Failed);c.Parameters.AddWithValue("status",cycle.Status);},ct);
        }
        public Task<CarrierManualRequest?> TomarPedidoAsync(CancellationToken ct)=>Run(async()=>{
            await using var c=new NpgsqlCommand("update fugates.carrier_sync_request set status='PROCESSING' where id=(select id from fugates.carrier_sync_request where status in('PENDING','PROCESSING') order by requested_utc,id limit 1 for update skip locked) returning id,bp,authenticated_sid,requested_utc",connection);
            await using var r=await c.ExecuteReaderAsync(ct); return await r.ReadAsync(ct)?new CarrierManualRequest(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetFieldValue<DateTimeOffset>(3)):null;
        },ct);
        public Task ConcluirPedidoAsync(Guid id,string resultCode,CancellationToken ct)
        {
            CarrierPersistencePolicy.ValidateCode(resultCode); return Exec("update fugates.carrier_sync_request set status='COMPLETED',result_code=@code where id=@id and status='PROCESSING'",c=>{c.Parameters.AddWithValue("id",id);c.Parameters.AddWithValue("code",resultCode);},ct);
        }
        public async ValueTask DisposeAsync()
        {
            await gate.WaitAsync();
            try { if(disposed)return; disposed=true; connection.StateChange-=StateChanged; lost.Cancel(); await connection.DisposeAsync(); }
            finally { gate.Release(); }
        }
    }
    internal static void Nullable(NpgsqlCommand c,string key,NpgsqlDbType type,object? value)=>c.Parameters.AddWithValue(key,type,value??DBNull.Value);
}






