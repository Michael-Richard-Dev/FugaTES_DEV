using System.Globalization;
using System.Text.Json;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
namespace FugaTES.Aplicacao.Servicos.Recebimento;

public sealed class ToledoSapContractPartnerException(string code):Exception(code)
{public string Code{get;}=code;}
public sealed record ToledoSapContratoFonte(string NumeroContrato,long Versao,Guid IntegracaoId,string FingerprintContrato,
 string FingerprintIntegracao,string PayloadContratoJson,DateTimeOffset CriadoEm,string Origem,string Destino,string Operacao,string ChaveNegocio);
public sealed record ToledoSapParceiroAuditado(int Indice,string ContratoACM,string ContratoSap,string Centro,string Material,
 string IdParceiroSap,Guid IntegracaoContratoId,long VersaoContrato,string FingerprintContrato)
{
 public bool Valido=>Indice>=0&&ToledoSapContractPartners.NumericKey(ContratoACM) is not null&&
  ToledoSapContractPartners.NumericKey(ContratoACM)==ToledoSapContractPartners.NumericKey(ContratoSap)&&
  ToledoSapContractPartners.NumericKey(Centro) is not null&&ToledoSapContractPartners.NumericKey(Material) is not null&&
  IdParceiroSap.Length is >0 and <=10&&IdParceiroSap.All(char.IsAsciiDigit)&&IntegracaoContratoId!=Guid.Empty&&VersaoContrato>0&&
  FingerprintContrato.Length==64&&FingerprintContrato.All(char.IsAsciiHexDigit);
}
// Resolves the SAP contract owner independently of the emitter selected at Guardian.
// No HTTP, database mutation, PF/PJ inference or conversion from Guardian Indice to SAP Item.
public static class ToledoSapContractPartners
{
 public const string Version="TOLEDO_SAP_PARTNER_V1";
 public static string? NumericKey(string? s)
 {if(string.IsNullOrEmpty(s)||s.Length>40||!s.All(char.IsAsciiDigit))return null;var n=s.TrimStart('0');return n.Length==0?"0":n;}
 public static IReadOnlyList<ToledoSapParceiroAuditado> Resolve(string original,DateTimeOffset received,IReadOnlyList<ToledoSapContratoFonte> sources)
 {
  if(received==default||sources.Count==0)throw Fail("SAP_CONTRACT_PARTNER_UNPROVEN");
  if(sources.Count>500)throw Fail("SAP_CONTRACT_SOURCE_LIMIT_EXCEEDED");
  using var doc=JsonDocument.Parse(original,new(){MaxDepth=32});var root=Props(doc.RootElement);
  var center=Value(root,"Centro");var material=Value(root,"Material");
  if(NumericKey(center) is null||NumericKey(material) is null||!root.TryGetValue("to_Pesagem",out var items)||items.ValueKind!=JsonValueKind.Array||items.GetArrayLength() is <1 or >1000)throw Fail("SAP_CONTRACT_BINDING_INVALID");
  var proven=new List<(ToledoSapContratoFonte Source,ContratoSapToledoSnapshot Snapshot)>();
  foreach(var s in sources)
  {
   if(s.Versao<1||s.IntegracaoId==Guid.Empty||s.CriadoEm==default||s.Origem!="SAP"||s.Destino!="TOLEDO"||s.Operacao!="SAP_CONTRATO_TOLEDO"||
    s.ChaveNegocio!=s.NumeroContrato||NumericKey(s.NumeroContrato) is null||s.FingerprintContrato!=s.FingerprintIntegracao)
    throw Fail("SAP_CONTRACT_SOURCE_UNVERIFIED");
   ContratoSapToledoCanonico canonical;
   try{canonical=new ContratoSapToledoCanonicalizer().Canonicalizar(JsonSerializer.Deserialize<ContratoSapToledoSnapshot>(s.PayloadContratoJson)??throw Fail("SAP_CONTRACT_SOURCE_UNVERIFIED"));}
   catch{throw Fail("SAP_CONTRACT_SOURCE_UNVERIFIED");}
   if(canonical.FingerprintSha256!=s.FingerprintContrato||canonical.Snapshot.NumeroContrato!=s.NumeroContrato)throw Fail("SAP_CONTRACT_SOURCE_UNVERIFIED");
   if(s.CriadoEm<=received)proven.Add((s,canonical.Snapshot));
  }
  var result=new List<ToledoSapParceiroAuditado>();var indexes=new HashSet<int>();
  foreach(var element in items.EnumerateArray())
  {
   var item=Props(element);var contract=Value(item,"ContratoACM");var contractKey=NumericKey(contract);
   if(contractKey is null||!int.TryParse(Value(item,"Indice"),NumberStyles.None,CultureInfo.InvariantCulture,out var index)||index<0||!indexes.Add(index))throw Fail("SAP_CONTRACT_BINDING_INVALID");
   var delivery=Value(item,"IdParceiroEntrega");
   if(delivery.Length is <1 or >10||!delivery.All(char.IsAsciiDigit))throw Fail("SAP_DELIVERY_PARTNER_REQUIRED");
   var eligible=proven.Where(x=>NumericKey(x.Source.NumeroContrato)==contractKey).ToArray();
   if(eligible.Length==0)throw Fail("SAP_CONTRACT_PARTNER_UNPROVEN");
   var latest=new List<(ToledoSapContratoFonte Source,ContratoSapToledoSnapshot Snapshot)>();
   foreach(var group in eligible.GroupBy(x=>(x.Source.NumeroContrato,x.Snapshot.Item??"")))
   {
    var top=group.Max(x=>x.Source.Versao);var matches=group.Where(x=>x.Source.Versao==top).ToArray();
    if(matches.Length!=1)throw Fail("SAP_CONTRACT_PARTNER_AMBIGUOUS");latest.Add(matches[0]);
   }
   // A newer version that changed scope supersedes the old one; never resurrect an older matching material/center.
   var scoped=latest.Where(x=>x.Snapshot.Centro==center&&NumericKey(x.Snapshot.Material)==NumericKey(material)).ToArray();
   if(scoped.Length==0)throw Fail("SAP_CONTRACT_PARTNER_UNPROVEN");
   var partners=scoped.Select(x=>x.Snapshot.IdParceiro).Distinct(StringComparer.Ordinal).ToArray();
   if(partners.Length!=1)throw Fail("SAP_CONTRACT_PARTNER_AMBIGUOUS");
   var partner=partners[0];if(partner is null||partner.Length is <1 or >10||!partner.All(char.IsAsciiDigit))throw Fail("SAP_CONTRACT_PARTNER_INVALID");
   var chosen=scoped.OrderByDescending(x=>x.Source.CriadoEm).ThenByDescending(x=>x.Source.Versao).ThenBy(x=>x.Source.IntegracaoId).First();
   result.Add(new(index,contract,chosen.Source.NumeroContrato,center,material,partner,chosen.Source.IntegracaoId,chosen.Source.Versao,chosen.Source.FingerprintContrato));
  }
  return result;
 }
 static Dictionary<string,JsonElement> Props(JsonElement e)
 {if(e.ValueKind!=JsonValueKind.Object)throw Fail("SAP_CONTRACT_BINDING_INVALID");try{return e.EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value,StringComparer.OrdinalIgnoreCase);}catch{throw Fail("SAP_CONTRACT_BINDING_INVALID");}}
 static string Value(Dictionary<string,JsonElement> e,string key)=>!e.TryGetValue(key,out var x)?"":x.ValueKind==JsonValueKind.String?x.GetString()??"":x.ValueKind==JsonValueKind.Number?x.GetRawText():"";
 static ToledoSapContractPartnerException Fail(string code)=>new(code);
}
