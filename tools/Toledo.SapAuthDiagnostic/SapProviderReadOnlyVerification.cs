using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Http;
namespace FugaTES.Tools;

public sealed record SapProviderReadOnlyProof(bool Passed,string Phase,int? HttpStatus,bool MetadataConfirmed,bool CsrfConfirmed);
// Installation-only GET verification. It reuses IAutenticacaoSap and the existing CSRF executor.
// Its caller creates a fresh redirect-disabled, cookie-disabled transport. No POST is possible here.
public static class SapProviderReadOnlyVerification
{
 public static async Task<SapProviderReadOnlyProof> CheckAsync(HttpClient http,IAutenticacaoSap authentication,CancellationToken ct)
 {
  var phase="METADATA";int? status=null;var metadata=false;
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(45));
  try
  {
   var origin=new Uri("https://vhfufds4ci.sap.fugacouros.com.br:44300");
   var cookies=new CookieContainer();
   var executor=new SapODataHttpExecutor(http,new(){BaseUrl=origin,SapClient="110",Timeout=TimeSpan.FromSeconds(20)},authentication,cookies);
   using(var request=new HttpRequestMessage(HttpMethod.Get,executor.ConstruirUri("/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/$metadata")))
   {
    request.Headers.TryAddWithoutValidation("Accept","*/*");
    await authentication.AplicarAsync(request,timeout.Token);
    using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
    status=(int)response.StatusCode;
    if(status!=200)return new(false,phase,status,false,false);
    if(response.Content.Headers.ContentLength>1048576)return new(false,"METADATA_CONTRACT",status,false,false);
    await using var input=await response.Content.ReadAsStreamAsync(timeout.Token);using var output=new MemoryStream();var buffer=new byte[8192];
    int n;while((n=await input.ReadAsync(buffer.AsMemory(),timeout.Token))>0){if(output.Length+n>1048576)return new(false,"METADATA_CONTRACT",status,false,false);output.Write(buffer,0,n);}
    var body=new UTF8Encoding(false,true).GetString(output.ToArray());
    using var text=new StringReader(body);using var xml=XmlReader.Create(text,new(){DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1048576});
    var root=XDocument.Load(xml).Root;XNamespace edmx="http://schemas.microsoft.com/ado/2007/06/edmx",edm="http://schemas.microsoft.com/ado/2008/09/edm";
    metadata=root?.Name==edmx+"Edmx"&&root.Descendants(edm+"EntitySet").Any(e=>(string?)e.Attribute("Name")=="ReceberPesagem"&&((string?)e.Attribute("EntityType"))?.EndsWith(".ReceberPesagemType",StringComparison.Ordinal)==true);
    if(!metadata)return new(false,"METADATA_CONTRACT",status,false,false);
   }
   phase="CSRF";status=null;
   var token=await executor.ObterTokenCsrfAsync(executor.ConstruirUri("/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/"),timeout.Token);
   var session=token.Length>0&&cookies.Count>0;
   return new(session,session?"COMPLETE":"CSRF_SESSION",200,true,session);
  }
  catch(SapHttpStatusException e){return new(false,phase,(int)e.StatusCode,metadata,false);}
  catch{return new(false,phase,status,metadata,false);}
 }
}
