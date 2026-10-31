using System.Data;
using System.Data.Common;
using System.Text.Json;
using FugaTES.Aplicacao.Servicos.Recebimento;
namespace FugaTES.Persistencia.Recebimento;
public static class ToledoSapContractPartnerRead
{
 public const string Sql="""
 select v.numero_contrato,v.versao,v.integracao_id,v.fingerprint_sha256,i.fingerprint_sha256,
 v.payload_contrato::text,v.criado_em,i.origem,i.destino,i.operacao,i.chave_negocio
 from fugates.contrato_sap_toledo_versao v join fugates.integracao i on i.id=v.integracao_id
 where ltrim(v.numero_contrato,'0')=any(@contracts) and v.criado_em<=@received
 order by v.numero_contrato,v.versao desc limit 501;
 """;
 public static async Task<IReadOnlyList<ToledoSapContratoFonte>> ReadAsync(DbConnection c,DbTransaction tx,string original,DateTimeOffset received,CancellationToken ct)
 {
  using var doc=JsonDocument.Parse(original);var items=doc.RootElement.EnumerateObject().Single(p=>p.Name.Equals("to_Pesagem",StringComparison.OrdinalIgnoreCase)).Value;
  var contracts=items.EnumerateArray().Select(i=>i.EnumerateObject().Single(p=>p.Name.Equals("ContratoACM",StringComparison.OrdinalIgnoreCase)).Value)
   .Select(x=>ToledoSapContractPartners.NumericKey(x.ValueKind==JsonValueKind.String?x.GetString():x.GetRawText())).ToArray();
  if(contracts.Length is <1 or >1000||contracts.Any(x=>x is null))throw new InvalidOperationException("SAP_CONTRACT_BINDING_INVALID");
  await using var command=c.CreateCommand();command.Transaction=tx;command.CommandText=Sql;command.CommandTimeout=5;
  var list=command.CreateParameter();list.ParameterName="contracts";list.Value=contracts.Distinct(StringComparer.Ordinal).ToArray();command.Parameters.Add(list);
  var time=command.CreateParameter();time.ParameterName="received";time.DbType=DbType.DateTimeOffset;time.Value=received;command.Parameters.Add(time);
  await using var r=await command.ExecuteReaderAsync(ct);var result=new List<ToledoSapContratoFonte>();
  while(await r.ReadAsync(ct))result.Add(new(r.GetString(0),r.GetInt64(1),r.GetGuid(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetFieldValue<DateTimeOffset>(6),r.GetString(7),r.GetString(8),r.GetString(9),r.GetString(10)));
  return result;
 }
}
