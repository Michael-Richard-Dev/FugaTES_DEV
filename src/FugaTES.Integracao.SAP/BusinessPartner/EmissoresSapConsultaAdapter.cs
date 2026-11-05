using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Consultas.EmissoresSap;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace FugaTES.Integracao.SAP.BusinessPartner;

// Read-only monitor capability. No Guardian gateway, master-data store or write method.
public sealed class EmissoresSapConsultaAdapter : IEmissoresSapConsulta
{
 public const string Origem="https://vhfufds4ci.sap.fugacouros.com.br:44300";
 const string Colecao="/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner";
 const int TamanhoPagina=50;
 const string Selecao="BusinessPartner,BusinessPartnerFullName,BusinessPartnerCategory,BusinessPartnerGrouping,BusinessPartnerIsBlocked,to_Customer/Customer,to_Customer/CustomerAccountGroup,to_Supplier/Supplier,to_Supplier/SupplierAccountGroup";
 const string Expansao="to_Customer,to_Supplier,to_BusinessPartnerRole,to_BusinessPartnerTax,to_BusinessPartnerAddress,to_BusinessPartnerAddress/to_AddressUsage,to_BusinessPartnerAddress/to_PhoneNumber,to_BusinessPartnerAddress/to_EmailAddress";
 readonly SapODataHttpExecutor sap;
 public EmissoresSapConsultaAdapter(HttpClient http,OpcoesSap opcoes,IAutenticacaoSap autenticacao)
 {
  if(opcoes.BaseUrl is not{IsAbsoluteUri:true} origin||origin.Scheme!="https"||!FugaTES.Aplicacao.Configuracao.AmbientesSap.EhAprovado(origin.GetLeftPart(UriPartial.Authority),opcoes.SapClient)||origin.UserInfo.Length!=0||origin.Query.Length!=0||origin.Fragment.Length!=0||opcoes.Timeout<=TimeSpan.Zero||opcoes.Timeout>TimeSpan.FromSeconds(30))throw Error("SAP_PROFILE_NOT_APPROVED");
  sap=new(http,opcoes,autenticacao,new CookieContainer());
 }
 public async Task<EmissoresSapPagina> ConsultarAsync(EmissoresSapFiltro filtro,string? continuacao,CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested();
  if(filtro is null)throw Error("SAP_FILTER_INVALID");
  var codigo=(filtro.Codigo??"").Trim();var nome=(filtro.Nome??"").Trim();
  if(codigo.Length>0&&!Codigo(codigo)||nome.Length>80||nome.Any(char.IsControl))throw Error("SAP_FILTER_INVALID");
  var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(codigo+"\n"+nome)));
  var skip=0;
  if(continuacao is not null)
  {
   var bits=continuacao.Split('.');
   if(bits.Length!=2||bits[1]!=fingerprint||!int.TryParse(bits[0],NumberStyles.None,CultureInfo.InvariantCulture,out skip)||skip is <=0 or >1_000_000)throw Error("SAP_CURSOR_INVALID");
  }
  var filter="(to_Customer/Customer ne null or to_Supplier/Supplier ne null)";
  if(codigo.Length>0)
  {
   var keys=(codigo.All(char.IsAsciiDigit)?new[]{codigo,codigo.TrimStart('0'),codigo.TrimStart('0').PadLeft(10,'0')}:new[]{codigo}).Distinct(StringComparer.Ordinal);
   filter+=" and ("+string.Join(" or ",keys.Select(x=>"BusinessPartner eq '"+x.Replace("'","''",StringComparison.Ordinal)+"'"))+")";
  }
  if(nome.Length>0)filter+=" and substringof('"+nome.Replace("'","''",StringComparison.Ordinal)+"',BusinessPartnerFullName)";
  var uri=sap.ConstruirUri(Colecao,"$format=json&$top=50&$skip="+skip.ToString(CultureInfo.InvariantCulture)+"&$inlinecount=allpages&$orderby=BusinessPartner&$filter="+Uri.EscapeDataString(filter)+"&$select="+Uri.EscapeDataString(Selecao)+"&$expand=to_Customer,to_Supplier");
  using var json=await ReadAsync(uri,ct).ConfigureAwait(false);var d=Data(json);
  if(!d.TryGetProperty("results",out var rows)||rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>TamanhoPagina)throw Error("SAP_RESPONSE_INVALID");
  var result=new List<EmissorSapLeitura>();var identities=new HashSet<string>(StringComparer.Ordinal);
  foreach(var row in rows.EnumerateArray())
  {
   var bp=Text(row,"BusinessPartner");var name=Text(row,"BusinessPartnerFullName");var cat=Text(row,"BusinessPartnerCategory");
   if(!Codigo(bp)||!identities.Add(bp)||string.IsNullOrWhiteSpace(name)||cat.Length!=1||!char.IsAsciiDigit(cat[0]))throw Error("SAP_RESPONSE_INVALID");
   if(codigo.Length>0&&Chave(bp)!=Chave(codigo))throw Error("SAP_IDENTITY_MISMATCH");
   var cliente=Navigation(row,"to_Customer","Customer",bp);var fornecedor=Navigation(row,"to_Supplier","Supplier",bp);
   if(!cliente&&!fornecedor)throw Error("SAP_EMITTER_ROLE_MISSING");
   bool? blocked=null;
   if(row.TryGetProperty("BusinessPartnerIsBlocked",out var flag)&&flag.ValueKind!=JsonValueKind.Null)
   {if(flag.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw Error("SAP_RESPONSE_INVALID");blocked=flag.GetBoolean();}
   result.Add(new(bp,name,cat switch{"1"=>"PF","2"=>"PJ",_=>"Categoria SAP "+cat},cliente,fornecedor,blocked,Text(row,"BusinessPartnerGrouping")));
  }
  long? total=null;
  if(d.TryGetProperty("__count",out var count))
  {if(count.ValueKind!=JsonValueKind.String||!long.TryParse(count.GetString(),NumberStyles.None,CultureInfo.InvariantCulture,out var value))throw Error("SAP_RESPONSE_INVALID");total=value;}
  var hasNext=false;
  if(d.TryGetProperty("__next",out var next)&&next.ValueKind!=JsonValueKind.Null)
  {
   if(next.ValueKind!=JsonValueKind.String||!Uri.TryCreate(uri,next.GetString(),out var remote)||remote.Scheme!="https"||remote.GetLeftPart(UriPartial.Authority)!=Origem||remote.AbsolutePath!=Colecao||remote.UserInfo.Length!=0||remote.Fragment.Length!=0||remote==uri)throw Error("SAP_CONTINUATION_INVALID");
   var query=QueryHelpers.ParseQuery(remote.Query);
   if(query.Any(x=>x.Value.Count!=1)||!query.TryGetValue("sap-client",out var client)||!QueryHelpers.ParseQuery(uri.Query).TryGetValue("sap-client",out var meu)||client.ToString()!=meu.ToString())throw Error("SAP_CONTINUATION_INVALID");
   // Never follow the server URL. Pagination is constructed locally with a fixed scope.
   hasNext=true;
  }
  var end=checked(skip+result.Count);
  if(total is not null&&(total<end||result.Count==0&&total>skip)||result.Count==0&&hasNext)throw Error("SAP_PAGE_INCOMPLETE");
  var more=total is not null?end<total:hasNext||result.Count==TamanhoPagina;
  return new(result,more?end.ToString(CultureInfo.InvariantCulture)+"."+fingerprint:null,total,skip,DateTimeOffset.UtcNow);
 }
 public async Task<EmissorSapDetalhe> ObterDetalheAsync(string bp,CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested();if(!Codigo(bp))throw Error("SAP_FILTER_INVALID");
  var uri=sap.ConstruirUri(Colecao+"('"+Uri.EscapeDataString(bp.Replace("'","''",StringComparison.Ordinal))+"')","$format=json&$expand="+Uri.EscapeDataString(Expansao));
  using var json=await ReadAsync(uri,ct).ConfigureAwait(false);var d=Data(json);
  if(Text(d,"BusinessPartner")!=bp)throw Error("SAP_IDENTITY_MISMATCH");
  if(HasContinuation(d))throw Error("SAP_DETAIL_INCOMPLETE");
  var c=Navigation(d,"to_Customer","Customer",bp);var s=Navigation(d,"to_Supplier","Supplier",bp);
  if(!c&&!s)throw Error("SAP_EMITTER_ROLE_MISSING");
  var fields=new List<EmissorSapDado>();
  void Add(JsonElement element,string group,params string[] names)
  {foreach(var name in names)if(element.ValueKind==JsonValueKind.Object&&element.TryGetProperty(name,out var value)&&value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False){var text=value.ValueKind==JsonValueKind.String?value.GetString()!:value.ToString();if(!string.IsNullOrEmpty(text))fields.Add(new(group,name,text));}}
  Add(d,"BP","BusinessPartner","BusinessPartnerFullName","BusinessPartnerName","BusinessPartnerCategory","BusinessPartnerGrouping","BusinessPartnerType","OrganizationBPName1","OrganizationBPName2","OrganizationBPName3","OrganizationBPName4","FirstName","MiddleName","LastName","BusinessPartnerIsBlocked","CreationDate","LastChangeDate","SearchTerm1","SearchTerm2");
  if(c)Add(d.GetProperty("to_Customer"),"Cliente","Customer","CustomerName","CustomerFullName","CustomerAccountGroup","PostingIsBlocked","DeliveryIsBlocked","OrderIsBlocked","BillingIsBlocked");
  if(s)Add(d.GetProperty("to_Supplier"),"Fornecedor","Supplier","SupplierName","SupplierFullName","SupplierAccountGroup","SupplierIsBlockedForPosting","PostingIsBlocked","PurchasingIsBlocked");
  foreach(var tax in Rows(d,"to_BusinessPartnerTax"))Add(tax,"Documento "+Text(tax,"BPTaxType"),"BPTaxType","BPTaxNumber","BPTaxLongNumber");
  foreach(var address in Rows(d,"to_BusinessPartnerAddress"))
  {
   var group="Endereço "+Text(address,"AddressID");
   Add(address,group,"AddressID","StreetName","HouseNumber","StreetPrefixName","StreetSuffixName","AdditionalStreetPrefixName","AdditionalStreetSuffixName","Building","Floor","RoomNumber","CityName","District","Region","PostalCode","Country","AddressFullName");
   foreach(var usage in Rows(address,"to_AddressUsage"))Add(usage,group+" / uso","AddressUsage","ValidityStartDate","ValidityEndDate");
   foreach(var phone in Rows(address,"to_PhoneNumber"))Add(phone,group+" / telefone","PhoneNumber","InternationalPhoneNumber","PhoneNumberExtension");
   foreach(var email in Rows(address,"to_EmailAddress"))Add(email,group+" / e-mail","EmailAddress","IsDefaultEmailAddress");
  }
  foreach(var role in Rows(d,"to_BusinessPartnerRole"))Add(role,"Função SAP","BusinessPartnerRole","ValidFrom","ValidTo");
  return new(bp,JsonSerializer.Serialize(d,new JsonSerializerOptions{WriteIndented=true}),fields,DateTimeOffset.UtcNow);
 }
 async Task<JsonDocument> ReadAsync(Uri uri,CancellationToken ct)
 {
  try
  {
   var response=await sap.ObterJsonLimitadoAsync(uri,ct).ConfigureAwait(false);
   if(response.Status!=HttpStatusCode.OK)throw Error(response.Status switch{HttpStatusCode.Unauthorized=>"SAP_UNAUTHORIZED",HttpStatusCode.Forbidden=>"SAP_FORBIDDEN",HttpStatusCode.NotFound=>"SAP_BP_NOT_FOUND",_=>"SAP_HTTP_ERROR"},(int)response.Status);
   var json=JsonDocument.Parse(response.Body,new JsonDocumentOptions{MaxDepth=48});
   if(Duplicate(json.RootElement)){json.Dispose();throw Error("SAP_RESPONSE_INVALID");}return json;
  }
  catch(EmissoresSapException){throw;}
  catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
  catch(OperationCanceledException){throw Error("SAP_TIMEOUT");}
  catch(HttpRequestException){throw Error("SAP_UNAVAILABLE");}
  catch(JsonException){throw Error("SAP_RESPONSE_INVALID");}
  catch{throw Error("SAP_AUTH_OR_READ_FAILED");}
 }
 static JsonElement Data(JsonDocument json)=>json.RootElement.ValueKind==JsonValueKind.Object&&json.RootElement.TryGetProperty("d",out var d)&&d.ValueKind==JsonValueKind.Object?d:throw Error("SAP_RESPONSE_INVALID");
 static bool Navigation(JsonElement row,string name,string key,string bp)
 {
  if(row.ValueKind!=JsonValueKind.Object||!row.TryGetProperty(name,out var nav))throw Error("SAP_RESPONSE_INVALID");
  if(nav.ValueKind==JsonValueKind.Null)return false;
  if(nav.ValueKind!=JsonValueKind.Object||nav.TryGetProperty("__deferred",out _)||!Codigo(Text(nav,key)))throw Error("SAP_IDENTITY_MISMATCH");
  if(nav.TryGetProperty("BusinessPartner",out _)&&Chave(Text(nav,"BusinessPartner"))!=Chave(bp))throw Error("SAP_IDENTITY_MISMATCH");return true;
 }
 static IEnumerable<JsonElement> Rows(JsonElement node,string name)
 {
  if(!node.TryGetProperty(name,out var nav)||nav.ValueKind==JsonValueKind.Null)throw Error("SAP_DETAIL_INCOMPLETE");
  if(nav.ValueKind!=JsonValueKind.Object||!nav.TryGetProperty("results",out var rows)||rows.ValueKind!=JsonValueKind.Array)throw Error("SAP_DETAIL_INCOMPLETE");return rows.EnumerateArray().ToArray();
 }
 static bool HasContinuation(JsonElement node)=>node.ValueKind==JsonValueKind.Object?node.EnumerateObject().Any(x=>x.Name=="__next"||HasContinuation(x.Value)):node.ValueKind==JsonValueKind.Array&&node.EnumerateArray().Any(HasContinuation);
 static bool Duplicate(JsonElement node)
 {if(node.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var p in node.EnumerateObject())if(!names.Add(p.Name)||Duplicate(p.Value))return true;}else if(node.ValueKind==JsonValueKind.Array)return node.EnumerateArray().Any(Duplicate);return false;}
 static string Text(JsonElement node,string field)=>node.ValueKind==JsonValueKind.Object&&node.TryGetProperty(field,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:"";
 static bool Codigo(string bp)=>bp.Length is >0 and <=10&&!string.IsNullOrWhiteSpace(bp)&&!bp.Any(char.IsControl)&&bp.Any(c=>c!='0');
 static string Chave(string bp)=>bp.All(char.IsAsciiDigit)?bp.TrimStart('0'):bp;
 static EmissoresSapException Error(string code,int? status=null)=>new(code,status);
}
