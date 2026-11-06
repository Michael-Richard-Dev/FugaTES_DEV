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

 /// <summary>
 /// Uso: --toledo-sap-auth-read-only &lt;diretorio&gt; &lt;DEV110|QAS120&gt;
 ///
 /// O ambiente E OBRIGATORIO, e nao tem padrao de proposito. Ate 05/11/2026 esta checagem tinha
 /// o DEV110 escrito dentro: rodada na instalacao de QAS120 ela conferiria o DEV e imprimiria
 /// PASS, e quem a rodou concluiria que o QAS esta bom. Um atestado que nao diz de qual ambiente
 /// fala nao e um atestado - e por isso que o nome do ambiente tambem sai na linha de saida.
 ///
 /// Quem ja chamava com dois argumentos passa a receber TOLEDO_SAP_CHECK_ARGUMENTS_INVALID. Falhar
 /// alto e melhor do que conferir o ambiente errado em silencio.
 /// </summary>
 public static bool Selected(string[] args)
 {
  if(!args.Any(a=>a.StartsWith(Flag,StringComparison.Ordinal)))return false;
  if(args.Length!=3||args[0]!=Flag||!Path.IsPathFullyQualified(args[1])||Ambiente(args[2]) is null)throw new InvalidOperationException("TOLEDO_SAP_CHECK_ARGUMENTS_INVALID");
  return true;
 }

 static FugaTES.Aplicacao.Configuracao.AmbienteSap? Ambiente(string? nome)
  => FugaTES.Aplicacao.Configuracao.AmbientesSap.Aprovados.FirstOrDefault(a=>string.Equals(a.Nome,nome?.Trim(),StringComparison.OrdinalIgnoreCase));

 public static async Task<int> RunAsync(string directory,string nomeAmbiente)
 {
  var ambiente=Ambiente(nomeAmbiente);
  try
  {
   if(!OperatingSystem.IsLinux()||ambiente is null)throw new InvalidOperationException();
   var origin=new Uri(ambiente.Origem);
   using var transport=new ToledoSapHttpTransport();
   var executor=new SapODataHttpExecutor(transport.Client,new(){BaseUrl=origin,SapClient=ambiente.SapClient,Timeout=TimeSpan.FromSeconds(20)},new CarrierLinuxSapAuthentication(origin,new CarrierSystemdCredentials(directory),"FUGATES"),new CookieContainer());
   using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
   var metadata=await executor.ObterAtomAsync(executor.ConstruirUri("/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/$metadata"),timeout.Token);
   if((int)metadata.Status!=200)throw new InvalidOperationException();
   using var text=new StringReader(metadata.Body);using var xml=XmlReader.Create(text,new(){DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1048576});
   var root=XDocument.Load(xml).Root;
   if(root?.Name.LocalName!="Edmx"||!root.Descendants().Any(e=>e.Name.LocalName=="EntitySet"&&(string?)e.Attribute("Name")=="ReceberPesagem"))throw new InvalidOperationException();
   _=await executor.ObterTokenCsrfAsync(executor.ConstruirUri("/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/"),timeout.Token);
   Console.WriteLine("TOLEDO_SAP_AUTHENTICATION_READ_ONLY=PASS;AMBIENTE="+ambiente.Nome+";CSRF_SESSION=PASS;SAP_POSTS=0");return 0;
  }
  catch{Console.WriteLine("TOLEDO_SAP_AUTHENTICATION_READ_ONLY=BLOCKED;AMBIENTE="+(ambiente?.Nome??"DESCONHECIDO")+";SAP_POSTS=0");return 1;}
 }
}
