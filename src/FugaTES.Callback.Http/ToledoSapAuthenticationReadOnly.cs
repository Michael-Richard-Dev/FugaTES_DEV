using System.Net;
using System.Xml;
using System.Xml.Linq;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.SAP.Http;
namespace FugaTES.Callback.Http;

// Administrative installation check. Reuses the operational provider, makes GETs only,
// does not build the web host, open PostgreSQL, create a worker, or emit tokens/cookies.
public static class ToledoSapAuthenticationReadOnly
{
 public const string Flag="--toledo-sap-auth-read-only";
 public static bool Selected(string[] args)
 {
  if(!args.Any(a=>a.StartsWith(Flag,StringComparison.Ordinal)))return false;
  if(args.Length!=2||args[0]!=Flag||!Path.IsPathFullyQualified(args[1]))throw new InvalidOperationException("TOLEDO_SAP_CHECK_ARGUMENTS_INVALID");
  return true;
 }
 public static async Task<int> RunAsync(string directory)
 {
  try
  {
   if(!OperatingSystem.IsLinux())throw new InvalidOperationException();
   var origin=new Uri("https://vhfufds4ci.sap.fugacouros.com.br:44300");
   using var transport=new ToledoSapHttpTransport();
   var executor=new SapODataHttpExecutor(transport.Client,new(){BaseUrl=origin,SapClient="110",Timeout=TimeSpan.FromSeconds(20)},new CarrierLinuxSapAuthentication(origin,new CarrierSystemdCredentials(directory),"FUGATES"),new CookieContainer());
   using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
   var metadata=await executor.ObterAtomAsync(executor.ConstruirUri("/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/$metadata"),timeout.Token);
   if((int)metadata.Status!=200)throw new InvalidOperationException();
   using var text=new StringReader(metadata.Body);using var xml=XmlReader.Create(text,new(){DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1048576});
   var root=XDocument.Load(xml).Root;
   if(root?.Name.LocalName!="Edmx"||!root.Descendants().Any(e=>e.Name.LocalName=="EntitySet"&&(string?)e.Attribute("Name")=="ReceberPesagem"))throw new InvalidOperationException();
   _=await executor.ObterTokenCsrfAsync(executor.ConstruirUri("/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/"),timeout.Token);
   Console.WriteLine("TOLEDO_SAP_AUTHENTICATION_READ_ONLY=PASS;CSRF_SESSION=PASS;SAP_POSTS=0");return 0;
  }
  catch{Console.WriteLine("TOLEDO_SAP_AUTHENTICATION_READ_ONLY=BLOCKED;SAP_POSTS=0");return 1;}
 }
}
