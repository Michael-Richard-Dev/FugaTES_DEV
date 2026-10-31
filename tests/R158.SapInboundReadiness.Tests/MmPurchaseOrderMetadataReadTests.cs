using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FugaTES.HostIntegracao;
using FugaTES.WinForms.Runtime;
using FugaTES.WinForms.Transportadoras;
namespace FugaTES.Testes;
public sealed class MmPurchaseOrderMetadataReadTests
{
 [Fact] public async Task Optional_authorized_MM_metadata_GET_exports_only_schema_names_and_types()
 {
  var output=Environment.GetEnvironmentVariable("FUGATES_MM_METADATA_RESULT");if(string.IsNullOrWhiteSpace(output))return;
  var result=new Dictionary<string,object?>{{"method","GET"},{"businessWrites",0},{"credentialExported",false}};
  try
  {
   var config=CarrierDesktopProfile.Load(@"C:\FugaTES_DEV110\FugaTES.carrier.json");
   var origin="https://vhfufds4ci.sap.fugacouros.com.br:44300";
   Assert.Equal("110",config["Sap:SapClient"]);Assert.Equal(origin,new Uri(config["Sap:BaseUrl"]!).GetLeftPart(UriPartial.Authority));
   using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false}){Timeout=TimeSpan.FromSeconds(25)};
   using var request=new HttpRequestMessage(HttpMethod.Get,origin+"/sap/opu/odata4/sap/api_purchaseorder_2/srvd_a2x/sap/purchaseorder/0001/$metadata?sap-client=110");
   request.Headers.TryAddWithoutValidation("Accept","application/xml");await new CarrierServiceSapAuthentication(config).AplicarAsync(request,default);
   using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);result["http"]=(int)response.StatusCode;
   Assert.Equal(200,(int)response.StatusCode);
   await using var input=await response.Content.ReadAsStreamAsync();using var memory=new MemoryStream();var buffer=new byte[8192];int count;
   while((count=await input.ReadAsync(buffer))>0){Assert.True(memory.Length+count<=4194304,"METADATA_SIZE_LIMIT");memory.Write(buffer,0,count);}memory.Position=0;
   using var xml=XmlReader.Create(memory,new(){DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=4194304});var document=XDocument.Load(xml);
   var sets=document.Descendants().Where(n=>n.Name.LocalName=="EntitySet"&&((string?)n.Attribute("Name") is "PurchaseOrder" or "PurchaseOrderItem")).ToArray();Assert.Equal(2,sets.Length);
   var entities=sets.Select(set=>
   {
    var full=(string)set.Attribute("EntityType")!;var name=full[(full.LastIndexOf('.')+1)..];var type=document.Descendants().Single(n=>n.Name.LocalName=="EntityType"&&(string?)n.Attribute("Name")==name);
    return new{name=(string?)set.Attribute("Name"),properties=type.Elements().Where(n=>n.Name.LocalName=="Property").Select(n=>new{name=(string?)n.Attribute("Name"),type=(string?)n.Attribute("Type"),maxLength=(string?)n.Attribute("MaxLength")}).ToArray(),navigation=type.Elements().Where(n=>n.Name.LocalName=="NavigationProperty").Select(n=>new{name=(string?)n.Attribute("Name"),type=(string?)n.Attribute("Type")}).ToArray()};
   }).ToArray();
   result["result"]="PASS";result["entities"]=entities;
  }
  catch(Exception ex){result["result"]="BLOCKED";result["code"]=ex.GetType().Name;File.WriteAllText(output,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));throw new InvalidOperationException("MM_METADATA_READ_BLOCKED_"+ex.GetType().Name);}
  File.WriteAllText(output,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
 }
}
