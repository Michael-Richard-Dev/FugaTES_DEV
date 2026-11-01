using FugaTES.Aplicacao.Consultas.RecebimentosToledo;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Servicos.Recebimento;
namespace FugaTES.Integracao.SAP.Http;

public static partial class ToledoSapODataPayload
{
 // Audit reconstruction is not an outbound fallback. Exact SHA equality is mandatory, including for historical policies.
 public static (ToledoSapPreparedPayload Payload,string Projection)? ReproduceAudited(string original,ToledoSapPayloadOptions options,string expectedHash,string expectedVersion,IReadOnlyList<ToledoSapParceiroAuditado>? partners=null,string? partnerVersion=null,ToledoDeliveryPartnerProof? delivery=null)
 {
  if(expectedVersion!=ToledoSapPreparedPayload.Version||!HashValid(expectedHash))return null;
  if(partnerVersion is not null&&(partnerVersion!=ToledoSapContractPartners.Version||partners?.Count is not >0))return null;
  if(partners?.Count>0&&partnerVersion!=ToledoSapContractPartners.Version)return null;
  if(delivery is not null){try{original=ToledoDeliveryPartners.Apply(original,delivery);}catch{return null;}}
  foreach(var projection in new[]{Projection.Current,Projection.BeforeNameTruncation,Projection.BeforeDecimalAndUserNormalization,Projection.DirectionOnly,Projection.LegacyNumeric})
  {
   try{var p=PrepareCore(original,options,projection,false,partners?.Count>0?partners:null);if(string.Equals(p.Sha256,expectedHash,StringComparison.OrdinalIgnoreCase))return(p,projection.ToString());}
   catch(ToledoSapPayloadException){}catch(System.Text.Json.JsonException){}
  }
  return null;
 }
 internal static bool HashValid(string? s)=>s?.Length==64&&s.All(char.IsAsciiHexDigit);
}

// Pure observation: no HTTP, database command, worker or send gateway can be reached here.
public static class ToledoSapDocumentoLeitura
{
 public static ToledoSapJsonVisualizacao Preparar(ToledoSapJsonFonte source)
 {
  var receipt=source.Original.ReciboId;
  ToledoSapJsonVisualizacao Block(string reason)=>new(receipt,null,"INDISPONIVEL",reason,null,null,null,null,null,null);
  try
  {
   if(receipt==Guid.Empty||!ToledoSapODataPayload.HashValid(source.Original.FingerprintSha256)||
    !string.Equals(RegistrarPesagemToledoCasoDeUso.Fingerprint(source.Original.DocumentoJson),source.Original.FingerprintSha256,StringComparison.OrdinalIgnoreCase))
     return Block("A integridade do JSON recebido não foi comprovada.");
   if(source.IntegracoesEncontradas>1||source.EvidenciaTruncada)return Block("A evidência do journal é ambígua ou incompleta.");
   if(source.IntegracoesEncontradas<0||source.IntegracoesEncontradas>0&&(source.IntegracaoId is null||source.IntegracaoId==Guid.Empty||
    !string.Equals(ToledoSapWork.Fingerprint(receipt,source.Original.FingerprintSha256),source.FingerprintIntegracao,StringComparison.OrdinalIgnoreCase)))
     return Block("A correlação entre recibo e integração não foi comprovada.");
   var events=source.Eventos.OrderByDescending(x=>x.CriadoEm).ThenByDescending(x=>x.Id).ToArray();
   if(source.IntegracoesEncontradas==0&&events.Length>0||events.Any(x=>!x.Valido||x.Evento is not("toledo_sap_documento_preparado" or "toledo_sap_resultado")))return Block("Há evidência inválida no journal; não é seguro reconstruir o envio.");
   var prepared=events.Where(x=>x.Evento=="toledo_sap_documento_preparado").ToArray();
   if(prepared.Length==0)
   {
    if(events.Length!=0)return Block("Existe resultado sem o documento preparado auditado.");
    var preview=ToledoSapODataPayload.PrepareWithContractPartners(ToledoDeliveryPartners.Apply(source.Original.DocumentoJson,source.DeliveryPartners),new("America/Sao_Paulo"),source.RecebidoEm,source.ContratosSap);
    return new(receipt,preview.Json,"PREVIA_NAO_ENVIADA","Prévia calculada com as regras atuais. Não comprova envio nem autorização para enviar.",preview.Sha256,ToledoSapPreparedPayload.Version,null,null,null,"Current");
   }
   var p=prepared[0];
   if(p.TentativaId is null||p.TentativaId==Guid.Empty||p.EpochMilliseconds is null||!ToledoSapODataPayload.HashValid(p.RequestSha256)||
    prepared.Count(x=>x.TentativaId==p.TentativaId)!=1)return Block("A preparação da tentativa não possui identidade e hash inequívocos.");
   if(events.Any(x=>x.CriadoEm>p.CriadoEm&&x.TentativaId!=p.TentativaId))return Block("A tentativa mais recente não pode ser conciliada ao documento preparado.");
   var results=events.Where(x=>x.Evento=="toledo_sap_resultado"&&x.TentativaId==p.TentativaId).ToArray();
   if(results.Length>1)return Block("Existem resultados concorrentes para a mesma tentativa.");
   var result=results.SingleOrDefault();
   if(result is not null&&(result.CriadoEm<p.CriadoEm||result.PostIniciado is null||
    !string.Equals(result.RequestSha256,p.RequestSha256,StringComparison.OrdinalIgnoreCase)||result.PayloadVersion!=p.PayloadVersion))
     return Block("O resultado não corresponde ao hash e à versão da preparação.");
   var match=ToledoSapODataPayload.ReproduceAudited(source.Original.DocumentoJson,new("America/Sao_Paulo"),p.RequestSha256!,p.PayloadVersion??"",p.ParceirosContrato,p.ParceirosVersao,p.DeliveryPartners);
   if(match is null||match.Value.Payload.EpochMilliseconds!=p.EpochMilliseconds)return Block("O JSON não corresponde ao hash auditado. O documento recebido não será exibido como envio SAP.");
   bool sent=result?.PostIniciado==true;
   var message=sent?"JSON da tentativa de envio: conteúdo conferido pelo SHA-256 do journal. Resultado empresarial deve ser consultado separadamente.":
    "JSON preparado e conferido pelo journal; não há comprovação de POST nesta tentativa.";
   return new(receipt,match.Value.Payload.Json,sent?"ENVIADO_CONFERIDO":"PREPARADO_SEM_POST_COMPROVADO",message,
    match.Value.Payload.Sha256,p.PayloadVersion,p.TentativaId,p.CriadoEm,result?.HttpStatus,match.Value.Projection);
  }
  catch(ToledoDeliveryPartnerException ex){return Block("Validação do parceiro de entrega indisponível: "+ex.Code+". JSON recebido preservado.");}
  catch(ToledoSapContractPartnerException ex){return Block("Parceiro do contrato SAP não comprovado: "+ex.Code+". O parceiro de entrega e o documento Guardian foram preservados.");}
  catch(ToledoSapPayloadException ex){return Block("Prévia SAP indisponível: "+ex.Code+". O documento original foi preservado.");}
  catch{return Block("Não foi possível validar o documento SAP desta pesagem.");}
 }
}
