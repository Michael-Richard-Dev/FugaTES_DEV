using System.Security.Cryptography;
using System.Text.Json;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Aplicacao.Servicos.Recebimento;
using Npgsql;
namespace FugaTES.Persistencia.Recebimento;

// Shares existing integration/attempt/audit tables; no schema change and no receipt mutation.
public sealed class ReenvioToledoSapPostgreSql(NpgsqlDataSource source):IToledoSapRetryStore,IToledoSapAdmissionProof
{
 public async Task<bool> ProvarAdmissaoAsync(Guid receipt,Guid integration,ToledoSapOperatorWindow window,CancellationToken ct)
 {
  if(!window.Valida)return false;
  await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
  try
  {
   await using(var protect=new NpgsqlCommand("set transaction read only;set local statement_timeout='5s';set local lock_timeout='1s';",c,tx))await protect.ExecuteNonQueryAsync(ct);
   return await ProvarNaTransacaoAsync(c,tx,receipt,integration,window,ct);
  }
  finally{await tx.RollbackAsync(CancellationToken.None);}
 }
 static async Task<bool> ProvarNaTransacaoAsync(NpgsqlConnection c,NpgsqlTransaction tx,Guid receipt,Guid integration,ToledoSapOperatorWindow window,CancellationToken ct)
 {
   const string sql="""
   select exists(select 1 from fugates.integracao i join fugates.toledo_pesagem_recebida r on r.recibo_id::text=i.chave_negocio
    where i.id=@integration and r.recibo_id=@receipt and i.origem='GUARDIAN' and i.destino='SAP' and i.operacao=@operation
     and r.origem_autenticada=@source and r.recebido_em>=@start and (@continuous or r.recebido_em<@end) and i.criado_em>=@start and (@continuous or i.criado_em<@end)
     and clock_timestamp()>=@start and (@continuous or clock_timestamp()<@end)
     and exists(select 1 from fugates.auditoria_integracao a where a.integracao_id=i.id and a.evento='toledo_sap_admitido'
       and a.diagnostico=@reference and a.criada_em>=@start and (@continuous or a.criada_em<@end)));
   """;
   await using var cmd=new NpgsqlCommand(sql,c,tx);cmd.CommandTimeout=5;
   cmd.Parameters.AddWithValue("integration",integration);cmd.Parameters.AddWithValue("receipt",receipt);cmd.Parameters.AddWithValue("operation",ToledoSapWork.Operacao);
   cmd.Parameters.AddWithValue("source",window.OrigemAdmitida);cmd.Parameters.AddWithValue("start",window.InicioUtc);cmd.Parameters.AddWithValue("end",window.ContinuoAutorizado?window.InicioUtc:window.FimUtc);cmd.Parameters.AddWithValue("continuous",window.ContinuoAutorizado);cmd.Parameters.AddWithValue("reference",window.ReferenciaAutorizacao);
   return await cmd.ExecuteScalarAsync(ct) is true;
 }
 public async Task<DevolutivaToledoSapSnapshot?> LerAsync(Guid receipt,CancellationToken ct)
 {
  await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
  try
  {await using(var protect=new NpgsqlCommand("set transaction read only;set local statement_timeout='5s';set local lock_timeout='1s';",c,tx))await protect.ExecuteNonQueryAsync(ct);return await Snapshot(c,tx,receipt,false,ct);}
  finally{await tx.RollbackAsync(CancellationToken.None);}
 }
 public async Task<ReciboComandoToledoSap> AdmitirAsync(ComandoReenvioToledoSap cmd,AcmRetryActor actor,ToledoSapRetryApproval approval,CancellationToken ct)
 {
  ReciboComandoToledoSap Block()=>new(cmd.ComandoId,cmd.ReciboId,"BLOCKED");
  await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
  try
  {
   await using(var limit=new NpgsqlCommand("set local statement_timeout='5s';set local lock_timeout='1s';",c,tx))await limit.ExecuteNonQueryAsync(ct);
   var s=await Snapshot(c,tx,cmd.ReciboId,true,ct);
   await using var clock=new NpgsqlCommand("select clock_timestamp();",c,tx);var now=(DateTime) (await clock.ExecuteScalarAsync(ct))!;
   if(!ReenvioToledoSapService.Autorizado(approval,actor,cmd.ReciboId,new DateTimeOffset(now))||s is null||
     s.IntegracaoId!=cmd.IntegracaoId||s.IntegracaoId!=approval.IntegracaoId||s.Revisao!=cmd.Revisao||s.Revisao!=approval.Revisao||
     !cmd.Confirmado||!AcmManualRetryService.SafeJustification(cmd.Justificativa)||!DevolutivaToledoSapPolicy.Avaliar(s,true).Permitido)
   {await tx.RollbackAsync(ct);return Block();}
   if(approval.EscopoAdmissao is{} scope&&(!scope.Valida||scope.OperadorSid!=actor.Sid||scope.ReferenciaAutorizacao!=approval.ReferenciaAutorizacao||scope.InicioUtc!=approval.InicioUtc||scope.FimUtc!=approval.FimUtc||scope.ContinuoAutorizado!=approval.ContinuoAutorizado||
      !await ProvarNaTransacaoAsync(c,tx,cmd.ReciboId,s.IntegracaoId,scope,ct)))
   {await tx.RollbackAsync(ct);return Block();}
   const string update="""
   update fugates.integracao i set estado='AGUARDANDO_REPROCESSAMENTO',tipo_disparo_pendente='REPROCESSAMENTO',
       proxima_tentativa_em=clock_timestamp(),atualizado_em=clock_timestamp()
   where i.id=@id and i.operacao=@operation and i.estado in ('FALHA_DEFINITIVA','AGUARDANDO_ACAO_MANUAL')
     and i.fingerprint_sha256=@hash and i.worker_proprietario is null and i.lease_expira_em is null
     and i.fase_envio='RESULTADO_RECEBIDO'
     and exists(select 1 from pg_catalog.pg_locks l where l.locktype='advisory' and l.classid=165165 and l.objid=2801 and l.granted)
     and not exists(select 1 from fugates.tentativa_integracao t where t.integracao_id=i.id and t.desfecho in ('INDETERMINADA','EM_EXECUCAO'))
     and not exists(select 1 from fugates.auditoria_integracao a where a.evento='toledo_sap_reenvio_solicitado'
          and (case when a.evento='toledo_sap_reenvio_solicitado' then a.diagnostico::jsonb->>'commandId' end)=@command);
   """;
   await using(var write=new NpgsqlCommand(update,c,tx))
   {write.Parameters.AddWithValue("id",s.IntegracaoId);write.Parameters.AddWithValue("operation",ToledoSapWork.Operacao);write.Parameters.AddWithValue("hash",s.Fingerprint);write.Parameters.AddWithValue("command",cmd.ComandoId.ToString("D"));if(await write.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return Block();}}
   var diagnostic=JsonSerializer.Serialize(new{commandId=cmd.ComandoId,reciboId=cmd.ReciboId,operatorSid=actor.Sid,justificativa=cmd.Justificativa,estadoAnterior=s.Estado,revision=s.Revisao,authorizationReference=approval.ReferenciaAutorizacao});
   const string audit="""
   insert into fugates.auditoria_integracao(id,integracao_id,evento,criada_em,correlation_id,tentativa_id,diagnostico)
   select @audit,i.id,'toledo_sap_reenvio_solicitado',clock_timestamp(),i.correlation_id,i.tentativa_atual_id,@diagnostic
   from fugates.integracao i where i.id=@id;
   """;
   await using(var write=new NpgsqlCommand(audit,c,tx)){write.Parameters.AddWithValue("audit",cmd.ComandoId);write.Parameters.AddWithValue("id",s.IntegracaoId);write.Parameters.AddWithValue("diagnostic",diagnostic);if(await write.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("TOLEDO_SAP_COMMAND_AUDIT_FAILED");}
   await tx.CommitAsync(ct);return new(cmd.ComandoId,cmd.ReciboId,"QUEUED");
  }
  catch{try{await tx.RollbackAsync(CancellationToken.None);}catch{}throw;}
 }
 static async Task<DevolutivaToledoSapSnapshot?> Snapshot(NpgsqlConnection c,NpgsqlTransaction tx,Guid receipt,bool locked,CancellationToken ct)
 {
  const string sql="""
  select i.id,i.estado,i.fingerprint_sha256,i.atualizado_em,
   (i.worker_proprietario is not null or i.lease_expira_em is not null),
   (select count(*)::int from fugates.tentativa_integracao t where t.integracao_id=i.id),
   (select a.diagnostico from fugates.auditoria_integracao a where a.integracao_id=i.id and a.evento='toledo_sap_resultado' order by a.criada_em desc,a.id desc limit 1),
   exists(select 1 from fugates.auditoria_integracao a where a.integracao_id=i.id and a.evento='toledo_sap_admitido'),
   exists(select 1 from fugates.toledo_pesagem_item f where f.recibo_id=r.recibo_id and ltrim(f.contrato_acm,'0')='986801')
  from fugates.integracao i join fugates.toledo_pesagem_recebida r on r.recibo_id::text=i.chave_negocio
  where r.recibo_id=@receipt and i.origem='GUARDIAN' and i.destino='SAP' and i.operacao=@operation
  order by i.criado_em desc,i.id desc limit 2
  """;
  await using var cmd=new NpgsqlCommand(sql+(locked?" for update of i,r;":";"),c,tx);cmd.CommandTimeout=5;cmd.Parameters.AddWithValue("receipt",receipt);cmd.Parameters.AddWithValue("operation",ToledoSapWork.Operacao);
  await using var read=await cmd.ExecuteReaderAsync(ct);if(!await read.ReadAsync(ct))return null;
  var id=read.GetGuid(0);var state=read.GetString(1);var hash=read.GetString(2);var updated=read.GetFieldValue<DateTimeOffset>(3);var active=read.GetBoolean(4);var attempts=read.GetInt32(5);var proven=read.GetBoolean(7);var synthetic=read.GetBoolean(8);
  var code="SEM_RESULTADO_REGISTRADO";int? http=null;var post=true;
  if(!read.IsDBNull(6))try{using var j=JsonDocument.Parse(read.GetString(6));code=j.RootElement.GetProperty("codigo").GetString()??"RESULTADO_NAO_VERIFICADO";if(j.RootElement.GetProperty("httpStatus").ValueKind==JsonValueKind.Number)http=j.RootElement.GetProperty("httpStatus").GetInt32();post=j.RootElement.GetProperty("postIniciado").GetBoolean();}catch{code="RESULTADO_NAO_VERIFICADO";}
  if(await read.ReadAsync(ct))return null; // Never choose one of multiple uncertain linkages.
  var s=new DevolutivaToledoSapSnapshot(receipt,id,state,code,http,attempts,active,state=="RESULTADO_INDETERMINADO",proven,synthetic,!post&&code=="FalhaAntesDoPost",hash,"",updated);
  return s with{Revisao=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(s)))};
 }
}
