using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Consultas.RecebimentosToledo;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Integracao.SAP.Http;
using FugaTES.Persistencia.Consultas.RecebimentosToledo;
using FugaTES.WinForms.Toledo;
namespace FugaTES.Testes;
public sealed class ToledoSapJsonReadbackTests
{
 static readonly Guid Receipt=Guid.Parse("12345678-1111-2222-3333-444444444444"),Attempt=Guid.Parse("12345678-1111-2222-3333-555555555555");
 static readonly DateTimeOffset At=DateTimeOffset.Parse("2026-10-30T10:00:00Z");
 static string Hash(string s)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
 static ToledoSapJsonFonte Source(string? raw=null,bool sent=true)
 {
  raw??=ToledoSapOData201Tests.Payload;var p=ToledoSapODataPayload.Prepare(raw,new("America/Sao_Paulo"));var fingerprint=RegistrarPesagemToledoCasoDeUso.Fingerprint(raw);
  return new(new(Receipt,raw,fingerprint),Guid.NewGuid(),ToledoSapWork.Fingerprint(Receipt,fingerprint),1,
   [new(Guid.NewGuid(),"toledo_sap_documento_preparado",Attempt,At,p.Sha256,ToledoSapPreparedPayload.Version,p.EpochMilliseconds,null,null,null,true),
    new(Guid.NewGuid(),"toledo_sap_resultado",Attempt,At.AddSeconds(1),p.Sha256,ToledoSapPreparedPayload.Version,null,sent,201,sent?"RecebimentoConfirmadoSap":"RecebimentoJaConfirmadoSap",true)],false){DeliveryPartners=ToledoDeliveryPartnerTests.Proof(raw),RecebidoEm=ToledoSapPartnerFixture.Received,ContratosSap=ToledoSapPartnerFixture.Sources(raw)};
 }
 [Fact] public void Sent_JSON_requires_the_exact_prepared_and_result_hash_and_preserves_original()
 {
  var source=Source();var view=ToledoSapDocumentoLeitura.Preparar(source);Assert.Equal("ENVIADO_CONFERIDO",view.Estado);Assert.Equal(Attempt,view.TentativaId);
  Assert.Equal(view.Sha256,Hash(view.Json!));Assert.Equal(201,view.HttpStatus);Assert.Equal(ToledoSapOData201Tests.Payload,source.Original.DocumentoJson);
  using var j=JsonDocument.Parse(view.Json!);Assert.Equal("S",j.RootElement.GetProperty("TipoPesagem").GetString());Assert.Equal("0000900003",j.RootElement.GetProperty("IdPesagem").GetString());
 }
 [Fact] public void No_POST_evidence_is_never_called_sent_even_when_HTTP_is_201()
 {var view=ToledoSapDocumentoLeitura.Preparar(Source(sent:false));Assert.Equal("PREPARADO_SEM_POST_COMPROVADO",view.Estado);Assert.NotNull(view.Json);}
 [Fact] public void Prepared_intent_alone_is_not_a_sent_POST()
 {var s=Source();var v=ToledoSapDocumentoLeitura.Preparar(s with{Eventos=[s.Eventos[0]]});Assert.Equal("PREPARADO_SEM_POST_COMPROVADO",v.Estado);}
 [Fact] public void Receipt_without_integration_gets_only_an_explicit_current_preview()
 {var s=Source();var v=ToledoSapDocumentoLeitura.Preparar(s with{IntegracaoId=null,FingerprintIntegracao=null,IntegracoesEncontradas=0,Eventos=[]});Assert.Equal("PREVIA_NAO_ENVIADA",v.Estado);Assert.Null(v.TentativaId);Assert.NotNull(v.Json);}
 [Theory][InlineData("hash")][InlineData("version")][InlineData("epoch")][InlineData("resultHash")][InlineData("resultVersion")][InlineData("foreignAttempt")][InlineData("originalHash")][InlineData("integrationHash")][InlineData("truncated")][InlineData("ambiguous")][InlineData("duplicateResult")][InlineData("duplicatePrepared")][InlineData("invalidEvent")][InlineData("noPrepared")]
 public void Ambiguous_missing_or_inconsistent_proof_is_blocked_without_substituting_a_preview(string kind)
 {
  var s=Source();var p=s.Eventos[0];var r=s.Eventos[1];
  s=kind switch {
   "hash"=>s with{Eventos=[p with{RequestSha256=new string('A',64)},r with{RequestSha256=new string('A',64)}]},
   "version"=>s with{Eventos=[p with{PayloadVersion="UNKNOWN"},r with{PayloadVersion="UNKNOWN"}]},
   "epoch"=>s with{Eventos=[p with{EpochMilliseconds=0},r]},
   "resultHash"=>s with{Eventos=[p,r with{RequestSha256=new string('B',64)}]},
   "resultVersion"=>s with{Eventos=[p,r with{PayloadVersion="UNKNOWN"}]},
   "foreignAttempt"=>s with{Eventos=[p,r with{TentativaId=Guid.NewGuid()}]},
   "originalHash"=>s with{Original=s.Original with{FingerprintSha256=new string('C',64)}},
   "integrationHash"=>s with{FingerprintIntegracao=new string('D',64)},
   "truncated"=>s with{EvidenciaTruncada=true},"ambiguous"=>s with{IntegracoesEncontradas=2},
   "duplicateResult"=>s with{Eventos=[p,r,r with{Id=Guid.NewGuid()}]},
   "duplicatePrepared"=>s with{Eventos=[p,p with{Id=Guid.NewGuid()},r]},
   "invalidEvent"=>s with{Eventos=[p,r with{Valido=false}]},
   _=>s with{Eventos=[r]}};
  var v=ToledoSapDocumentoLeitura.Preparar(s);Assert.Equal("INDISPONIVEL",v.Estado);Assert.Null(v.Json);
 }
 [Fact] public void Failed_or_unknown_outcome_can_show_proven_request_but_does_not_claim_business_success()
 {var s=Source();var v=ToledoSapDocumentoLeitura.Preparar(s with{Eventos=[s.Eventos[0],s.Eventos[1] with{HttpStatus=null,Codigo="ResultadoIndeterminado"}]});Assert.Equal("ENVIADO_CONFERIDO",v.Estado);Assert.Null(v.HttpStatus);Assert.Contains("separadamente",v.Mensagem);}
 [Theory][InlineData(false)][InlineData(true)]
 public void Historical_direction_and_unpadded_identifier_are_shown_only_when_exact_hash_matches(bool numeric)
 {
  var s=Source();var p=ToledoSapODataPayload.Prepare(s.Original.DocumentoJson,new());var historic=JsonNode.Parse(p.Json)!;
  historic["IdPesagem"]="900003";if(numeric)historic["TipoPesagem"]="2";
  var hash=Hash(historic.ToJsonString());s=s with{Eventos=s.Eventos.Select(x=>x with{RequestSha256=hash}).ToArray()};
  var v=ToledoSapDocumentoLeitura.Preparar(s);Assert.Equal("ENVIADO_CONFERIDO",v.Estado);Assert.Equal(hash,Hash(v.Json!));Assert.NotEqual("Current",v.Projecao);
 }
 [Fact] public void Historical_read_does_not_disable_the_current_NFe_send_guard()
 {
  var s=Source();var original=JsonNode.Parse(s.Original.DocumentoJson)!;original["TipoPesagem"]="E";var raw=original.ToJsonString();
  var projected=JsonNode.Parse(ToledoSapODataPayload.Prepare(s.Original.DocumentoJson,new()).Json)!;projected["TipoPesagem"]="E";
  var hash=Hash(projected.ToJsonString());var fp=RegistrarPesagemToledoCasoDeUso.Fingerprint(raw);
  s=s with{Original=new(Receipt,raw,fp),FingerprintIntegracao=ToledoSapWork.Fingerprint(Receipt,fp),Eventos=s.Eventos.Select(x=>x with{RequestSha256=hash}).ToArray()};
  Assert.Equal("ENVIADO_CONFERIDO",ToledoSapDocumentoLeitura.Preparar(s).Estado);
  Assert.Equal("SAP_ENTRY_NFE_ACCESS_KEY_REQUIRED",Assert.Throws<ToledoSapPayloadException>(()=>ToledoSapODataPayload.Prepare(raw,new())).Code);
  var preview=ToledoSapDocumentoLeitura.Preparar(s with{DeliveryPartners=ToledoDeliveryPartnerTests.Proof(raw),IntegracaoId=null,FingerprintIntegracao=null,IntegracoesEncontradas=0,Eventos=[]});
  Assert.Equal("INDISPONIVEL",preview.Estado);Assert.Contains("SAP_ENTRY_NFE_ACCESS_KEY_REQUIRED",preview.Mensagem);
 }
 [Fact] public void Audit_reader_exports_only_known_metadata_and_never_arbitrary_diagnostics()
 {
  var e=RecebimentosToledoPostgreSqlReadRepository.ParseSapJsonEvento(Guid.NewGuid(),"toledo_sap_documento_preparado",Attempt,At,
   "{\"RequestSha256\":\""+new string('A',64)+"\",\"PayloadVersion\":\"TOLEDO_SAP_ODATA_V1\",\"EpochMilliseconds\":123,\"password\":\"OFFLINE_SECRET_SENTINEL\"}");
  Assert.True(e.Valido);Assert.Equal(123,e.EpochMilliseconds);Assert.DoesNotContain("OFFLINE_SECRET_SENTINEL",JsonSerializer.Serialize(e));
 }
 [Fact] public void Null_HTTP_after_a_POST_timeout_remains_valid_metadata_and_can_show_the_audited_request()
 {
  var s=Source();var original=s.Eventos[1];
  var parsed=RecebimentosToledoPostgreSqlReadRepository.ParseSapJsonEvento(original.Id,original.Evento,Attempt,original.CriadoEm,
   JsonSerializer.Serialize(new {codigo="ResultadoIndeterminado",httpStatus=(int?)null,postIniciado=true,requestSha256=original.RequestSha256,payloadVersion=original.PayloadVersion}));
  Assert.True(parsed.Valido);Assert.Null(parsed.HttpStatus);Assert.Equal("ENVIADO_CONFERIDO",ToledoSapDocumentoLeitura.Preparar(s with{Eventos=[s.Eventos[0],parsed]}).Estado);
  Assert.False(RecebimentosToledoPostgreSqlReadRepository.ParseSapJsonEvento(original.Id,original.Evento,Attempt,original.CriadoEm,"{\"httpStatus\":\"secret text\"}").Valido);
 }
 [Fact] public void Audit_events_without_an_integration_identity_cannot_attest_a_sent_document()
 {var s=Source();Assert.Null(ToledoSapDocumentoLeitura.Preparar(s with{IntegracoesEncontradas=0,IntegracaoId=null,FingerprintIntegracao=null}).Json);}
 [Fact] public void Viewer_is_readonly_and_shows_attempt_and_clear_preview_or_sent_labels()
 {
  ToledoSapJsonButtonTests.OnSta(()=>{
   var row=new RecebimentoToledoLinha(Receipt,"TOLEDO","3011","900003",DateTime.Now,At,"RECEIVED",null,"0099999903",1,"H4_NOT_FOUND");
   var s=Source();using var view=new FrmRecebimentoToledoJson(row,ToledoSapDocumentoLeitura.Preparar(s));
   Assert.Contains("hash conferido",view.Text);Assert.Equal(Receipt,view.ReciboId);Assert.NotEmpty(view.JsonExibido);
   Assert.True(ToledoSapJsonButtonTests.Children(view).OfType<TextBox>().Single().ReadOnly);
   using var preview=new FrmRecebimentoToledoJson(row,ToledoSapDocumentoLeitura.Preparar(s with{Eventos=[],IntegracaoId=null,FingerprintIntegracao=null,IntegracoesEncontradas=0}));Assert.Contains("não enviada",preview.Text);
   using var unavailable=new FrmRecebimentoToledoJson(row,ToledoSapDocumentoLeitura.Preparar(s with{EvidenciaTruncada=true}));Assert.False(ToledoSapJsonButtonTests.Children(unavailable).Single(c=>c.Name=="ToledoCopySapJson").Enabled);
  });
 }
 [Fact] public void Optional_existing_physical_exports_match_the_exact_historically_audited_requests()
 {
  var dir=Environment.GetEnvironmentVariable("FUGATES_SAP_JSON_EXISTING_EXPORTS");if(string.IsNullOrWhiteSpace(dir))return;
  foreach(var name in new[]{"EXPORT_PESAGEM_0000019690_20261030","EXPORT_PESAGEM_0000020322_20261030","EXPORT_PESAGEM_0000020321_20261030","EXPORT_PESAGEM_0019689_20261029"})
  {
   var folder=Path.Combine(dir,name);var raw=File.ReadAllText(Path.Combine(folder,"ORIGINAL_GUARDIAN_PRIVATE.json"));
   using var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"EXPORT_VERIFIED.json")));
   var expected=metadata.RootElement.GetProperty("compactSha256").GetString()!;
   var match=ToledoSapODataPayload.ReproduceAudited(raw,new("America/Sao_Paulo"),expected,ToledoSapPreparedPayload.Version);
   Assert.True(match is not null,"Existing physical audit hash did not match: "+name);Assert.Equal(expected,match!.Value.Payload.Sha256);
  }
 }
 [Fact] public async Task Optional_live_GET_readback_matches_persisted_attempts_without_any_business_POST()
 {
  var output=Environment.GetEnvironmentVariable("FUGATES_SAP_JSON_LIVE_READBACK_RESULT");if(string.IsNullOrWhiteSpace(output))return;
  using var profile=JsonDocument.Parse(File.ReadAllText(@"C:\FugaTES_DEV110\FugaTES.unified.json"));
  var key=File.ReadAllText(profile.RootElement.GetProperty("ReadKeyFile").GetString()!).Trim();
  using var client=new R165AL.Desktop.PhysicalMonitorClient(new("http://127.0.0.1:18761/"),key,R165AL.PilotPlan.Create(),FugaTES.Persistencia.Consultas.UnifiedDev.DevReadScope.PilotId,new HttpClientHandler{UseProxy=false},unified:true);
  var cases=new[]{
   ("83facfa6-b4aa-4a26-8910-713a37548959","7D52B8B6FA71E9A5846638E0D0143AAE97293890B64711F64BD46AD4F63DE30A"),
   ("701425af-4e70-40c0-a337-9716dbd7ad0a","FBF7937352CDB4D10C7178B9CB42BE8697F374083F0C4F4B37AD32B9C2FB4A2B"),
   ("fabf44e2-5ce0-442b-89ff-43b799396d59","9832EAD0448B635E7A3404501E5F9F6AAB2AC8211AD59672E653E9A568FB77ED")};
  var verified=new List<object>();
  foreach(var (receipt,hash) in cases)
  {
   var source=await client.ObterFonteJsonSapAsync(Guid.Parse(receipt),default);var view=ToledoSapDocumentoLeitura.Preparar(source);
   Assert.Equal("ENVIADO_CONFERIDO",view.Estado);Assert.Equal(hash,view.Sha256);Assert.Equal(201,view.HttpStatus);
   verified.Add(new{receipt,state=view.Estado,hash=view.Sha256,attempt=view.TentativaId,http=view.HttpStatus,profile=view.Projecao,contentExported=false});
  }
  var unattempted=await client.ObterFonteJsonSapAsync(Guid.Parse("e13f37be-ca56-4e29-b6ed-e5763b556e90"),default);
  var notSent=ToledoSapDocumentoLeitura.Preparar(unattempted);Assert.NotEqual("ENVIADO_CONFERIDO",notSent.Estado);
  Assert.True(client.Connection.Connected);
  File.WriteAllText(output,JsonSerializer.Serialize(new{result="PASS",verified,unattemptedState=notSent.Estado,readOnly=true,cluster="18/fugatesacmdev",port=5433,database="fugates_dev",businessPosts=0,databaseWrites=0,secretExported=false},new JsonSerializerOptions{WriteIndented=true}));
 }

}
