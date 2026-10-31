using FugaTES.Aplicacao.Servicos.Recebimento;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FugaTES.Aplicacao.Modelos.Recebimento;
namespace FugaTES.Integracao.SAP.Http;

public sealed class ToledoSapPayloadException(string code):Exception(code)
{ public string Code {get;}=code; }
public sealed record ToledoSapPayloadOptions(string? LocalTimeZoneId=null,bool RequireMeasuredQuality=true);
public sealed record ToledoSapPreparedPayload(string Json,string Sha256,string IdPesagem,string SistemaBalanca,string Centro,long EpochMilliseconds)
{ public const string Version="TOLEDO_SAP_ODATA_V1"; public ToledoDeliveryPartnerProof? DeliveryPartners {get;init;} public IReadOnlyList<FugaTES.Aplicacao.Servicos.Recebimento.ToledoSapParceiroAuditado> ParceirosContrato {get;init;}=[]; }

// Outbound projection: the stored Guardian document is never overwritten.
public static partial class ToledoSapODataPayload
{
 public static ToledoSapPreparedPayload Prepare( FugaTES.Aplicacao.Servicos.Recebimento.DocumentoToledoSap document,ToledoSapPayloadOptions options)
 {
  var projected=ToledoDeliveryPartners.Apply(document.DocumentoJson,document.DeliveryPartners);
  return PrepareWithContractPartners(projected,options,document.RecebidoEm,document.ContratosSap) with{DeliveryPartners=document.DeliveryPartners};
 }
 public static ToledoSapPreparedPayload PrepareWithContractPartners(string original,ToledoSapPayloadOptions options,DateTimeOffset received,IReadOnlyList<FugaTES.Aplicacao.Servicos.Recebimento.ToledoSapContratoFonte> sources)
 {
  var partners=FugaTES.Aplicacao.Servicos.Recebimento.ToledoSapContractPartners.Resolve(original,received,sources);
  return PrepareCore(original,options,Projection.Current,true,partners) with{ParceirosContrato=partners};
 }
 public static ToledoSapPreparedPayload Prepare(string original,ToledoSapPayloadOptions options)
  =>PrepareCore(original,options,Projection.Current,true);
 private enum Projection {Current,BeforeDecimalAndUserNormalization,DirectionOnly,LegacyNumeric}
 private static ToledoSapPreparedPayload PrepareCore(string original,ToledoSapPayloadOptions options,Projection projection,bool validateEntry,IReadOnlyList<FugaTES.Aplicacao.Servicos.Recebimento.ToledoSapParceiroAuditado>? partners=null)
 {
  if(Encoding.UTF8.GetByteCount(original)>1048576)throw Bad("SAP_PAYLOAD_TOO_LARGE");
  var root=JsonNode.Parse(original,new(){PropertyNameCaseInsensitive=false},new(){MaxDepth=32}) as JsonObject??throw Bad("SAP_PAYLOAD_OBJECT_REQUIRED");
  Names(root,["SistemaBalanca","DataPesagem","TipoPesagem","IdPesagem","UsuarioPesagem","UsuarioClassificacao","IdPlanejamento","Centro","IdAgendamento","IdTransportadora","PesoBruto","PesoTara","Material","PesoAntesDesconto","PesoAposDesconto","NomeMotorista","CpfMotorista","Placa","Observacao","to_QualidPes","to_Pesagem"]);
  foreach(var name in new[]{"IdPesagem","Centro","IdPlanejamento","IdAgendamento","IdTransportadora","Material"})
   if(root.ContainsKey(name))root[name]=Identifier(root[name],name,name is "IdPesagem" or "Centro");
  if(projection is Projection.Current or Projection.BeforeDecimalAndUserNormalization)root["IdPesagem"]=SapWeighingId(root["IdPesagem"]);
  if(projection==Projection.Current)
  {
   if(root.ContainsKey("UsuarioPesagem"))root["UsuarioPesagem"]=SapWeighingUser(Text(root["UsuarioPesagem"]));
   foreach(var name in new[]{"PesoBruto","PesoTara","PesoAntesDesconto","PesoAposDesconto"})NormalizeDecimalField(root,name);
  }
  var direction=SapWeighingType(root["TipoPesagem"]);
  root["TipoPesagem"]=projection==Projection.LegacyNumeric?(direction=="E"?"1":"2"):direction;
  var id=Identifier(root["IdPesagem"],"IdPesagem",true);var center=Identifier(root["Centro"],"Centro",true);
  if(!string.Equals(Text(root["SistemaBalanca"]),"Toledo",StringComparison.OrdinalIgnoreCase))throw Bad("SAP_SYSTEM_INVALID");
  root["SistemaBalanca"]="Toledo";
  var ms=Date(Text(root["DataPesagem"]),options.LocalTimeZoneId);root["DataPesagem"]=$"/Date({ms.ToString(CultureInfo.InvariantCulture)})/";
  if(root["to_QualidPes"] is not JsonArray input||input.Count>1000)throw Bad("SAP_QUALITY_ARRAY_REQUIRED");
  var qualities=new JsonArray();var keys=new HashSet<string>(StringComparer.Ordinal);
  foreach(var item in input)
  {
   if(item is not JsonObject q)throw Bad("SAP_QUALITY_OBJECT_REQUIRED");
   Names(q,["Tipo","Grupo","ValorAferido"]);var type=Text(q["Tipo"]);var value=Scalar(q["ValorAferido"]);
   if(projection==Projection.Current&&!string.IsNullOrWhiteSpace(value))value=NormalizeDecimalText(value);
   if(string.IsNullOrWhiteSpace(type)&&(string.IsNullOrWhiteSpace(value)||Decimal(value)==0m))continue;
   if(string.IsNullOrWhiteSpace(type)||string.IsNullOrWhiteSpace(value)||Decimal(value)<0)throw Bad("SAP_MEASURED_QUALITY_REQUIRED");
   // Proven Guardian aliases from the operator's working SAP contract; never invent a measurement.
   type=type switch {"1"=>"IMPUREZA","2"=>"UMIDADE",_=>type};
   if(!keys.Add(type))throw Bad("SAP_QUALITY_DUPLICATE");
   q["Tipo"]=type;
   q["ValorAferido"]=value;qualities.Add(q.DeepClone());
  }
  if(options.RequireMeasuredQuality&&qualities.Count==0)throw Bad("SAP_MEASURED_QUALITY_REQUIRED");
  root["to_QualidPes"]=qualities;
  if(root["to_Pesagem"] is not JsonArray weights||weights.Count is <1 or >1000)throw Bad("SAP_WEIGHING_ITEMS_REQUIRED");
  if(partners is not null&&(partners.Count!=weights.Count||partners.Any(x=>x is null||!x.Valido)||partners.Select(x=>x.Indice).Distinct().Count()!=partners.Count))throw Bad("SAP_CONTRACT_BINDING_INVALID");
  var indexes=new HashSet<int>();
  foreach(var item in weights)
  {
   if(item is not JsonObject w)throw Bad("SAP_WEIGHING_ITEM_REQUIRED");
   Names(w,["Indice","NrIndiceTotal","ContratoACM","IdParceiro","ParceiroNome","ParceiroCpfCnpj","IdParceiroEntrega","ParceiroEntregaNome","ObservacaoPesagem","PercentualDesdobro","PesoAntesDesconto","PesoAposDesconto","ChaveAcessoNFe"]);
   if(partners is not null)
   {
    if(!int.TryParse(Scalar(w["Indice"]),NumberStyles.None,CultureInfo.InvariantCulture,out var index))throw Bad("SAP_CONTRACT_BINDING_INVALID");
    var partner=partners.SingleOrDefault(x=>x.Indice==index);
    if(partner is null||partner.ContratoACM!=Scalar(w["ContratoACM"])||partner.Centro!=center||
     FugaTES.Aplicacao.Servicos.Recebimento.ToledoSapContractPartners.NumericKey(partner.Material)!=FugaTES.Aplicacao.Servicos.Recebimento.ToledoSapContractPartners.NumericKey(Scalar(root["Material"])))throw Bad("SAP_CONTRACT_BINDING_INVALID");
    var delivery=Scalar(w["IdParceiroEntrega"]);if(delivery.Length is <1 or >10||!delivery.All(char.IsAsciiDigit))throw Bad("SAP_DELIVERY_PARTNER_REQUIRED");
    w["IdParceiro"]=partner.IdParceiroSap; // Only the owner is projected; Guardian delivery partner stays original.
   }
   if(validateEntry)ValidateEntryNfe(w,direction);
   if(projection==Projection.Current)foreach(var name in new[]{"PercentualDesdobro","PesoAntesDesconto","PesoAposDesconto"})NormalizeDecimalField(w,name);
   foreach(var name in new[]{"ContratoACM","IdParceiro","IdParceiroEntrega"})if(w.ContainsKey(name))w[name]=Identifier(w[name],name,name is "ContratoACM" or "IdParceiro");
   foreach(var name in new[]{"Indice","NrIndiceTotal"}){if(!int.TryParse(Scalar(w[name]),NumberStyles.None,CultureInfo.InvariantCulture,out var n)||n<0)throw Bad("SAP_WEIGHING_INDEX_INVALID");w[name]=n;}
   if(!indexes.Add(w["Indice"]!.GetValue<int>())||w["NrIndiceTotal"]!.GetValue<int>()!=weights.Count)throw Bad("SAP_WEIGHING_INDEX_INVALID");
  }
  var json=root.ToJsonString();return new(json,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))),id,"Toledo",center,ms);
 }
 // Entry requires the real Guardian NF-e key for every item, independently of PF/PJ.
 // Keep the supplied value exact; never generate a default, pad it, or reuse another item's key.
 static void ValidateEntryNfe(JsonObject item,string direction)
 {
  if(direction!="E")return;
  var key=Text(item["ChaveAcessoNFe"]);
  if(string.IsNullOrWhiteSpace(key))throw Bad("SAP_ENTRY_NFE_ACCESS_KEY_REQUIRED");
  if(key.Length!=44||!key.All(char.IsAsciiDigit))throw Bad("SAP_NFE_ACCESS_KEY_INVALID");
 }
 static void Names(JsonObject value,string[] names)
 {
  var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var p in value)if(!seen.Add(p.Key))throw Bad("SAP_DUPLICATE_PROPERTY");
  foreach(var expected in names){var actual=value.Select(p=>p.Key).SingleOrDefault(k=>string.Equals(k,expected,StringComparison.OrdinalIgnoreCase));if(actual is not null&&actual!=expected){var node=value[actual];value.Remove(actual);value.Add(expected,node);}}
 }
 static string Text(JsonNode? n)=>n is null?"":n.GetValueKind()==JsonValueKind.String?n.GetValue<string>():throw Bad("SAP_TEXT_REQUIRED");
 static string Scalar(JsonNode? n)=>n is null?"":n.GetValueKind()==JsonValueKind.String?n.GetValue<string>():n.GetValueKind()==JsonValueKind.Number?n.ToJsonString():throw Bad("SAP_SCALAR_REQUIRED");
 // SAP requires E/S: preserve Guardian direction; legacy numeric aliases resolve through the existing contract.
 static string SapWeighingType(JsonNode? node)=>AcmSapWeighingDirection.ToSap(Scalar(node))
  ??throw Bad("SAP_WEIGHING_TYPE_INVALID");
 // Only the outbound SAP identity is padded; the stored Guardian document and receipt ID stay original.
 static string SapWeighingId(JsonNode? node)
 {var id=Identifier(node,"IdPesagem",true);if(id.Length>10)throw Bad("SAP_WEIGHING_ID_LENGTH_INVALID");return id.PadLeft(10,'0');}
 static string Identifier(JsonNode? node,string field,bool required)
 {var text=Scalar(node);if((required&&text.Length==0)||text.Length>30||!text.All(char.IsAsciiDigit))throw Bad("SAP_IDENTIFIER_INVALID_"+field.ToUpperInvariant());return text;}
 // SAP-only projection; never shorten or edit the stored Guardian user/document.
 static string SapWeighingUser(string value)
 {if(value.Length<=15)return value;return value[..(char.IsHighSurrogate(value[14])?14:15)];}
 static void NormalizeDecimalField(JsonObject value,string field)
 {
  if(!value.TryGetPropertyValue(field,out var node))return;
  var normalized=NormalizeDecimalText(Scalar(node));
  if(node?.GetValueKind()==JsonValueKind.String)value[field]=normalized;
 }
 // Comma is a decimal separator, never a grouping separator. No mixed separators or rounding.
 static string NormalizeDecimalText(string value)
 {
  var normalized=value.Replace(',','.');
  if(normalized.Length>128||!Regex.IsMatch(normalized,@"\A[0-9]+(?:\.[0-9]+)?\z",RegexOptions.CultureInvariant)||
   !decimal.TryParse(normalized,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var number))throw Bad("SAP_DECIMAL_INVALID");
  var dot=normalized.IndexOf('.');var scale=dot<0?0:normalized.Length-dot-1;
  var coefficient=BigInteger.Parse(normalized.Replace(".","",StringComparison.Ordinal),CultureInfo.InvariantCulture);
  var bits=decimal.GetBits(number);var parsed=(BigInteger)(uint)bits[0]+((BigInteger)(uint)bits[1]<<32)+((BigInteger)(uint)bits[2]<<64);
  if(coefficient*BigInteger.Pow(10,(bits[3]>>16)&0x7f)!=parsed*BigInteger.Pow(10,scale))throw Bad("SAP_DECIMAL_INVALID");
  return normalized;
 }
 static decimal Decimal(string value)
 {if(!decimal.TryParse(value,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var n))throw Bad("SAP_QUALITY_NUMBER_INVALID");return n;}
 static long Date(string value,string? zone)
 {
  var match=Regex.Match(value,@"\A/Date\((-?[0-9]{1,16})\)/\z",RegexOptions.CultureInvariant);
  if(match.Success&&long.TryParse(match.Groups[1].Value,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var ms))
  {try{_ = DateTimeOffset.FromUnixTimeMilliseconds(ms);return ms;}catch{throw Bad("SAP_DATE_OUT_OF_RANGE");}}
  if(string.IsNullOrWhiteSpace(zone))throw Bad("SAP_WEIGHING_TIMEZONE_REQUIRED");
  if(!DateTime.TryParseExact(value,new[]{"MM/dd/yyyy hh:mm:ss tt","M/d/yyyy h:mm:ss tt","dd/MM/yyyy HH:mm:ss"},CultureInfo.InvariantCulture,DateTimeStyles.None,out var local))throw Bad("SAP_WEIGHING_DATE_INVALID");
  var tz=TimeZoneInfo.FindSystemTimeZoneById(zone);local=DateTime.SpecifyKind(local,DateTimeKind.Unspecified);
  if(tz.IsAmbiguousTime(local)||tz.IsInvalidTime(local))throw Bad("SAP_WEIGHING_DATE_AMBIGUOUS");
  return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local,tz)).ToUnixTimeMilliseconds();
 }
 static ToledoSapPayloadException Bad(string code)=>new(code);
}
