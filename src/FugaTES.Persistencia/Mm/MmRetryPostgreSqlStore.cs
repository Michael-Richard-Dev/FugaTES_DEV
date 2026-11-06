using FugaTES.Aplicacao.Configuracao;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Mm;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
using Npgsql;
namespace FugaTES.Persistencia.Mm;
/// <summary>Durable MM commands, independent of H4/ACM integration identifiers.</summary>
public sealed class MmRetryPostgreSqlStore(NpgsqlDataSource source,AmbienteBanco? ambiente=null):IMmRetryStore
{
 const string CommandFields="comando_id,anterior_json::text,snapshot_sha256,operador_sid,justificativa,estado,criado_em,claim_token,envio_iniciado_em,resultado_json::text";
 const string RegistryFields="reserva_id,chave_natural,pedido,item,centro,payload_json,payload_sha256,estado,http_status,protocolo_id,mensagem_segura,atualizado_em,fonte_sha256";
 static RegistroProtocoloMm Registry(NpgsqlDataReader r)=>new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.IsDBNull(8)?null:r.GetInt16(8),r.IsDBNull(9)?null:r.GetInt64(9),r.IsDBNull(10)?null:r.GetString(10),r.GetFieldValue<DateTimeOffset>(11),r.GetString(12));
 static MmRetryCommand Command(NpgsqlDataReader r)
 {
  string? code=null;
  if(!r.IsDBNull(9))
  {
   using var document=JsonDocument.Parse(r.GetString(9));var result=document.RootElement.GetProperty("result");
   var message=result.GetProperty("mensagem").GetString();
   if(MmRetryReason.IsPublic(message))code=message;
   else if(result.GetProperty("estado").GetString()==EstadoProtocoloMm.Recusado)code="MM_RETRY_GUARDIAN_REJECTED";
  }
  return new(){Id=r.GetGuid(0),Previous=JsonSerializer.Deserialize<RegistroProtocoloMm>(r.GetString(1),JsonSerializerOptions.Web)??throw new InvalidOperationException("MM_COMMAND_AUDIT_INVALID"),SnapshotDigest=r.GetString(2),OperatorSid=r.GetString(3),Justification=r.GetString(4),State=r.GetString(5),CreatedUtc=r.GetFieldValue<DateTimeOffset>(6),Fence=r.IsDBNull(7)?null:r.GetGuid(7),DispatchStartedUtc=r.IsDBNull(8)?null:r.GetFieldValue<DateTimeOffset>(8),OutcomeCode=code};
 }

 static bool Same(RegistroProtocoloMm? a,RegistroProtocoloMm b)=>a is not null&&JsonSerializer.Serialize(a,JsonSerializerOptions.Web)==JsonSerializer.Serialize(b,JsonSerializerOptions.Web);
 static async Task<RegistroProtocoloMm?> Read(NpgsqlConnection c,NpgsqlTransaction? tx,string pedido,string item,bool locked,CancellationToken ct)
 {
  await using var cmd=new NpgsqlCommand("select "+RegistryFields+" from fugates.protocolo_mm_pedido_compra where pedido=$1 and item=$2"+(locked?" for update":""),c,tx);cmd.Parameters.AddWithValue(pedido);cmd.Parameters.AddWithValue(item);
  await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?Registry(r):null;
 }
 public async Task<RegistroProtocoloMm?> ReadRegistryAsync(string pedido,string item,CancellationToken ct)
 {await using var c=await source.OpenConnectionAsync(ct);await RegistroProtocolosMmPostgreSql.ConferirAsync(c,null,ct,ambiente);return await Read(c,null,pedido,item,false,ct);}
 public async Task<bool> HasPendingAsync(string key,CancellationToken ct)
 {
  await using var c=await source.OpenConnectionAsync(ct);await RegistroProtocolosMmPostgreSql.ConferirAsync(c,null,ct,ambiente);
  await using var cmd=new NpgsqlCommand("select exists(select 1 from fugates.protocolo_mm_reenvio_comando where chave_natural=$1 and estado in ('PENDENTE','EM_PROCESSAMENTO','INDETERMINADO'))",c);cmd.Parameters.AddWithValue(key);return await cmd.ExecuteScalarAsync(ct) is true;
 }
 public async Task<MmRetryCommand?> FindCommandAsync(Guid id,CancellationToken ct)
 {
  await using var c=await source.OpenConnectionAsync(ct);await RegistroProtocolosMmPostgreSql.ConferirAsync(c,null,ct,ambiente);
  await using var cmd=new NpgsqlCommand("select "+CommandFields+" from fugates.protocolo_mm_reenvio_comando where comando_id=$1",c);cmd.Parameters.AddWithValue(id);await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?Command(r):null;
 }
 public async Task<MmRetryReceipt> EnqueueAsync(MmRetryCommand job,CancellationToken ct)
 {
  var row=job.Previous;
  if(job.Id==Guid.Empty||row is null||row.ReservaId==Guid.Empty||row.Estado!=EstadoProtocoloMm.Recusado||row.ProtocoloId is not null||!MmRetryPolicy.Key(row.Pedido,10)||!MmRetryPolicy.Key(row.Item,5)||!MmRetryOptions.Hash(job.SnapshotDigest)||!System.Text.RegularExpressions.Regex.IsMatch(job.OperatorSid,@"\AS-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+\z")||job.Justification.Length is <8 or >500||job.Justification.Any(char.IsControl)||job.State!="PENDENTE"||job.Fence is not null||job.DispatchStartedUtc is not null)throw new InvalidOperationException("MM_COMMAND_ADMISSION_INVALID");
  await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);await RegistroProtocolosMmPostgreSql.ConferirAsync(c,tx,ct,ambiente);
  var current=await Read(c,tx,row.Pedido,row.Item,true,ct);
  if(!Same(current,row))return new(job.Id,row.Pedido,row.Item,"BLOCKED","MM_RETRY_PREVIEW_CHANGED");
  await using var cmd=new NpgsqlCommand("""
   insert into fugates.protocolo_mm_reenvio_comando(comando_id,reserva_id,chave_natural,anterior_json,snapshot_sha256,operador_sid,justificativa,estado)
   values($1,$2,$3,$4::jsonb,$5,$6,$7,'PENDENTE') on conflict do nothing
   """,c,tx);
  cmd.Parameters.AddWithValue(job.Id);cmd.Parameters.AddWithValue(row.ReservaId);cmd.Parameters.AddWithValue(row.Chave);cmd.Parameters.AddWithValue(JsonSerializer.Serialize(row,JsonSerializerOptions.Web));cmd.Parameters.AddWithValue(job.SnapshotDigest);cmd.Parameters.AddWithValue(job.OperatorSid);cmd.Parameters.AddWithValue(job.Justification);
  var count=await cmd.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
  return new(job.Id,row.Pedido,row.Item,count==1?"QUEUED":"BLOCKED",count==1?"MM_RETRY_COMMITTED":"MM_RETRY_ALREADY_PENDING");
 }
 public async Task<MmRetryCommand?> ClaimAsync(CancellationToken ct)
 {
  await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);await RegistroProtocolosMmPostgreSql.ConferirAsync(c,tx,ct,ambiente);
  await using var cmd=new NpgsqlCommand("""
   with candidate as(select comando_id from fugates.protocolo_mm_reenvio_comando where estado='PENDENTE' order by criado_em,comando_id for update skip locked limit 1)
   update fugates.protocolo_mm_reenvio_comando j set estado='EM_PROCESSAMENTO',claim_token=$1,atualizado_em=now() from candidate c where j.comando_id=c.comando_id
   returning j.comando_id,j.anterior_json::text,j.snapshot_sha256,j.operador_sid,j.justificativa,j.estado,j.criado_em,j.claim_token,j.envio_iniciado_em,j.resultado_json::text
   """,c,tx);cmd.Parameters.AddWithValue(Guid.NewGuid());MmRetryCommand? value;
  await using(var r=await cmd.ExecuteReaderAsync(ct)){value=await r.ReadAsync(ct)?Command(r):null;}
  await tx.CommitAsync(ct);return value;
 }
 public async Task<bool> MarkDispatchAsync(MmRetryCommand job,CancellationToken ct)
 {
  if(job.Fence is null)return false;
  await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);await RegistroProtocolosMmPostgreSql.ConferirAsync(c,tx,ct,ambiente);
  var previous=await Read(c,tx,job.Previous.Pedido,job.Previous.Item,true,ct);if(!Same(previous,job.Previous))return false;
  await using var cmd=new NpgsqlCommand("update fugates.protocolo_mm_reenvio_comando set envio_iniciado_em=now(),atualizado_em=now() where comando_id=$1 and claim_token=$2 and estado='EM_PROCESSAMENTO' and envio_iniciado_em is null",c,tx);cmd.Parameters.AddWithValue(job.Id);cmd.Parameters.AddWithValue(job.Fence.Value);
  var count=await cmd.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return count==1;
 }
 public async Task FinishAsync(MmRetryCommand job,ResultadoProtocoloMm result,bool dispatched,CancellationToken ct)
 {
  if(job.Fence is null||result.Estado is not(EstadoProtocoloMm.Criado or EstadoProtocoloMm.Recusado or EstadoProtocoloMm.Indeterminado)||result.Mensagem.Length>400||result.Estado==EstadoProtocoloMm.Criado&&(!dispatched||result.Http is not(>=200 and <300)||result.ProtocoloId is not >0))throw new InvalidOperationException("MM_COMMAND_OUTCOME_INVALID");
  await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);await RegistroProtocolosMmPostgreSql.ConferirAsync(c,tx,ct,ambiente);
  var current=await Read(c,tx,job.Previous.Pedido,job.Previous.Item,true,ct);if(!Same(current,job.Previous))throw new InvalidOperationException("MM_COMMAND_PRIOR_STATE_CHANGED");
  if(dispatched)
  {
   await using var change=new NpgsqlCommand("update fugates.protocolo_mm_pedido_compra set estado=$2,http_status=$3,protocolo_id=$4,mensagem_segura=$5,atualizado_em=now() where reserva_id=$1 and estado='RECUSADO' and protocolo_id is null",c,tx);
   change.Parameters.AddWithValue(job.Previous.ReservaId);change.Parameters.AddWithValue(result.Estado);change.Parameters.Add(new(){NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Smallint,Value=result.Http is{} h?(short)h:DBNull.Value});change.Parameters.Add(new(){NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Bigint,Value=(object?)result.ProtocoloId??DBNull.Value});change.Parameters.AddWithValue(result.Mensagem);
   if(await change.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("MM_COMMAND_REGISTRY_NOT_UPDATED");
  }
  var state=result.Estado==EstadoProtocoloMm.Criado?"CONCLUIDO":result.Estado==EstadoProtocoloMm.Indeterminado?"INDETERMINADO":"BLOQUEADO";
  await using var finish=new NpgsqlCommand("update fugates.protocolo_mm_reenvio_comando set estado=$3,resultado_json=$4::jsonb,atualizado_em=now() where comando_id=$1 and claim_token=$2 and estado='EM_PROCESSAMENTO' and ($5=false or envio_iniciado_em is not null)",c,tx);finish.Parameters.AddWithValue(job.Id);finish.Parameters.AddWithValue(job.Fence.Value);finish.Parameters.AddWithValue(state);finish.Parameters.AddWithValue(JsonSerializer.Serialize(new{dispatched,result},JsonSerializerOptions.Web));finish.Parameters.AddWithValue(dispatched);
  if(await finish.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("MM_COMMAND_RESULT_NOT_COMMITTED");await tx.CommitAsync(ct);
 }
}
