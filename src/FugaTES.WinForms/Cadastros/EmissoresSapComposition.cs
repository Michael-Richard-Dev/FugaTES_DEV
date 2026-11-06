using FugaTES.HostIntegracao;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.WinForms.Transportadoras;
namespace FugaTES.WinForms.Cadastros;
public static class EmissoresSapComposition
{
 public static FrmEmissoresSap Criar()
 {
  HttpClient? http=null;
  try
  {
   var config=CarrierDesktopProfile.Load(Path.Combine(AppContext.BaseDirectory,"FugaTES.carrier.json"));
   http=new(new SomenteLeitura(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false})){Timeout=Timeout.InfiniteTimeSpan};
   var query=new EmissoresSapConsultaAdapter(http,new OpcoesSap{BaseUrl=new Uri(config["Sap:BaseUrl"]??""),SapClient=config["Sap:SapClient"],Timeout=TimeSpan.FromSeconds(30)},new CarrierServiceSapAuthentication(config));
   return new(query,http);
  }
  catch{http?.Dispose();return new(bloqueio:"SAP_PROFILE_NOT_AVAILABLE");}
 }
 sealed class SomenteLeitura(HttpMessageHandler inner):DelegatingHandler(inner)
 {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
  {
   // Guarda de transporte: ve a requisicao, nao o perfil, e por isso pergunta "este host e um SAP
   // nosso?". Ate 05/11/2026 exigia o host do DEV escrito, e a tela de emissores de um desktop de
   // QAS120 era barrada pela propria guarda. Quem confere o PAR origem+client e o adaptador, no
   // seu construtor, contra o perfil deste desktop.
   if(r.Method!=HttpMethod.Get||r.RequestUri is not{} uri||uri.Scheme!="https"||!FugaTES.Aplicacao.Configuracao.AmbientesSap.EhOrigemAprovada(uri.GetLeftPart(UriPartial.Authority))||!uri.AbsolutePath.StartsWith("/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner",StringComparison.Ordinal))throw new HttpRequestException("EMITTER_GET_SCOPE_REQUIRED");
   return base.SendAsync(r,ct);
  }
 }
}
