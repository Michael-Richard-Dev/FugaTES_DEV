using System.Text.Json;
using FugaTES.Aplicacao.Servicos.Recebimento;
using Npgsql;
namespace FugaTES.Persistencia.Recebimento;

// Recovery is only for this typed operation and this recorded authorization. Never generic backlog recovery.
public static class ToledoSapLeaseRecovery
{
 public static async Task<int> RecuperarAsync(NpgsqlDataSource db,ToledoSapAdmissionWindow window,CancellationToken ct)
 {
  if(!window.Valida)throw new InvalidOperationException("TOLEDO_SAP_RECOVERY_SCOPE_INVALID");
  await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
  try
  {
   await using(var limit=new NpgsqlCommand("set local statement_timeout='5s';set local lock_timeout='1s';",c,tx))await limit.ExecuteNonQueryAsync(ct);
   const string select="""
   select i.id,i.tentativa_atual_id,i.fencing_token,i.worker_proprietario,i.correlation_id,i.fase_envio
   from fugates.integracao i
   where i.origem='GUARDIAN' and i.destino='SAP' and i.operacao=@operation and i.estado='EM_PROCESSAMENTO'
     and i.lease_expira_em<=clock_timestamp() and i.tentativa_atual_id is not null
     and exists(select 1 from fugates.auditoria_integracao a where a.integracao_id=i.id
       and ((a.evento='toledo_sap_admitido' and a.diagnostico=@reference and i.criado_em>=@start and (@continuous or i.criado_em<@end))
         or (a.evento='toledo_sap_reenvio_solicitado' and a.criada_em>=@start and (@continuous or a.criada_em<@end)
           and (case when a.evento='toledo_sap_reenvio_solicitado' then a.diagnostico::jsonb->>'authorizationReference' end)=@reference)))
   order by i.id limit 20 for update of i skip locked;
   """;
   var expired=new List<(Guid Id,Guid Attempt,long Fence,string? Worker,string Correlation,bool Unknown)>();
   await using(var command=new NpgsqlCommand(select,c,tx))
   {command.Parameters.AddWithValue("operation",ToledoSapWork.Operacao);command.Parameters.AddWithValue("reference",window.ReferenciaAutorizacao);command.Parameters.AddWithValue("start",window.InicioUtc);command.Parameters.AddWithValue("end",window.ContinuoAutorizado?window.InicioUtc:window.FimUtc);command.Parameters.AddWithValue("continuous",window.ContinuoAutorizado);
    await using var read=await command.ExecuteReaderAsync(ct);while(await read.ReadAsync(ct))expired.Add((read.GetGuid(0),read.GetGuid(1),read.GetInt64(2),read.IsDBNull(3)?null:read.GetString(3),read.GetString(4),read.GetString(5)!="NAO_INICIADO"));}
   foreach(var e in expired)
   {
    const string update="""
    update fugates.integracao set estado=@state,tipo_disparo_pendente=null,proxima_tentativa_em=null,
      worker_proprietario=null,lease_expira_em=null,fencing_token=fencing_token+1,
      fase_envio=case when @unknown then fase_envio else 'RESULTADO_RECEBIDO' end,atualizado_em=clock_timestamp()
    where id=@id and estado='EM_PROCESSAMENTO' and tentativa_atual_id=@attempt and fencing_token=@fence and lease_expira_em<=clock_timestamp();
    update fugates.tentativa_integracao set desfecho=@outcome,
      fase_envio=case when @unknown then fase_envio else 'RESULTADO_RECEBIDO' end
    where id=@attempt and integracao_id=@id and fencing_token=@fence and desfecho='EM_EXECUCAO';
    """;
    await using(var command=new NpgsqlCommand(update,c,tx)){command.Parameters.AddWithValue("state",e.Unknown?"RESULTADO_INDETERMINADO":"AGUARDANDO_ACAO_MANUAL");command.Parameters.AddWithValue("unknown",e.Unknown);command.Parameters.AddWithValue("id",e.Id);command.Parameters.AddWithValue("attempt",e.Attempt);command.Parameters.AddWithValue("fence",e.Fence);command.Parameters.AddWithValue("outcome",e.Unknown?"INDETERMINADA":"FALHA");if(await command.ExecuteNonQueryAsync(ct)!=2)throw new InvalidOperationException("TOLEDO_SAP_RECOVERY_FENCE_REJECTED");}
    var diagnostic=JsonSerializer.Serialize(new{codigo=e.Unknown?"ResultadoIndeterminado":"FalhaAntesDoPost",httpStatus=(int?)null,postIniciado=e.Unknown,reason="LEASE_EXPIRED_NO_AUTOMATIC_RETRY"});
    const string audit="""
    insert into fugates.auditoria_integracao(id,integracao_id,evento,criada_em,worker,fencing_token,tentativa_id,correlation_id,diagnostico)
    values (@audit,@id,'toledo_sap_resultado',clock_timestamp(),@worker,@fence,@attempt,@correlation,@diagnostic);
    """;
    await using(var command=new NpgsqlCommand(audit,c,tx)){command.Parameters.AddWithValue("audit",Guid.NewGuid());command.Parameters.AddWithValue("id",e.Id);command.Parameters.AddWithValue("worker",NpgsqlTypes.NpgsqlDbType.Text,(object?)e.Worker??DBNull.Value);command.Parameters.AddWithValue("fence",e.Fence);command.Parameters.AddWithValue("attempt",e.Attempt);command.Parameters.AddWithValue("correlation",e.Correlation);command.Parameters.AddWithValue("diagnostic",diagnostic);if(await command.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("TOLEDO_SAP_RECOVERY_AUDIT_FAILED");}
   }
   await tx.CommitAsync(ct);return expired.Count;
  }
  catch{try{await tx.RollbackAsync(CancellationToken.None);}catch{}throw;}
 }
}
