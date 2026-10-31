using System.Data.Common;
using System.Text.Json;
using FugaTES.Aplicacao.Servicos.Recebimento;
namespace FugaTES.Persistencia.Recebimento;
public static class ToledoDeliveryPartnerRead
{
 public static async Task<ToledoDeliveryPartnerProof?> ReadAsync(DbConnection c,DbTransaction tx,Guid id,bool byReceipt,CancellationToken ct)
 {
  await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandTimeout=5;
  // byReceipt is exclusively the dedicated GET-only reader; application role uses integracao_id.
  cmd.CommandText="select diagnostico from fugates.auditoria_integracao where "+(byReceipt?"correlation_id=@id":"integracao_id=@id")+" and evento=@event limit 2;";
  var p=cmd.CreateParameter();p.ParameterName="id";p.DbType=byReceipt?System.Data.DbType.String:System.Data.DbType.Guid;p.Value=byReceipt?(object)$"toledo-sap:{id:D}":id;cmd.Parameters.Add(p);
  p=cmd.CreateParameter();p.ParameterName="event";p.Value=ToledoDeliveryPartners.Event;cmd.Parameters.Add(p);
  await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;
  var proof=JsonSerializer.Deserialize<ToledoDeliveryPartnerProof>(r.GetString(0));
  if(await r.ReadAsync(ct)||proof is null)throw new InvalidOperationException("TOLEDO_DELIVERY_BP_PROOF_AMBIGUOUS");return proof;
 }
}
