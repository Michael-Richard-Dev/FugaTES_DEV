using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
namespace FugaTES.Integracao.SAP.Http;

public sealed record ToledoSapAtomConfirmation(string EntityId,string ResponseSha256);
// Validates the supplied Atom contract. Never follows hrefs or resolves XML entities.
public static class ToledoSapAtomReceipt
{
 static readonly XNamespace Atom="http://www.w3.org/2005/Atom",Data="http://schemas.microsoft.com/ado/2007/08/dataservices",Meta="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";
 public static ToledoSapAtomConfirmation? Validate(string body,ToledoSapPreparedPayload expected,Uri resource)
 {
  try
  {
   if(Encoding.UTF8.GetByteCount(body)>1048576)return null;
   using var input=new StringReader(body);using var reader=XmlReader.Create(input,new(){DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1048576});
   var root=XDocument.Load(reader).Root;if(root?.Name!=Atom+"entry")return null;
   var ids=root.Elements(Atom+"id").ToArray();if(ids.Length!=1||!Uri.TryCreate(ids[0].Value,UriKind.Absolute,out var id)||id.GetLeftPart(UriPartial.Authority)!=resource.GetLeftPart(UriPartial.Authority)||id.Query.Length!=0||id.Fragment.Length!=0||id.UserInfo.Length!=0)return null;
   var path=resource.GetLeftPart(UriPartial.Path)+$"(IdPesagem='{expected.IdPesagem}',SistemaBalanca='Toledo',Centro='{expected.Centro}')";
   if(Uri.UnescapeDataString(id.AbsoluteUri)!=path)return null;
   var props=Properties(root);if(props is null)return null;
   using var json=JsonDocument.Parse(expected.Json);
   foreach(var p in json.RootElement.EnumerateObject())
   {
    if(p.Name is "to_Pesagem" or "to_QualidPes")
    {
     var links=root.Elements(Atom+"link").Where(l=>(string?)l.Attribute("rel")=="http://schemas.microsoft.com/ado/2007/08/dataservices/related/"+p.Name).ToArray();
     if(links.Length!=1)return null;var feeds=links[0].Elements(Meta+"inline").Elements(Atom+"feed").ToArray();if(feeds.Length!=1)return null;
     var entries=feeds[0].Elements(Atom+"entry").ToArray();if(entries.Length!=p.Value.GetArrayLength())return null;
     var remaining=entries.Select(Properties).ToList();if(remaining.Any(x=>x is null))return null;
     foreach(var child in p.Value.EnumerateArray())
     {
      var match=remaining.FindIndex(pr=>pr is not null&&MatchIdentity(pr,expected)&&child.EnumerateObject().All(k=>pr.TryGetValue(k.Name,out var v)&&Same(k.Name,k.Value,v,expected)));
      if(match<0)return null;remaining.RemoveAt(match);
     }
    }
    else if(!props.TryGetValue(p.Name,out var element)||!Same(p.Name,p.Value,element,expected))return null;
   }
   return new(id.AbsoluteUri,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))));
  }
  catch{return null;}
 }
 public static bool ProvesEmptyCollection(string body,Uri resource)
 {
  try
  {
   if(Encoding.UTF8.GetByteCount(body)>1048576)return false;
   using var input=new StringReader(body);using var reader=XmlReader.Create(input,new(){DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1048576});
   var root=XDocument.Load(reader).Root;
   if(root?.Name!=Atom+"feed"||root.Elements(Atom+"entry").Any()||root.Elements(Atom+"link").Any(x=>(string?)x.Attribute("rel")=="next"))return false;
   var ids=root.Elements(Atom+"id").ToArray();var counts=root.Elements(Meta+"count").ToArray();
   return ids.Length==1&&ids[0].Value==resource.GetLeftPart(UriPartial.Path)&&counts.Length==1&&counts[0].Value=="0";
  }
  catch{return false;}
 }
 static Dictionary<string,XElement>? Properties(XElement entry)
 {var all=entry.Elements(Atom+"content").Elements(Meta+"properties").ToArray();if(all.Length!=1)return null;return all[0].Elements().Where(e=>e.Name.Namespace==Data).ToDictionary(e=>e.Name.LocalName,StringComparer.Ordinal);}
 static bool MatchIdentity(Dictionary<string,XElement> p,ToledoSapPreparedPayload e)=>p.TryGetValue("IdPesagem",out var id)&&id.Value==e.IdPesagem&&p.TryGetValue("Centro",out var c)&&c.Value==e.Centro&&p.TryGetValue("SistemaBalanca",out var s)&&s.Value==e.SistemaBalanca;
 static bool Same(string name,JsonElement value,XElement actual,ToledoSapPreparedPayload expected)
 {
  if((string?)actual.Attribute(Meta+"null")=="true")return value.ValueKind==JsonValueKind.Null;
  var text=value.ValueKind==JsonValueKind.String?value.GetString():value.GetRawText();
  if(name=="DataPesagem")return DateTimeOffset.TryParse(actual.Value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var date)&&date.ToUnixTimeMilliseconds()==expected.EpochMilliseconds;
  if(name.StartsWith("Peso",StringComparison.Ordinal)||name is "ValorAferido" or "PercentualDesdobro" or "Indice" or "NrIndiceTotal")
    return decimal.TryParse(text,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var left)&&decimal.TryParse(actual.Value,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var right)&&left==right;
  return text==actual.Value;
 }
}
