using FugaTES.Aplicacao.Configuracao;
namespace FugaTES.HostIntegracao;
// Legacy profiles without SAP fields remain DEV110. A partial/invalid pair never falls back.
public static class AcmEnvironmentBinding
{
 public static AmbienteSap Sap(IConfiguration config)
 {
  var origin=config["Sap:BaseUrl"];var client=config["Sap:SapClient"];
  if(origin is null&&client is null)return AmbientesSap.Dev110;
  if(!Uri.TryCreate(origin,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0)
   throw new InvalidOperationException("ACM_SAP_ENVIRONMENT_PAIR_REJECTED");
  return AmbientesSap.Por(uri,client)??throw new InvalidOperationException("ACM_SAP_ENVIRONMENT_PAIR_REJECTED");
 }
 public static AmbienteBanco Banco(IConfiguration config)=>AmbientesBanco.PorNome(Sap(config).Nome)??throw new InvalidOperationException("ACM_DATABASE_ENVIRONMENT_REJECTED");
}
