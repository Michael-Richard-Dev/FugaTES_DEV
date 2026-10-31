using FugaTES.Aplicacao.Transportadoras;
using Npgsql;
using NpgsqlTypes;
namespace FugaTES.Persistencia.Transportadoras;
public sealed class CarrierSyncReadRepository(NpgsqlDataSource source,TimeProvider? clock=null) : ICarrierSyncReadRepository
{
    internal const string BpFields="bp,name,guardian_code,content_hash,status,action,result_code,sap_checked_utc,guardian_synced_utc,pending_attempt_id";
    private const string CycleFields="id,started_utc,finished_utc,consulted,created,updated,unchanged,pending,failed,status";
    private const string StepFields="cycle_id,bp,step,result_code,occurred_utc";
    private const string BpFilter="(@bp is null or bp ilike '%' || @bp || '%') and (@name is null or name ilike '%' || @name || '%') and (@status is null or status=@status) and (@from is null or sap_checked_utc>=@from) and (@to is null or sap_checked_utc<=@to)";
    public async Task<CarrierSyncSnapshot> ConsultarAsync(CarrierSyncFiltro filtro,CancellationToken ct)
    {
        filtro.Validar(); var rows=new List<CarrierStoredBp>();
        await using(var c=source.CreateCommand("select "+BpFields+" from fugates.carrier_sync_state where "+BpFilter+" order by sap_checked_utc desc,bp limit @limit")) { Filter(c,filtro); await using var r=await c.ExecuteReaderAsync(ct); while(await r.ReadAsync(ct))rows.Add(ReadBp(r)); }
        CarrierSyncIndicators indicators;
        await using(var c=source.CreateCommand("""
            select enabled,heartbeat_utc,next_cycle_utc,(select max(started_utc) from fugates.carrier_sync_cycle),
            (select count(*) from fugates.carrier_sync_state),
            coalesce((select sum(created) from fugates.carrier_sync_cycle),0),coalesce((select sum(updated) from fugates.carrier_sync_cycle),0),coalesce((select sum(unchanged) from fugates.carrier_sync_cycle),0),
            (select count(*) from fugates.carrier_sync_state where status in('PENDING','WRITE_IN_FLIGHT','INDETERMINATE','BLOCKED_DATA','BLOCKED')),
            (select count(*) from fugates.carrier_sync_state where status in('BLOCKED','BLOCKED_DATA','FAILED','ERROR','INDETERMINATE')),
            (select count(*) from fugates.carrier_sync_state where guardian_code is not null and guardian_synced_utc is not null)
            from fugates.carrier_sync_checkpoint where singleton
            """)) {
            await using var r=await c.ExecuteReaderAsync(ct); if(!await r.ReadAsync(ct))throw new InvalidOperationException("CARRIER_CHECKPOINT_MISSING");
            indicators=new(CarrierPersistencePolicy.ServiceActive(r.GetBoolean(0),Date(r,1),(clock??TimeProvider.System).GetUtcNow()),30,Date(r,3),Date(r,2),r.GetInt64(4),Convert.ToInt64(r.GetValue(5)),Convert.ToInt64(r.GetValue(6)),Convert.ToInt64(r.GetValue(7)),r.GetInt64(8),r.GetInt64(9)) {GuardianCount=r.GetInt64(10)};
        }
        return new(rows,indicators);
    }
    public async Task<CarrierBpDetail> DetalharBpAsync(string bp,CancellationToken ct)
    {
        CarrierPersistencePolicy.ValidateBp(bp); CarrierStoredBp? state=null;
        await using(var c=source.CreateCommand("select "+BpFields+" from fugates.carrier_sync_state where bp=@bp")){c.Parameters.AddWithValue("bp",bp); await using var r=await c.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct))state=ReadBp(r);}
        var steps=await Steps("bp=@bp",c=>c.Parameters.AddWithValue("bp",bp),ct);return new(state,steps);
    }
    public async Task<IReadOnlyList<CarrierStoredCycle>> ConsultarCiclosAsync(CarrierSyncFiltro filtro,CancellationToken ct)
    {
        filtro.Validar(); var result=new List<CarrierStoredCycle>();
        await using var c=source.CreateCommand("select "+CycleFields+" from fugates.carrier_sync_cycle cyc where (@from is null or started_utc>=@from) and (@to is null or started_utc<=@to) and (@status is null or status=@status) and ((@bp is null and @name is null) or exists(select 1 from fugates.carrier_sync_step st left join fugates.carrier_sync_state s on s.bp=st.bp where st.cycle_id=cyc.id and (@bp is null or st.bp ilike '%' || @bp || '%') and (@name is null or s.name ilike '%' || @name || '%'))) order by started_utc desc,id limit @limit");Filter(c,filtro);await using var r=await c.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))result.Add(new(r.GetGuid(0),r.GetFieldValue<DateTimeOffset>(1),Date(r,2),r.GetInt32(3),r.GetInt32(4),r.GetInt32(5),r.GetInt32(6),r.GetInt32(7),r.GetInt32(8),r.GetString(9))); return result;
    }
    public Task<IReadOnlyList<CarrierStoredStep>> DetalharCicloAsync(Guid id,CancellationToken ct)=>Steps("cycle_id=@id",c=>c.Parameters.AddWithValue("id",id),ct);
    private async Task<IReadOnlyList<CarrierStoredStep>> Steps(string where,Action<NpgsqlCommand> bind,CancellationToken ct)
    {
        var steps=new List<CarrierStoredStep>();await using var c=source.CreateCommand("select "+StepFields+" from fugates.carrier_sync_step where "+where+" order by occurred_utc desc,id desc limit 1000");bind(c);await using var r=await c.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))steps.Add(new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetFieldValue<DateTimeOffset>(4)));return steps;
    }
    internal static CarrierStoredBp ReadBp(NpgsqlDataReader r)=>new(r.GetString(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetFieldValue<DateTimeOffset>(7),Date(r,8),r.IsDBNull(9)?null:r.GetGuid(9));
    internal static DateTimeOffset? Date(NpgsqlDataReader r,int ordinal)=>r.IsDBNull(ordinal)?null:r.GetFieldValue<DateTimeOffset>(ordinal);
    private static void Filter(NpgsqlCommand c,CarrierSyncFiltro f)
    {
        CarrierSyncPostgreSqlStore.Nullable(c,"bp",NpgsqlDbType.Text,string.IsNullOrWhiteSpace(f.CodigoBp)?null:f.CodigoBp);
        CarrierSyncPostgreSqlStore.Nullable(c,"name",NpgsqlDbType.Text,string.IsNullOrWhiteSpace(f.Nome)?null:f.Nome);
        CarrierSyncPostgreSqlStore.Nullable(c,"status",NpgsqlDbType.Text,string.IsNullOrWhiteSpace(f.Status)?null:f.Status);
        CarrierSyncPostgreSqlStore.Nullable(c,"from",NpgsqlDbType.TimestampTz,f.DeUtc?.ToUniversalTime());CarrierSyncPostgreSqlStore.Nullable(c,"to",NpgsqlDbType.TimestampTz,f.AteUtc?.ToUniversalTime());c.Parameters.AddWithValue("limit",f.Limit);
    }
}

