using FugaTES.Aplicacao.Configuracao;
using System.Security.Cryptography;
using System.Text;
using FugaTES.Aplicacao.Contratos.Mm;
using FugaTES.Aplicacao.Modelos.Mm;
using Npgsql;
namespace FugaTES.Persistencia.Mm;
public sealed class RegistroProtocolosMmPostgreSql(NpgsqlDataSource fonte,AmbienteBanco? ambiente=null):IRegistroProtocolosMm
{
    public static string Role=>AmbientesBanco.Dev110.PapelMm;
    public static void ValidarIdentidade(string database,string user,string session,string cluster,int port,params AmbienteBanco[] aceitos)
    {var lista=aceitos.Length==0?new[]{AmbientesBanco.Dev110}:aceitos;var a=AmbientesBanco.Por(database,user,lista);if(a is null||database!=a.Banco||user!=a.PapelMm||session!=user||cluster!="18/fugatesacmdev"||port!=5433)throw new InvalidOperationException("MM_DATABASE_IDENTITY_REJECTED");}
    internal static async Task ConferirAsync(NpgsqlConnection connection,NpgsqlTransaction? tx,CancellationToken ct,AmbienteBanco? ambiente=null)
    {
        await using var command=new NpgsqlCommand("select current_database(),current_user,session_user,current_setting('cluster_name'),current_setting('port')::int",connection,tx);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))throw new InvalidOperationException("MM_DATABASE_IDENTITY_UNAVAILABLE");
        ValidarIdentidade(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetInt32(4),ambiente??AmbientesBanco.Dev110);
    }
    public async Task<IReadOnlyList<RegistroProtocoloMm>> LerAsync(CancellationToken ct)
    {
        await using var connection=await fonte.OpenConnectionAsync(ct);await ConferirAsync(connection,null,ct,ambiente);
        await using var command=new NpgsqlCommand("select reserva_id,chave_natural,pedido,item,centro,payload_json,payload_sha256,estado,http_status,protocolo_id,mensagem_segura,atualizado_em,fonte_sha256 from fugates.protocolo_mm_pedido_compra order by criado_em limit 100001",connection);
        await using var reader=await command.ExecuteReaderAsync(ct);var result=new List<RegistroProtocoloMm>();
        while(await reader.ReadAsync(ct))
        {
            if(result.Count>=100000)throw new InvalidOperationException("MM_REGISTRY_LIMIT");
            result.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7),
                reader.IsDBNull(8)?null:reader.GetInt16(8),reader.IsDBNull(9)?null:reader.GetInt64(9),reader.IsDBNull(10)?null:reader.GetString(10),reader.GetFieldValue<DateTimeOffset>(11),reader.GetString(12)));
        }
        return result;
    }
    public async Task<bool> TentarReservarAsync(RegistroProtocoloMm row,CancellationToken ct)
    {
        if(row.Estado!=EstadoProtocoloMm.Indeterminado||row.Http is not null||row.ProtocoloId is not null||row.ReservaId==Guid.Empty
           ||row.SourceSha256 is null||!System.Text.RegularExpressions.Regex.IsMatch(row.SourceSha256,"^[0-9A-F]{64}$")||row.JsonEnviado.Length>65536||Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.JsonEnviado)))!=row.Sha256)
            throw new InvalidOperationException("MM_RESERVATION_INVALID");
        await using var connection=await fonte.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);await ConferirAsync(connection,tx,ct,ambiente);
        await using var command=new NpgsqlCommand("""
            insert into fugates.protocolo_mm_pedido_compra
            (reserva_id,chave_natural,empresa,pedido,item,centro,payload_json,payload_sha256,estado,mensagem_segura,fonte_sha256)
            values($1,$2,'3000',$3,$4,$5,$6,$7,'INDETERMINADO',$8,$9)
            on conflict(chave_natural) do nothing
            """,connection,tx);
        foreach(var value in new object[]{row.ReservaId,row.Chave,row.Pedido,row.Item,row.Centro,row.JsonEnviado,row.Sha256,(object?)row.Mensagem??DBNull.Value,row.SourceSha256})command.Parameters.AddWithValue(value);
        var count=await command.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return count==1;
    }
    public async Task FecharAsync(Guid id,string key,ResultadoProtocoloMm result,CancellationToken ct)
    {
        if(result.Estado is not(EstadoProtocoloMm.Criado or EstadoProtocoloMm.Recusado or EstadoProtocoloMm.Indeterminado)
           ||result.Estado==EstadoProtocoloMm.Criado&&(result.ProtocoloId is not >0||result.Http is not(>=200 and <300)))
            throw new InvalidOperationException("MM_OUTCOME_INVALID");
        await using var connection=await fonte.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);await ConferirAsync(connection,tx,ct,ambiente);
        await using var command=new NpgsqlCommand("""
            update fugates.protocolo_mm_pedido_compra set estado=$3,http_status=$4,protocolo_id=$5,mensagem_segura=$6,atualizado_em=now()
            where reserva_id=$1 and chave_natural=$2 and estado='INDETERMINADO' and http_status is null
            """,connection,tx);
        command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(key);command.Parameters.AddWithValue(result.Estado);
        command.Parameters.Add(new(){NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Smallint,Value=result.Http is {} h?(object)(short)h:DBNull.Value});
        command.Parameters.Add(new(){NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Bigint,Value=(object?)result.ProtocoloId??DBNull.Value});
        command.Parameters.AddWithValue(result.Mensagem.Length<=400?result.Mensagem:result.Mensagem[..400]);
        if(await command.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("MM_OUTCOME_NOT_PERSISTED");
        await tx.CommitAsync(ct);
    }
}
