using System.Text.Json;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.Mvp;
using Xunit;

public sealed class PendingLatestRuntimeScopeTests
{
 static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
 static (AutomaticFixture F,VersaoContratoSapToledo V,FugaTES.Aplicacao.Modelos.MotorIntegracao.ClaimIntegracaoMotor C) Case()
 {
  var f=new AutomaticFixture();var(v,c)=f.Add("0099999975",s=>s with{Item=null,TipoContrato="AC3S",TipoPesagem="S",Material="7000002",IdParceiro="0020000169",QuantidadeSaldoDevolucao=3000m});
  v=v with{Versao=2,RecebidoEmUtc=Now.AddDays(-2)};f.Versions.Items[v.IntegracaoId]=v;return(f,v,c);
 }
 static AcmR57NormalOptions Options(VersaoContratoSapToledo v,Action<Dictionary<string,object?>>? change=null)
 {
  var o=AutomaticFixture.Options() with{DynamicEmitterValidationEnabled=true,DynamicEmitterSignatureId=133289,DynamicEmitterValidationFromUtc=Now.AddDays(-1),SapIdentifiersCopiedVerbatimApproved=false,SapContractCopiedVerbatimApproved=true,
   ApprovedMaterialMappings=[new(){Centro="3011",Item=null,SapMaterial="7000002",GuardianCode="7000002",EvidenceReference="OFFLINE_MATERIAL_APPROVED"}]};
  var scope=new Dictionary<string,object?>{["IntegrationId"]=v.IntegracaoId,["Contract"]=v.NumeroContrato,["Item"]=v.Snapshot.Item,["Version"]=v.Versao,["Fingerprint"]=v.FingerprintSha256,["ReceivedUtc"]=v.RecebidoEmUtc,["AuthorizationReference"]="OFFLINE_PENDING_APPROVAL",["NotBeforeUtc"]=Now.AddMinutes(-1),["UntilUtc"]=Now.AddMinutes(5)};
  change?.Invoke(scope);var prop=typeof(AcmR57NormalOptions).GetProperty("ManualRecoveryScope");Assert.NotNull(prop);
  prop.SetValue(o,JsonSerializer.Deserialize(JsonSerializer.Serialize(scope),prop.PropertyType));return o;
 }
 static AcmR57Pipeline Pipeline(AutomaticFixture f,AcmR57NormalOptions o,Lookup lookup)
 {var resolver=new AcmGuardianEmitterResolver(o,lookup,TimeProvider.System);return new(f.Versions,new AcmR57NormalPreparation(o,resolver),new AcmR57NormalRuntimeGate(o,TimeProvider.System,resolver),f.Soap,f.Rest,f.Journal,f.H4);}
 [Fact] public async Task Approved_latest_sale_nullItem_uses_existing_pipeline_and_fresh_emitter_before_one_SOAP_and_REST()
 {
  var(f,v,c)=Case();var lookup=new Lookup();var pipeline=Pipeline(f,Options(v),lookup);
  var result=await pipeline.ExecutarAsync(c,default);Assert.Equal(StatusFluxoMvp.AguardandoCallback,result.Status);
  Assert.Equal(1,lookup.Calls);Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Creates);Assert.Equal("20000169",f.Soap.Sent!.CodigoEmissor);
  Assert.Equal(3000m,f.Versions.Items[v.IntegracaoId].Snapshot.QuantidadeSaldoDevolucao);Assert.Null(f.Versions.Items[v.IntegracaoId].Snapshot.Item);
  await pipeline.ExecutarAsync(c,default);Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Creates);
 }
 [Theory][InlineData("IntegrationId")][InlineData("Version")][InlineData("Fingerprint")][InlineData("ReceivedUtc")][InlineData("Contract")][InlineData("Item")]
 public async Task Manual_scope_cannot_be_borrowed_by_other_identity_version_or_item(string field)
 {
  var(f,v,c)=Case();var lookup=new Lookup();var o=Options(v,s=>s[field]=field switch{"IntegrationId"=>Guid.NewGuid(),"Version"=>1L,"ReceivedUtc"=>Now,"Fingerprint"=>new string('B',64),"Item"=>"10",_=>"OTHER"});
  var result=await Pipeline(f,o,lookup).ExecutarAsync(c,default);Assert.NotEqual(StatusFluxoMvp.AguardandoCallback,result.Status);Assert.Equal(0,lookup.Calls);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);
 }
 [Fact] public async Task Expired_scope_never_even_calls_Guardian_emitter()
 {var(f,v,c)=Case();var lookup=new Lookup();var result=await Pipeline(f,Options(v,s=>s["UntilUtc"]=Now.AddSeconds(-1)),lookup).ExecutarAsync(c,default);Assert.Equal(0,lookup.Calls);Assert.Equal(0,f.Soap.Creates);Assert.NotEqual(StatusFluxoMvp.AguardandoCallback,result.Status);}
 [Fact] public async Task Automatic_mode_cannot_use_the_manual_historical_exception()
 {var(f,v,c)=Case();var lookup=new Lookup();var o=Options(v) with{AcmAutomatico=new(){Enabled=true}};await Pipeline(f,o,lookup).ExecutarAsync(c,default);Assert.Equal(0,lookup.Calls);Assert.Equal(0,f.Soap.Creates);}
 [Fact] public async Task Predecessor_guard_still_blocks_even_under_nominal_manual_context()
 {var(f,v,c)=Case();f.Versions.Predecessor=true;var result=await Pipeline(f,Options(v),new()).ExecutarAsync(c,default);Assert.Equal("R165U_PREDECESSOR_PENDING",result.Erro);Assert.Equal(0,f.Soap.Lookups);Assert.Equal(0,f.Soap.Creates);}
 [Fact] public async Task Existing_remote_contract_is_never_silently_adopted_by_pending_recovery()
 {var(f,v,c)=Case();f.Soap.Lookup=EstadoConsultaContratoGuardian.Existente;var result=await Pipeline(f,Options(v),new()).ExecutarAsync(c,default);Assert.Equal("R165AG_EXISTING_CONTRACT_RECONCILIATION_REQUIRED",result.Erro);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);}
 [Fact] public async Task Failed_emitter_proof_blocks_lookup_and_business_writes()
 {var(f,v,c)=Case();var result=await Pipeline(f,Options(v),new(){Active=false}).ExecutarAsync(c,default);Assert.Equal("R171_EMITTER_INACTIVE",result.Erro);Assert.Equal(0,f.Soap.Lookups);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);}
 [Theory][InlineData(true)][InlineData(false)]
 public async Task SOAP_only_confirms_contract_durably_but_never_calls_REST_even_if_callback_is_enabled(bool callback)
 {
  var(f,v,c)=Case();var o=Options(v,s=>s["SoapOnly"]=true) with{CallbackApproved=callback};var pipeline=Pipeline(f,o,new());
  var result=await pipeline.ExecutarAsync(c,default);
  Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna,result.Status);Assert.Equal("R165U_PROTOCOL_BLOCKED",result.Etapa);Assert.Equal("ACM_MANUAL_SOAP_ONLY_PROTOCOL_NOT_AUTHORIZED",result.Erro);
  Assert.Equal(1,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);Assert.EndsWith("::900",result.ResultadoFuncional);
  var persisted=await f.Journal.ObterAsync(c,default);Assert.Equal(result,persisted);
  var link=await f.H4.ObterPorIntegracaoAsync(v.IntegracaoId,default);Assert.Equal(v.NumeroContrato,link!.CodigoContratoConfirmado);Assert.Null(link.ProtocoloIdGuardian);
  await pipeline.ExecutarAsync(c,default);Assert.Equal(1,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);
 }
 [Fact] public async Task SOAP_only_unknown_result_does_not_retry_SOAP_or_advance_REST()
 {
  var(f,v,c)=Case();f.Soap.Failure=new TimeoutException("SYNTHETIC_UNKNOWN");var pipeline=Pipeline(f,Options(v,s=>s["SoapOnly"]=true),new());
  var result=await pipeline.ExecutarAsync(c,default);Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,result.Status);
  await pipeline.ExecutarAsync(c,default);Assert.Equal(1,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);
 }

 sealed class Lookup:IGuardianEmitterRead
 {public int Calls;public bool Active=true;public Task<GuardianEmitterReadResult> ConsultarAsync(string code,CancellationToken ct){Calls++;return Task.FromResult(Active?new GuardianEmitterReadResult(true,"20000169",42,133289,200,"R171_EMITTER_CONFIRMED"):new(false,"",0,133289,200,"R171_EMITTER_INACTIVE"));}}
}
