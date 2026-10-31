using System.Globalization;
using System.Xml.Linq;
using FugaTES.Aplicacao.Modelos.Guardian;
namespace FugaTES.Integracao.Guardian;

// One codec for the CadastraContrato wire and its offline preview. No transport or authorization.
public static class GuardianContratoSoapCodec
{
 public const string Namespace="http://toledobrasil.com.br/WS_Guardian";
 public static readonly string[] CamposObrigatorios=["Codigo","Descricao","ValorContrato","Estado","ValorProximidadeFimContrato","TipoLancamento"];
 public static XDocument CriarCadastro(ContratoGuardianSet26 contrato)
 {
  ArgumentNullException.ThrowIfNull(contrato);
  if(new[]{contrato.Codigo,contrato.Descricao,contrato.Estado,contrato.TipoLancamento}.Any(Pendente)||contrato.ValorContrato<0||contrato.ValorProximidadeFimContrato<0)
   throw new GuardianHttpException("Guardian contract mapping is incomplete or unverified.");
  return CriarRascunho(new Dictionary<string,string?>{["Codigo"]=contrato.Codigo,["Descricao"]=contrato.Descricao,
   ["ValorContrato"]=contrato.ValorContrato.ToString(CultureInfo.InvariantCulture),["Estado"]=contrato.Estado,
   ["ValorProximidadeFimContrato"]=contrato.ValorProximidadeFimContrato.ToString(CultureInfo.InvariantCulture),["TipoLancamento"]=contrato.TipoLancamento,["CodigoEmissor"]=contrato.CodigoEmissor,["CodigoItem"]=contrato.CodigoItem});
 }
 public static XDocument CriarRascunho(IReadOnlyDictionary<string,string?> valores)
 {
  XNamespace soap="http://schemas.xmlsoap.org/soap/envelope/";XNamespace ns=Namespace;
  var fields=CamposObrigatorios.Concat(new[]{"CodigoEmissor","CodigoItem"}.Where(name=>valores.ContainsKey(name)&&valores[name] is not null)).Select(name=>new XElement(ns+name,valores.TryGetValue(name,out var v)&&!Pendente(v)?(object)v!:new XComment("NAO VALIDADO - RASCUNHO NAO ENVIAVEL")));
  return new(new XElement(soap+"Envelope",new XAttribute(XNamespace.Xmlns+"soap",soap),new XElement(soap+"Body",
   new XElement(ns+"CadastraContrato",new XElement(ns+"voConfiguracaoContrato"),new XElement(ns+"voDadosContrato",new XElement(ns+"Contrato",fields))))));
 }
 static bool Pendente(string? value)=>string.IsNullOrWhiteSpace(value)||new[]{"NÃO VALIDADO","NAO VALIDADO","PENDENTE"}.Contains(value.Trim(),StringComparer.OrdinalIgnoreCase);
}
