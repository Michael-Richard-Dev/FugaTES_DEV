using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Recebimento;
namespace FugaTES.Testes;
// Only synthetic offline contract evidence. Never used in production or persisted in a business database.
internal static class ToledoSapPartnerFixture
{
 internal static readonly DateTimeOffset Received=new(2026,10,30,18,0,0,TimeSpan.Zero);
 internal static ToledoSapContratoFonte Source(string contract="0099999903",string partner="90000003",string material="9000000",string center="3011",long version=1,string? item=null,DateTimeOffset? at=null)
 {
  var raw=JsonSerializer.Serialize(new{SistemaBalanca="Toledo",NumeroContrato=contract,Centro=center,Material=material,IdParceiro=partner,CentroOpcional=Array.Empty<string>(),Item=item});
  var canonical=new ContratoSapToledoCanonicalizer().Canonicalizar(JsonSerializer.Deserialize<ContratoSapToledoSnapshot>(raw)!);
  return new(contract,version,Guid.NewGuid(),canonical.FingerprintSha256,canonical.FingerprintSha256,canonical.PayloadCanonicoJson,at??Received.AddHours(-1),"SAP","TOLEDO","SAP_CONTRATO_TOLEDO",contract);
 }
 internal static IReadOnlyList<ToledoSapContratoFonte> Sources(string raw)
 {
  var json=JsonNode.Parse(raw)!;var center=json["Centro"]!.ToString();var material=json["Material"]!.ToString();
  return json["to_Pesagem"]!.AsArray().Select(x=>Source(x!["ContratoACM"]!.ToString(),x["IdParceiro"]!.ToString(),material,center)).GroupBy(x=>x.NumeroContrato).Select(x=>x.First()).ToArray();
 }
 internal static DocumentoToledoSap Bind(DocumentoToledoSap doc)=>doc with{DeliveryPartners=ToledoDeliveryPartnerTests.Proof(doc.DocumentoJson),RecebidoEm=Received,ContratosSap=Sources(doc.DocumentoJson)};
}
