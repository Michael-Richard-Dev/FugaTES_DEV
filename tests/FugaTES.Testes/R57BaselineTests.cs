using System.Text.Json;
using FugaTES.Integracao.Guardian;
using FugaTES.Callback.Http;
using FugaTES.Aplicacao.Modelos.Guardian;
public class R57BaselineTests
{
 [Fact] public void Soap_requires_proven_emitter_and_item_on_wire()
 {
  var xml=GuardianContratoSoapCodec.CriarCadastro(new ContratoGuardianSet26("1106","Synthetic",12.345m,"E","T"=="T"?0m:1m,"L"){CodigoEmissor="EM02",CodigoItem="MAT02"});
  var contract=xml.Descendants().Single(x=>x.Name.LocalName=="Contrato");
  Assert.Equal("EM02",contract.Elements().SingleOrDefault(x=>x.Name.LocalName=="CodigoEmissor")?.Value);
  Assert.Equal("MAT02",contract.Elements().SingleOrDefault(x=>x.Name.LocalName=="CodigoItem")?.Value);
 }
 [Fact] public void Sap_item_and_unit_are_retained_without_becoming_version()
 {
  var item=JsonSerializer.Deserialize<SapContractInboundItemHttpDto>("{\"NumeroContrato\":\"0000001106\",\"Item\":\"000020\",\"UnidCont\":\"KG\",\"QuantidadeContrato\":12.345}")!;
  var json=JsonSerializer.Serialize(SapContractInboundMapper.MapearSnapshot(item));
  using var doc=JsonDocument.Parse(json);
  Assert.True(doc.RootElement.TryGetProperty("Item",out var value)); Assert.Equal("000020",value.GetString());
  Assert.Equal("KG",doc.RootElement.GetProperty("UnidCont").GetString());
 }
}
