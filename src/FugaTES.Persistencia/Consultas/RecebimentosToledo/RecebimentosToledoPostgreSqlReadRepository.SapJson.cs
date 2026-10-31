using System.Data;
using System.Text.Json;
using FugaTES.Aplicacao.Consultas.RecebimentosToledo;
namespace FugaTES.Persistencia.Consultas.RecebimentosToledo;
public sealed partial class RecebimentosToledoPostgreSqlReadRepository
{
 public const string SqlIntegracaoJsonSap="""
 select id,fingerprint_sha256 from fugates.integracao
 where chave_negocio=@recibo_texto and origem='GUARDIAN' and destino='SAP'
 and operacao='SAP_DEVOLUTIVA_TOLEDO_DOCUMENTO' order by id limit 2;
 """;
 public const string SqlEvidenciaJsonSap="""
 select id,evento,tentativa_id,criada_em,diagnostico from fugates.auditoria_integracao
 where integracao_id=@integracao and evento in ('toledo_sap_documento_preparado','toledo_sap_resultado')
 order by criada_em desc,id desc limit 101;
 """;
 public async Task<ToledoSapJsonFonte> ObterFonteJsonSapAsync(Guid reciboId,CancellationToken ct)
 {
  if(reciboId==Guid.Empty)throw new ArgumentException("ReciboId invalido.");
  await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
  try
  {
   await Protect(c,tx,ct);RecebimentoToledoDocumento original;DateTimeOffset received;
   await using(var cmd=Cmd(c,tx,"select recibo_id,documento_json::text,fingerprint_sha256,recebido_em from fugates.toledo_pesagem_recebida where recibo_id=@recibo;"))
   {Param(cmd,"recibo",DbType.Guid,reciboId);await using var r=await cmd.ExecuteReaderAsync(ct);
    if(!await r.ReadAsync(ct))throw new KeyNotFoundException();original=new(r.GetGuid(0),r.GetString(1),r.GetString(2));received=r.GetFieldValue<DateTimeOffset>(3);}
   var delivery=await FugaTES.Persistencia.Recebimento.ToledoDeliveryPartnerRead.ReadAsync(c,tx,reciboId,true,ct);
   var contracts=await FugaTES.Persistencia.Recebimento.ToledoSapContractPartnerRead.ReadAsync(c,tx,original.DocumentoJson,received,ct);
   var integrations=new List<(Guid Id,string Hash)>();
   await using(var cmd=Cmd(c,tx,SqlIntegracaoJsonSap))
   {Param(cmd,"recibo_texto",DbType.String,reciboId.ToString("D"));await using var r=await cmd.ExecuteReaderAsync(ct);
    while(await r.ReadAsync(ct))integrations.Add((r.GetGuid(0),r.GetString(1)));}
   if(integrations.Count!=1)return new(original,null,null,integrations.Count,[],false){DeliveryPartners=delivery,RecebidoEm=received,ContratosSap=contracts};
   var integration=integrations[0];var events=new List<ToledoSapJsonEvento>();
   await using(var cmd=Cmd(c,tx,SqlEvidenciaJsonSap))
   {Param(cmd,"integracao",DbType.Guid,integration.Id);await using var r=await cmd.ExecuteReaderAsync(ct);
    while(await r.ReadAsync(ct))events.Add(ParseSapJsonEvento(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetFieldValue<DateTimeOffset>(3),r.IsDBNull(4)?null:r.GetString(4)));}
   return new(original,integration.Id,integration.Hash,1,events.Take(100).ToArray(),events.Count>100){DeliveryPartners=delivery,RecebidoEm=received,ContratosSap=contracts};
  }
  finally{await tx.RollbackAsync(CancellationToken.None);}
 }
 // Only whitelisted scalar audit fields leave persistence; arbitrary diagnostics never leave this endpoint.
 public static ToledoSapJsonEvento ParseSapJsonEvento(Guid id,string evt,Guid? attempt,DateTimeOffset at,string? diagnostic)
 {
  try
  {
   using var d=JsonDocument.Parse(diagnostic??"{}");var r=d.RootElement;
   string? Text(string name)=>r.TryGetProperty(name,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString():null;
   long? Long(string name)
   {if(!r.TryGetProperty(name,out var x)||x.ValueKind==JsonValueKind.Null)return null;if(x.ValueKind!=JsonValueKind.Number||!x.TryGetInt64(out var n))throw new JsonException();return n;}
   if(evt is not("toledo_sap_documento_preparado" or "toledo_sap_resultado"))throw new JsonException();
   bool prepared=evt=="toledo_sap_documento_preparado";
   string? code=prepared?null:Text("codigo");
   if(code?.Any(x=>!char.IsAsciiLetterOrDigit(x)&&x!='_')==true||code?.Length>100)throw new JsonException();
   var partnerVersion=prepared?Text("ParceirosVersao"):null;
   var partners=prepared&&r.TryGetProperty("ParceirosContrato",out var proof)?
    JsonSerializer.Deserialize<FugaTES.Aplicacao.Servicos.Recebimento.ToledoSapParceiroAuditado[]>(proof.GetRawText())??throw new JsonException():[];
   if(partnerVersion is not null&&(partnerVersion!=FugaTES.Aplicacao.Servicos.Recebimento.ToledoSapContractPartners.Version||partners.Length is <1 or >1000||partners.Any(x=>x is null||!x.Valido)))throw new JsonException();
   if(partnerVersion is null&&partners.Length>0)throw new JsonException();
   var delivery=prepared&&r.TryGetProperty("DeliveryPartners",out var dp)&&dp.ValueKind!=JsonValueKind.Null?
    JsonSerializer.Deserialize<FugaTES.Aplicacao.Servicos.Recebimento.ToledoDeliveryPartnerProof>(dp.GetRawText()):null;
   return new(id,evt,attempt,at,Text(prepared?"RequestSha256":"requestSha256"),Text(prepared?"PayloadVersion":"payloadVersion"),
    prepared?Long("EpochMilliseconds"):null,
    !prepared&&r.TryGetProperty("postIniciado",out var post)&&post.ValueKind is JsonValueKind.True or JsonValueKind.False?post.GetBoolean():null,
    !prepared&&Long("httpStatus") is long http?checked((int)http):null,code,true){DeliveryPartners=delivery,ParceirosVersao=partnerVersion,ParceirosContrato=partners};
  }
  catch{return new(id,evt,attempt,at,null,null,null,null,null,null,false);}
 }
}
