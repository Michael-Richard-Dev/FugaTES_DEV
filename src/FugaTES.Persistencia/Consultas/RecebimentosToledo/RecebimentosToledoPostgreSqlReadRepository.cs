using System.Data;
using System.Text.Json;
using System.Security.Cryptography;
using FugaTES.Aplicacao.Servicos.Recebimento;
using System.Data.Common;
using FugaTES.Aplicacao.Consultas.RecebimentosToledo;
using FugaTES.Persistencia.Consultas.Monitor;
namespace FugaTES.Persistencia.Consultas.RecebimentosToledo;

// Pure read side. It never invokes callback storage, motor, Guardian or SAP.
public sealed partial class RecebimentosToledoPostgreSqlReadRepository(DbDataSource source,string? roleEsperada=null):IRecebimentosToledoReadRepository,IToledoSapJsonReadRepository
{
 readonly string? expectedRole=ValidateRole(roleEsperada);
 static string? ValidateRole(string? role)
 {
  if(role is not null&&!MonitorPostgreSqlReadRepository.RolesDeLeituraAutorizadas.Contains(role,StringComparer.Ordinal))
   throw new InvalidOperationException("TOLEDO_READ_ROLE_NOT_AUTHORIZED");
  return role;
 }
 public const string SqlLista="""
 with recibos as (
  select r.recibo_id,r.origem,r.centro,r.id_pesagem,r.data_pesagem_local,r.recebido_em,r.estado,r.origem_autenticada
  from fugates.toledo_pesagem_recebida r
  where (@busca is null or r.id_pesagem=@busca or r.recibo_id=@recibo
   or exists(select 1 from fugates.toledo_pesagem_item f where f.recibo_id=r.recibo_id and f.contrato_acm=@busca))
  order by r.recebido_em desc,r.recibo_id desc limit 101
 )
 select r.recibo_id,r.origem,r.centro,r.id_pesagem,r.data_pesagem_local,r.recebido_em,r.estado,r.origem_autenticada,
  coalesce(string_agg(distinct i.contrato_acm, ', ' order by i.contrato_acm),''),count(i.recibo_id)::int,
  coalesce(string_agg(distinct i.estado_correlacao, ', ' order by i.estado_correlacao),'SEM_ITEM_PERSISTIDO'),
  sap.id,sap.estado,sap.tentativas,sap.ativo,sap.diagnostico,sap.fingerprint_sha256,sap.atualizado_em,sap.origem_comprovada
 from recibos r left join fugates.toledo_pesagem_item i on i.recibo_id=r.recibo_id
 left join lateral (
   select q.id,q.estado,q.fingerprint_sha256,q.atualizado_em,
     (select count(*)::int from fugates.tentativa_integracao t where t.integracao_id=q.id) tentativas,
     (q.worker_proprietario is not null or q.lease_expira_em is not null) ativo,
     (select a.diagnostico from fugates.auditoria_integracao a where a.integracao_id=q.id and a.evento='toledo_sap_resultado'
        order by a.criada_em desc,a.id desc limit 1) diagnostico,
     exists(select 1 from fugates.auditoria_integracao a where a.integracao_id=q.id and a.evento='toledo_sap_admitido') origem_comprovada
   from fugates.integracao q where q.chave_negocio=r.recibo_id::text and q.origem='GUARDIAN' and q.destino='SAP'
     and q.operacao='SAP_DEVOLUTIVA_TOLEDO_DOCUMENTO'
   order by q.criado_em desc,q.id desc limit 1
 ) sap on true
 group by r.recibo_id,r.origem,r.centro,r.id_pesagem,r.data_pesagem_local,r.recebido_em,r.estado,r.origem_autenticada,
  sap.id,sap.estado,sap.tentativas,sap.ativo,sap.diagnostico,sap.fingerprint_sha256,sap.atualizado_em,sap.origem_comprovada
 order by r.recebido_em desc,r.recibo_id desc;
 """;
 public const string SqlJson="select recibo_id,documento_json::text,fingerprint_sha256 from fugates.toledo_pesagem_recebida where recibo_id=@recibo;";
 public async Task<RecebimentosToledoSnapshot> ConsultarRecebimentosAsync(string? busca,CancellationToken ct)
 {
  var term=RecebimentosToledoFiltro.Normalizar(busca);
  await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
  try
  {
   await Protect(c,tx,ct);
   await using var command=Cmd(c,tx,SqlLista);
   Param(command,"busca",DbType.String,term);Param(command,"recibo",DbType.Guid,Guid.TryParse(term,out var id)?id:null);
   await using var r=await command.ExecuteReaderAsync(ct);var rows=new List<RecebimentoToledoLinha>();
   while(await r.ReadAsync(ct))
   {
    var row=new RecebimentoToledoLinha(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetDateTime(4),
      r.GetFieldValue<DateTimeOffset>(5),r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.GetString(8),r.GetInt32(9),r.GetString(10));
    if(r.FieldCount>=19&&!r.IsDBNull(11))
    {
     string code="SEM_RESULTADO_REGISTRADO";int? http=null;bool post=true;
     if(!r.IsDBNull(15))try{using var diag=JsonDocument.Parse(r.GetString(15));
       code=diag.RootElement.GetProperty("codigo").GetString()??"RESULTADO_NAO_VERIFICADO";
       if(diag.RootElement.GetProperty("httpStatus").ValueKind==JsonValueKind.Number)http=diag.RootElement.GetProperty("httpStatus").GetInt32();
       post=diag.RootElement.GetProperty("postIniciado").GetBoolean();}
      catch{code="RESULTADO_NAO_VERIFICADO";}
     var snapshot=new DevolutivaToledoSapSnapshot(row.ReciboId,r.GetGuid(11),r.GetString(12),code,http,r.GetInt32(13),r.GetBoolean(14),
      r.GetString(12)=="RESULTADO_INDETERMINADO",r.GetBoolean(18),row.Contratos.TrimStart('0')=="986801",!post&&code=="FalhaAntesDoPost",r.GetString(16),"",r.GetFieldValue<DateTimeOffset>(17));
     snapshot=snapshot with{Revisao=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot)))};
     row=row with{DevolutivaSap=snapshot};
    }
    rows.Add(row);
   }
   return new(rows.Take(100).ToArray(),rows.Count>100,DateTimeOffset.UtcNow);
  }
  finally{await tx.RollbackAsync(CancellationToken.None);}
 }
 public async Task<RecebimentoToledoDocumento> ObterJsonRecebidoAsync(Guid reciboId,CancellationToken ct)
 {
  if(reciboId==Guid.Empty)throw new ArgumentException("ReciboId invalido.");
  await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
  try
  {
   await Protect(c,tx,ct);await using var command=Cmd(c,tx,SqlJson);Param(command,"recibo",DbType.Guid,reciboId);
   await using var r=await command.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))throw new KeyNotFoundException();
   return new(r.GetGuid(0),r.GetString(1),r.GetString(2));
  }
  finally{await tx.RollbackAsync(CancellationToken.None);}
 }
 async Task Protect(DbConnection c,DbTransaction tx,CancellationToken ct)
 {
  await using(var setup=Cmd(c,tx,"set transaction read only; set local statement_timeout='5s'; set local lock_timeout='1s';"))await setup.ExecuteNonQueryAsync(ct);
  await using var guard=Cmd(c,tx,expectedRole is null?MonitorPostgreSqlReadRepository.SqlIdentidadeAcm:MonitorPostgreSqlReadRepository.SqlIdentidadeRole);
  if(expectedRole is not null)Param(guard,"role",DbType.String,expectedRole);
  if(await guard.ExecuteScalarAsync(ct) is not true)throw new InvalidOperationException("TOLEDO_READ_DATABASE_IDENTITY_REJECTED");
 }
 static DbCommand Cmd(DbConnection c,DbTransaction tx,string sql){var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;cmd.CommandTimeout=6;return cmd;}
 static void Param(DbCommand c,string name,DbType type,object? value){var p=c.CreateParameter();p.ParameterName=name;p.DbType=type;p.Value=value??DBNull.Value;c.Parameters.Add(p);}
}
