using System.Data;
using System.Text.Json;
using FugaTES.Aplicacao.Servicos.Recebimento;
using Npgsql;

namespace FugaTES.Callback.Http;

// Observation only. No writer, dispatcher, retry command or SAP transport is reachable here.
public interface IToledoSapCallbackOutcomeRead
{
 Task<ToledoSapCallbackSnapshot?> ReadAsync(Guid receipt,Guid integration,CancellationToken ct);
}
public sealed record ToledoSapCallbackAudit(string Event,Guid Attempt,long Fence,string Worker,
 string? RequestSha256,string? PayloadVersion,RetornoToledoSap? Result);
public sealed record ToledoSapCallbackSnapshot(Guid Receipt,Guid Integration,string OriginalHash,string IntegrationHash,string Correlation)
{
 public int Integrations {get;init;}
 public bool Admitted {get;init;}
 public bool Synthetic {get;init;}
 public string State {get;init;}="";
 public string SendPhase {get;init;}="";
 public Guid? Attempt {get;init;}
 public long Fence {get;init;}
 public bool Active {get;init;}
 public string? AttemptOutcome {get;init;}
 public string? AttemptPhase {get;init;}
 public bool EvidenceValid {get;init;}=true;
 public IReadOnlyList<ToledoSapCallbackAudit> Audits {get;init;}=[];
}

public sealed class ToledoSapCallbackOutcomeRead(NpgsqlDataSource source):IToledoSapCallbackOutcomeRead
{
 public const string Sql="""
 select current_setting('cluster_name')='18/fugatesacmdev' and current_setting('port')='5433'
   and current_database()='fugates_dev' and current_user='fugates_acm_hml_app'
   and session_user='fugates_acm_hml_app' and current_setting('transaction_read_only')='on',
   r.recibo_id,i.id,r.fingerprint_sha256,i.fingerprint_sha256,i.correlation_id,
   (select count(*)::int from fugates.integracao x where x.chave_negocio=r.recibo_id::text
     and x.origem='GUARDIAN' and x.destino='SAP' and x.operacao=@operation),
   length(trim(coalesce(r.origem_autenticada,'')))>0 and exists(select 1 from fugates.auditoria_integracao a
     where a.integracao_id=i.id and a.evento='toledo_sap_admitido'),
   exists(select 1 from fugates.toledo_pesagem_item f where f.recibo_id=r.recibo_id and ltrim(f.contrato_acm,'0')='986801'),
   i.estado,i.fase_envio,i.tentativa_atual_id,i.fencing_token,
   (i.worker_proprietario is not null or i.lease_expira_em is not null),t.desfecho,t.fase_envio,
   coalesce((select json_agg(e) from (
     select a.evento,a.tentativa_id,a.fencing_token,a.worker,a.diagnostico
     from fugates.auditoria_integracao a where a.integracao_id=i.id
       and a.tentativa_id=i.tentativa_atual_id and a.fencing_token=i.fencing_token
       and a.evento in ('toledo_sap_documento_preparado','toledo_sap_resultado')
     order by a.criada_em,a.id limit 3) e),'[]'::json)
 from fugates.toledo_pesagem_recebida r join fugates.integracao i on i.chave_negocio=r.recibo_id::text
 left join fugates.tentativa_integracao t on t.id=i.tentativa_atual_id and t.integracao_id=i.id and t.fencing_token=i.fencing_token
 where r.recibo_id=@receipt and i.id=@integration and i.origem='GUARDIAN' and i.destino='SAP' and i.operacao=@operation;
 """;
 public async Task<ToledoSapCallbackSnapshot?> ReadAsync(Guid receipt,Guid integration,CancellationToken ct)
 {
  if(receipt==Guid.Empty||integration==Guid.Empty)return null;
  await using var c=await source.OpenConnectionAsync(ct);
  await using var tx=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
  try
  {
   await using(var protect=new NpgsqlCommand("set transaction read only;set local statement_timeout='3s';set local lock_timeout='1s';",c,tx))await protect.ExecuteNonQueryAsync(ct);
   await using var cmd=new NpgsqlCommand(Sql,c,tx){CommandTimeout=4};
   cmd.Parameters.AddWithValue("receipt",receipt);cmd.Parameters.AddWithValue("integration",integration);cmd.Parameters.AddWithValue("operation",ToledoSapWork.Operacao);
   await using var row=await cmd.ExecuteReaderAsync(ct);
   if(!await row.ReadAsync(ct)||!row.GetBoolean(0))return null;
   var snapshot=new ToledoSapCallbackSnapshot(row.GetGuid(1),row.GetGuid(2),row.GetString(3),row.GetString(4),row.GetString(5))
   {
    Integrations=row.GetInt32(6),Admitted=row.GetBoolean(7),Synthetic=row.GetBoolean(8),State=row.GetString(9),SendPhase=row.GetString(10),
    Attempt=row.IsDBNull(11)?null:row.GetGuid(11),Fence=row.GetInt64(12),Active=row.GetBoolean(13),
    AttemptOutcome=row.IsDBNull(14)?null:row.GetString(14),AttemptPhase=row.IsDBNull(15)?null:row.GetString(15)
   };
   try{snapshot=snapshot with{Audits=ParseAudits(row.GetString(16))};}
   catch(JsonException){snapshot=snapshot with{EvidenceValid=false};}
   catch(InvalidOperationException){snapshot=snapshot with{EvidenceValid=false};}
   catch(ArgumentException){snapshot=snapshot with{EvidenceValid=false};}
   return await row.ReadAsync(ct)?null:snapshot;
  }
  finally{await tx.RollbackAsync(CancellationToken.None);}
 }
 static IReadOnlyList<ToledoSapCallbackAudit> ParseAudits(string raw)
 {
  using var doc=JsonDocument.Parse(raw,new(){MaxDepth=32});var result=new List<ToledoSapCallbackAudit>();
  foreach(var item in doc.RootElement.EnumerateArray())
  {
   var evt=item.GetProperty("evento").GetString()!;var id=item.GetProperty("tentativa_id").GetGuid();var fence=item.GetProperty("fencing_token").GetInt64();
   var worker=item.GetProperty("worker").GetString()??"";using var data=JsonDocument.Parse(item.GetProperty("diagnostico").GetString()!,new(){MaxDepth=32});var d=data.RootElement;
   if(evt=="toledo_sap_documento_preparado")
    result.Add(new(evt,id,fence,worker,d.GetProperty("RequestSha256").GetString(),d.GetProperty("PayloadVersion").GetString(),null));
   else
   {
    if(!Enum.TryParse<ResultadoToledoSapCodigo>(d.GetProperty("codigo").GetString(),false,out var code)||!Enum.IsDefined(code))throw new JsonException();
    string? Text(string name)=>d.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
    var outcome=new RetornoToledoSap(code,d.GetProperty("httpStatus").ValueKind==JsonValueKind.Number?d.GetProperty("httpStatus").GetInt32():null,d.GetProperty("postIniciado").GetBoolean())
     {EntityId=Text("entityId"),RequestSha256=Text("requestSha256"),ResponseSha256=Text("responseSha256"),PayloadVersion=Text("payloadVersion")};
    result.Add(new(evt,id,fence,worker,outcome.RequestSha256,outcome.PayloadVersion,outcome));
   }
  }
  return result;
 }
}
