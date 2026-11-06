using FugaTES.HostIntegracao;
using FugaTES.Aplicacao.Consultas.CadastrosSap;
using FugaTES.Integracao.SAP.Cadastros;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.WinForms.Transportadoras;
namespace FugaTES.WinForms.Cadastros;
public static class CadastrosSapComposition
{
 public static FrmCadastroSap Criar(CategoriaCadastroSap categoria)
 {
  HttpClient? http=null;
  try
  {
   var config=CarrierDesktopProfile.Load(Path.Combine(AppContext.BaseDirectory,"FugaTES.carrier.json"));
   http=new(new SomenteLeitura(categoria,new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false})){Timeout=Timeout.InfiniteTimeSpan};
   var opcoes=new OpcoesSap{BaseUrl=new Uri(config["Sap:BaseUrl"]??""),SapClient=config["Sap:SapClient"],Timeout=TimeSpan.FromSeconds(30)};var auth=new CarrierServiceSapAuthentication(config);
   ICadastroSapConsulta query=categoria switch{CategoriaCadastroSap.Materiais=>new MateriaisSapConsultaAdapter(http,opcoes,auth),CategoriaCadastroSap.Transportadoras=>new TransportadorasSapConsultaAdapter(http,opcoes,auth),_=>throw new ArgumentException("SAP_CATEGORY_INVALID")};
   return new(categoria,query,http);
  }
  catch{http?.Dispose();return new(categoria,bloqueio:"SAP_PROFILE_NOT_AVAILABLE");}
 }
 sealed class SomenteLeitura(CategoriaCadastroSap categoria,HttpMessageHandler inner):DelegatingHandler(inner)
 {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
  {
   var prefix=categoria==CategoriaCadastroSap.Materiais?"/sap/opu/odata/sap/API_PRODUCT_SRV/A_Product":"/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner";
   // Mesma razao da guarda de emissores: aqui so se ve a requisicao, logo a pergunta e se o host
   // e um SAP aprovado. O par origem+client e conferido pelo construtor do adaptador, contra o
   // perfil deste desktop.
   if(r.Method!=HttpMethod.Get||r.RequestUri is not{} uri||uri.Scheme!="https"||!FugaTES.Aplicacao.Configuracao.AmbientesSap.EhOrigemAprovada(uri.GetLeftPart(UriPartial.Authority))||!uri.AbsolutePath.StartsWith(prefix,StringComparison.Ordinal))throw new HttpRequestException("CADASTRO_GET_SCOPE_REQUIRED");
   return base.SendAsync(r,ct);
  }
 }
}
