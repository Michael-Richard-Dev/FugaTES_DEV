using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Consultas.CadastrosSap;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using Microsoft.AspNetCore.WebUtilities;
namespace FugaTES.Integracao.SAP.Cadastros;

// A read session owns only temporary memory, not a master-data repository or worker.
public abstract class SapCadastroConsultaBase:ICadastroSapConsulta
{
 public const string Origem="https://vhfufds4ci.sap.fugacouros.com.br:44300";
 readonly SapODataHttpExecutor sap;
 readonly SemaphoreSlim gate=new(1,1);
 readonly TimeProvider tempo;
 CadastroSapLeitura[]? snapshot;
 string? snapshotId,filterHash;
 DateTimeOffset readAt;
 public CategoriaCadastroSap Categoria{get;}
 protected SapCadastroConsultaBase(CategoriaCadastroSap categoria,HttpClient http,OpcoesSap opcoes,IAutenticacaoSap auth,TimeProvider? tempo)
 {
  if(opcoes.BaseUrl is not{IsAbsoluteUri:true} origin||origin.Scheme!="https"||!FugaTES.Aplicacao.Configuracao.AmbientesSap.EhAprovado(origin.GetLeftPart(UriPartial.Authority),opcoes.SapClient)||origin.UserInfo.Length>0||origin.Query.Length>0||origin.Fragment.Length>0||opcoes.Timeout<=TimeSpan.Zero||opcoes.Timeout>TimeSpan.FromSeconds(30))throw Error("SAP_PROFILE_NOT_APPROVED");
  Categoria=categoria;this.tempo=tempo??TimeProvider.System;sap=new(http,opcoes,auth,new CookieContainer());
 }
 public async Task<CadastrosSapPagina> ConsultarAsync(CadastrosSapFiltro filtro,string? continuacao,CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested();
  if(filtro is null)throw Error("SAP_FILTER_INVALID");
  var code=(filtro.Codigo??"").Trim();var name=(filtro.Nome??"").Trim();
  if(code.Length>0&&!Key(code,Categoria==CategoriaCadastroSap.Materiais?40:10)||name.Length>80||name.Any(char.IsControl))throw Error("SAP_FILTER_INVALID");
  var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Categoria+"\n"+code+"\n"+name)));
  await gate.WaitAsync(ct).ConfigureAwait(false);
  try
  {
   var start=0;
   if(continuacao is null)
   {
    snapshot=null;snapshotId=null;filterHash=null;
    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(90));
    IReadOnlyList<CadastroSapLeitura> loaded;
    try{loaded=await LerCatalogoAsync(code,deadline.Token).ConfigureAwait(false);}
    catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw Error("SAP_TIMEOUT");}
    ct.ThrowIfCancellationRequested();
    if(loaded.GroupBy(x=>x.Codigo,StringComparer.Ordinal).Any(x=>x.Count()!=1))throw Error("SAP_DUPLICATE_KEY");
    snapshot=loaded.Where(x=>string.IsNullOrEmpty(name)||x.Nome.Contains(name,StringComparison.OrdinalIgnoreCase)).OrderBy(x=>x.Codigo,StringComparer.Ordinal).ToArray();
    filterHash=hash;snapshotId=Guid.NewGuid().ToString("N");readAt=tempo.GetUtcNow();
   }
   else
   {
    var parts=continuacao.Split('.');
    if(parts.Length!=3||parts[0]!=snapshotId||parts[2]!=hash||hash!=filterHash||snapshot is null||!int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out start)||start<=0||start>=snapshot.Length||start%50!=0)throw Error("SAP_CURSOR_INVALID");
   }
   var rows=snapshot!.Skip(start).Take(50).ToArray();var end=start+rows.Length;
   return new(rows,end<snapshot.Length?snapshotId+"."+end.ToString(CultureInfo.InvariantCulture)+"."+hash:null,snapshot.LongLength,start,readAt);
  }
  finally{gate.Release();}
 }
 protected abstract Task<IReadOnlyList<CadastroSapLeitura>> LerCatalogoAsync(string code,CancellationToken ct);
 public abstract Task<CadastroSapDetalhe> ObterDetalheAsync(string codigo,CancellationToken ct);
 protected DateTimeOffset Now=>tempo.GetUtcNow();
 protected async Task<JsonDocument> GetAsync(string path,string query,CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested();
  if(!path.StartsWith("/sap/opu/odata/sap/API_PRODUCT_SRV/A_Product",StringComparison.Ordinal)&&!path.StartsWith("/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner",StringComparison.Ordinal))throw Error("SAP_PATH_NOT_APPROVED");
  try
  {
   var response=await sap.ObterJsonLimitadoAsync(sap.ConstruirUri(path,query),ct).ConfigureAwait(false);
   if(response.Status!=HttpStatusCode.OK)throw Error(response.Status switch{HttpStatusCode.Unauthorized=>"SAP_UNAUTHORIZED",HttpStatusCode.Forbidden=>"SAP_FORBIDDEN",HttpStatusCode.NotFound=>"SAP_NOT_FOUND",_=>"SAP_HTTP_ERROR"},(int)response.Status);
   var json=JsonDocument.Parse(response.Body,new JsonDocumentOptions{MaxDepth=48});
   if(Duplicate(json.RootElement)){json.Dispose();throw Error("SAP_RESPONSE_INVALID");}return json;
  }
  catch(CadastroSapException){throw;}
  catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
  catch(OperationCanceledException){throw Error("SAP_TIMEOUT");}
  catch(HttpRequestException){throw Error("SAP_UNAVAILABLE");}
  catch(JsonException){throw Error("SAP_RESPONSE_INVALID");}
  catch{throw Error("SAP_AUTH_OR_READ_FAILED");}
 }
 protected async Task<IReadOnlyList<JsonElement>> AllAsync(string path,string key,string query,CancellationToken ct)
 {
  var result=new List<JsonElement>();var ids=new HashSet<string>(StringComparer.Ordinal);long? declared=null;
  for(var page=0;page<2000;page++)
  {
   var offset=result.Count;
   using var json=await GetAsync(path,query+"&$format=json&$top=50&$skip="+offset.ToString(CultureInfo.InvariantCulture)+"&$inlinecount=allpages&$orderby="+key,ct).ConfigureAwait(false);
   var d=Data(json);var rows=Rows(d,"results",direct:true);
   if(rows.Count>50)throw Error("SAP_RESPONSE_INVALID");
   foreach(var row in rows)
   {var id=Text(row,key);if(!Key(id,key=="Product"?40:10)||!ids.Add(id))throw Error("SAP_RESPONSE_INVALID");result.Add(row.Clone());}
   if(d.TryGetProperty("__count",out var count))
   {if(count.ValueKind!=JsonValueKind.String||!long.TryParse(count.GetString(),NumberStyles.None,CultureInfo.InvariantCulture,out var total)||total>100_000||declared is not null&&declared!=total)throw Error("SAP_PAGE_INCOMPLETE");declared=total;}
   var hasNext=false;
   if(d.TryGetProperty("__next",out var next)&&next.ValueKind!=JsonValueKind.Null)
   {
    var current=sap.ConstruirUri(path);
    if(next.ValueKind!=JsonValueKind.String||!Uri.TryCreate(current,next.GetString(),out var target)||target.GetLeftPart(UriPartial.Authority)!=current.GetLeftPart(UriPartial.Authority)||target.Scheme!="https"||target.AbsolutePath!=path||target.UserInfo.Length>0||target.Fragment.Length>0)throw Error("SAP_CONTINUATION_INVALID");
    var args=QueryHelpers.ParseQuery(target.Query);if(args.Any(x=>x.Value.Count!=1)||!args.TryGetValue("sap-client",out var client)||!QueryHelpers.ParseQuery(current.Query).TryGetValue("sap-client",out var meu)||client.ToString()!=meu.ToString())throw Error("SAP_CONTINUATION_INVALID");hasNext=true;
   }
   if(result.Count>100_000||declared is not null&&(result.Count>declared||rows.Count==0&&result.Count<declared)||rows.Count==0&&hasNext)throw Error("SAP_PAGE_INCOMPLETE");
   // The remote URL is never followed. All offsets and scope are constructed locally.
   if(declared is not null?result.Count==declared:!hasNext&&rows.Count<50)return result;
  }
  throw Error("SAP_READ_LIMIT_EXCEEDED");
 }
 protected CadastroSapDetalhe Detail(string codigo,JsonElement data,params string[] expansions)
 {
  if(HasNext(data))throw Error("SAP_DETAIL_INCOMPLETE");foreach(var nav in expansions)Rows(data,nav);
  var result=new List<CadastroSapDado>();
  void Walk(JsonElement node,string group)
  {
   if(result.Count>10_000)throw Error("SAP_DETAIL_TOO_LARGE");
   if(node.ValueKind==JsonValueKind.Object)
   {
    if(node.TryGetProperty("__deferred",out _))return;
    foreach(var p in node.EnumerateObject())
    {
     if(p.Name.StartsWith("__",StringComparison.Ordinal))continue;
     if(p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
     {var text=p.Value.ValueKind==JsonValueKind.String?p.Value.GetString()!:p.Value.ToString();if(text.Length>0)result.Add(new(group,p.Name,text));}
     else Walk(p.Value,group.Length==0?p.Name:group+" / "+p.Name);
    }
   }
   else if(node.ValueKind==JsonValueKind.Array){var i=0;foreach(var row in node.EnumerateArray())Walk(row,group+" ["+(++i).ToString(CultureInfo.InvariantCulture)+"]");}
  }
  Walk(data,"Dados SAP");return new(codigo,JsonSerializer.Serialize(data,new JsonSerializerOptions{WriteIndented=true}),result,Now);
 }
 protected static JsonElement Data(JsonDocument json)=>json.RootElement.ValueKind==JsonValueKind.Object&&json.RootElement.TryGetProperty("d",out var d)&&d.ValueKind==JsonValueKind.Object?d:throw Error("SAP_RESPONSE_INVALID");
 protected static IReadOnlyList<JsonElement> Rows(JsonElement node,string name,bool direct=false)
 {if(!node.TryGetProperty(name,out var value))throw Error("SAP_DETAIL_INCOMPLETE");if(!direct){if(value.ValueKind!=JsonValueKind.Object||value.TryGetProperty("__next",out _)||!value.TryGetProperty("results",out value))throw Error("SAP_DETAIL_INCOMPLETE");}if(value.ValueKind!=JsonValueKind.Array)throw Error("SAP_RESPONSE_INVALID");return value.EnumerateArray().ToArray();}
 protected static string Text(JsonElement node,string name)=>node.ValueKind==JsonValueKind.Object&&node.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:"";
 protected static bool? Flag(JsonElement node,string name)
 {if(!node.TryGetProperty(name,out var v)||v.ValueKind==JsonValueKind.Null)return null;if(v.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw Error("SAP_RESPONSE_INVALID");return v.GetBoolean();}
 protected static bool Key(string value,int max)=>value.Length is >0&&value.Length<=max&&!string.IsNullOrWhiteSpace(value)&&!value.Any(char.IsControl)&&value.Any(c=>c!='0');
 protected static string Literal(string value)=>"'"+value.Replace("'","''",StringComparison.Ordinal)+"'";
 protected static string FilterCode(string property,string code,int numericWidth)
 {var variants=code.All(char.IsAsciiDigit)?new[]{code,code.TrimStart('0'),code.TrimStart('0').PadLeft(numericWidth,'0')}:new[]{code};return "("+string.Join(" or ",variants.Distinct(StringComparer.Ordinal).Select(x=>property+" eq "+Literal(x)))+")";}
 protected static bool Matches(string actual,string requested)=>actual==requested||actual.All(char.IsAsciiDigit)&&requested.All(char.IsAsciiDigit)&&actual.TrimStart('0')==requested.TrimStart('0');
 protected static string EncodedKey(string code)=>Uri.EscapeDataString(code.Replace("'","''",StringComparison.Ordinal));
 protected static bool HasNext(JsonElement node)=>node.ValueKind==JsonValueKind.Object?node.EnumerateObject().Any(x=>x.Name=="__next"&&x.Value.ValueKind!=JsonValueKind.Null||HasNext(x.Value)):node.ValueKind==JsonValueKind.Array&&node.EnumerateArray().Any(HasNext);
 static bool Duplicate(JsonElement node)
 {if(node.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var p in node.EnumerateObject())if(!names.Add(p.Name)||Duplicate(p.Value))return true;}else if(node.ValueKind==JsonValueKind.Array)return node.EnumerateArray().Any(Duplicate);return false;}
 protected static CadastroSapException Error(string code,int? status=null)=>new(code,status);
}
