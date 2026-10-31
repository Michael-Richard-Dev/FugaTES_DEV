using System.Text.Json;
using FugaTES.Integracao.SAP.Http;
namespace FugaTES.Testes;
public sealed class ToledoSapOData201Tests
{
 public const string Payload="""{"SistemaBalanca":"Toledo","Centro":"3011","IdPesagem":900003,"DataPesagem":"/Date(1767225600000)/","TipoPesagem":"2","Material":"9000000","PesoBruto":"40000.0","PesoTara":"20000.0","PesoAntesDesconto":"20000.0","PesoAposDesconto":"20000.0","to_QualidPes":[{"Tipo":"IMPUREZA","ValorAferido":"1.1"},{"Tipo":"UMIDADE","ValorAferido":"2.2"}],"to_Pesagem":[{"Indice":1,"NrIndiceTotal":1,"ContratoACM":"0099999903","IdParceiro":"90000003","IdParceiroEntrega":"90000003","ParceiroEntregaNome":"SAP SYNTHETIC SUPPLIER"}]}""";
 static ToledoSapPreparedPayload Prepare(string? json=null,string? zone=null)=>ToledoSapODataPayload.Prepare(json??Payload,new(zone));
 static string Xml()=>File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","ToledoSapCreated.SYNTHETIC.xml"));
 static readonly Uri Resource=new("https://sap.invalid/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem?sap-client=110");
 [Fact] public void Numeric_identifier_is_sent_as_text_and_original_document_is_unchanged()
 {var source=Payload;var p=Prepare(source);using var j=JsonDocument.Parse(p.Json);Assert.Equal(JsonValueKind.String,j.RootElement.GetProperty("IdPesagem").ValueKind);Assert.Equal("0000900003",p.IdPesagem);Assert.Equal(Payload,source);Assert.DoesNotContain("TipoOperacao",p.Json);}
 [Fact] public void Real_local_Guardian_date_is_converted_in_configured_zone_not_copied_from_the_example()
 {var p=Prepare(Payload.Replace("/Date(1767225600000)/","10/27/2026 12:00:22 PM"),"America/Sao_Paulo");Assert.Equal(new DateTimeOffset(2026,10,27,15,0,22,TimeSpan.Zero).ToUnixTimeMilliseconds(),p.EpochMilliseconds);Assert.DoesNotContain("1772874589888",p.Json);}
 [Fact] public void Local_date_without_explicit_zone_is_blocked()
 {var e=Assert.Throws<ToledoSapPayloadException>(()=>Prepare(Payload.Replace("/Date(1767225600000)/","10/27/2026 12:00:22 PM")));Assert.Equal("SAP_WEIGHING_TIMEZONE_REQUIRED",e.Code);}
 [Fact] public void Existing_epoch_is_preserved_exactly()
 {Assert.Equal(1767225600000,Prepare().EpochMilliseconds);}
 [Fact] public void Empty_placeholders_are_removed_but_real_zero_measurement_is_retained()
 {var json=Payload.Replace("[{\"Tipo\":\"IMPUREZA\",\"ValorAferido\":\"1.1\"}","[{\"Tipo\":\"\",\"ValorAferido\":\"0.0\"},{\"Tipo\":\"IMPUREZA\",\"ValorAferido\":\"0.0\"}");using var j=JsonDocument.Parse(Prepare(json).Json);var q=j.RootElement.GetProperty("to_QualidPes");Assert.Equal(2,q.GetArrayLength());Assert.Equal("0.0",q[0].GetProperty("ValorAferido").GetString());}
 [Fact] public void Quality_values_are_not_replaced_by_the_operator_example()
 {var p=Prepare();Assert.DoesNotContain("9.6",p.Json);Assert.Contains("1.1",p.Json);Assert.Contains("2.2",p.Json);}
 [Theory][InlineData("\"Tipo\":\"IMPUREZA\"","\"Tipo\":\"\"")][InlineData("\"ValorAferido\":\"1.1\"","\"ValorAferido\":\"\"")]
 public void Incomplete_measurement_does_not_acquire_an_invented_default(string before,string after)
 {Assert.Throws<ToledoSapPayloadException>(()=>Prepare(Payload.Replace(before,after)));}
 [Fact] public void Proven_Atom_receipt_matches_the_prepared_identity_items_and_measurements()
 {var receipt=ToledoSapAtomReceipt.Validate(Xml(),Prepare(),Resource);Assert.NotNull(receipt);Assert.Contains("900003",receipt.EntityId);Assert.Equal(64,receipt.ResponseSha256.Length);}
 [Theory][InlineData("900003","900004")][InlineData("0099999903","0099999904")][InlineData("2026-01-01T00:00:00Z","2026-03-07T00:00:00Z")][InlineData(">1.1<",">7.7<")]
 public void Foreign_or_divergent_receipt_cannot_confirm_a_POST(string before,string after)
 {Assert.Null(ToledoSapAtomReceipt.Validate(Xml().Replace(before,after),Prepare(),Resource));}
 [Fact] public void Confirmed_Atom_receipt_must_preserve_the_requested_SAP_direction()
 {Assert.Null(ToledoSapAtomReceipt.Validate(Xml().Replace(">S<",">E<"),Prepare(),Resource));Assert.Null(ToledoSapAtomReceipt.Validate(Xml().Replace(">S<",">2<"),Prepare(),Resource));}
 [Theory][InlineData("<html>created</html>")][InlineData("<entry/>")][InlineData("<!DOCTYPE entry [<!ENTITY x SYSTEM 'file:///C:/secret'>]><entry>&x;</entry>")]
 public void HTML_missing_contract_and_external_XML_entities_are_rejected(string body)
 {Assert.Null(ToledoSapAtomReceipt.Validate(body,Prepare(),Resource));}
 [Fact] public void Same_quality_type_with_different_groups_is_still_a_duplicate_SAP_key()
 {var json=Payload.Replace("\"Tipo\":\"IMPUREZA\"","\"Tipo\":\"IMPUREZA\",\"Grupo\":\"A\"").Replace("\"Tipo\":\"UMIDADE\"","\"Tipo\":\"IMPUREZA\",\"Grupo\":\"B\"");Assert.Equal("SAP_QUALITY_DUPLICATE",Assert.Throws<ToledoSapPayloadException>(()=>Prepare(json)).Code);}
 [Theory][InlineData("<html/>")][InlineData("<feed xmlns='http://www.w3.org/2005/Atom'><id>https://sap.invalid/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem</id></feed>")]
 public void Uncounted_or_non_OData_feed_cannot_prove_absence(string body)=>Assert.False(ToledoSapAtomReceipt.ProvesEmptyCollection(body,Resource));
 [Fact] public void Redirected_authority_cannot_confirm_a_receipt()
 {Assert.Null(ToledoSapAtomReceipt.Validate(Xml().Replace("https://sap.invalid","https://other.invalid"),Prepare(),Resource));}
}
