using System.Net;
using System.Text;
using System.Xml.Linq;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Integracao.Guardian;
using Xunit;
public sealed class GuardianContractReadShapeTests
{
 static readonly XNamespace Ns="http://toledobrasil.com.br/WS_Guardian",Soap="http://schemas.xmlsoap.org/soap/envelope/";
 [Theory][InlineData("1183")][InlineData("0000001183")]
 public async Task Documented_VOContratoOut_reads_nested_integration_codes_and_preserves_literal_identity(string code)
 {var r=await Read(Contract(code),code);Assert.Equal(EstadoConsultaContratoGuardian.Existente,r.Estado);Assert.Equal(code,r.Contrato!.Codigo);Assert.Equal("20000434",r.Contrato.CodigoEmissor);Assert.Equal("7000000",r.Contrato.CodigoItem);Assert.Equal(608,r.SequencialContrato);}
 [Theory][InlineData("duplicate_emitter")][InlineData("conflicting_emitter")][InlineData("conflicting_item")][InlineData("sequence_only")][InlineData("foreign_namespace")][InlineData("duplicate_code")][InlineData("wrong_contract_code")]
 public async Task Ambiguous_foreign_or_unproven_nested_identity_stays_indeterminate(string fault)
 {
  var c=Contract("1183");var emitter=c.Element(Ns+"EmissorIntegracao")!;
  if(fault=="duplicate_emitter")c.Add(new XElement(emitter));
  if(fault=="conflicting_emitter")c.Add(new XElement(Ns+"CodigoEmissor","999"));
  if(fault=="conflicting_item")c.Add(new XElement(Ns+"CodigoItem","999"));
  if(fault=="sequence_only")emitter.Element(Ns+"Codigo")!.Remove();
  if(fault=="foreign_namespace")emitter.Name=(XNamespace)"urn:foreign"+"EmissorIntegracao";
  if(fault=="duplicate_code")emitter.Add(new XElement(Ns+"Codigo","999"));
  if(fault=="wrong_contract_code")c.Element(Ns+"Codigo")!.Value="0000001183";
  Assert.Equal(EstadoConsultaContratoGuardian.Indeterminado,(await Read(c,"1183")).Estado);
 }
 [Fact]public async Task Consistent_legacy_fields_remain_compatible_with_documented_output()
 {var c=Contract("1183");c.Add(new XElement(Ns+"CodigoEmissor","20000434"),new XElement(Ns+"CodigoItem","7000000"));Assert.Equal(EstadoConsultaContratoGuardian.Existente,(await Read(c,"1183")).Estado);}
 static XElement Contract(string code)=>new(Ns+"Contrato",new XElement(Ns+"Sequencial",608),new XElement(Ns+"Codigo",code),new XElement(Ns+"Descricao","SYNTHETIC_CONTRACT"),new XElement(Ns+"ValorContrato","150000.00000"),new XElement(Ns+"Estado","308102"),new XElement(Ns+"ValorProximidadeFimContrato","0.00000"),new XElement(Ns+"TipoLancamento","309837"),new XElement(Ns+"EmissorIntegracao",new XElement(Ns+"Sequencial",99),new XElement(Ns+"Codigo","20000434")),new XElement(Ns+"ItemIntegracao",new XElement(Ns+"Sequencial",18),new XElement(Ns+"Codigo","7000000")));
 static async Task<ConsultaContratoGuardian> Read(XElement contract,string code)
 {var xml=new XDocument(new XElement(Soap+"Envelope",new XElement(Soap+"Body",new XElement(Ns+"ConsultaContratoResponse",new XElement(Ns+"voRetornoContrato",new XElement(Ns+"VORetornoContrato",new XElement(Ns+"Erro",0),contract))))));using var http=new HttpClient(new Wire(xml.ToString()));return await new GuardianContratoSoapClient(http,new GuardianHttpOptions{ContratoSoapEndpoint=new("http://192.168.17.91/WS_GUARDIAN/WS_GUARDIAN_PLUS.asmx"),ContratoSoapEnvironment="HML",ContratoSoapInternalHmlHttpApproved=true,Timeout=TimeSpan.FromSeconds(2)}).ConsultarAsync(code,default);}
 sealed class Wire(string xml):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Assert.Equal(HttpMethod.Post,request.Method);Assert.Equal("\"http://toledobrasil.com.br/WS_Guardian/ConsultaContrato\"",request.Headers.GetValues("SOAPAction").Single());return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(xml,Encoding.UTF8,"text/xml")});}}
}
