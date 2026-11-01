using System.Reflection;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Infraestrutura.MotorIntegracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
public sealed class DynamicMaterialTests
{
 const string Sap="000000000007009999",Code="7009999";
 static AcmR57NormalOptions Options()
 {var o=AutomaticFixture.Options() with{SapIdentifiersCopiedVerbatimApproved=false,SapContractCopiedVerbatimApproved=true,ApprovedMaterialMappings=[],ApprovedPartnerMappings=[],DynamicEmitterValidationEnabled=true,DynamicEmitterSignatureId=133289,DynamicEmitterValidationFromUtc=DateTimeOffset.UtcNow.AddDays(-3)};Set(o,"DynamicMaterialValidationEnabled",true);Set(o,"DynamicMaterialValidationFromUtc",(DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-1));return o;}
 static void Set(object target,string name,object? value){var p=target.GetType().GetProperty(name);Assert.NotNull(p);p.SetValue(target,value);}
 static void Stamp(VersaoContratoSapToledo v,DateTimeOffset at)=>Set(v,"RecebidoEmUtc",(DateTimeOffset?)at);
 static object Material(AcmR57NormalOptions o,Item lookup,TimeProvider? clock=null)
 {var type=typeof(AcmR57NormalPreparation).Assembly.GetType("FugaTES.Aplicacao.Servicos.Mvp.AcmGuardianMaterialResolver");Assert.NotNull(type);return Activator.CreateInstance(type,o,lookup,clock??TimeProvider.System)!;}
 static object Emitter(AcmR57NormalOptions o,Partner lookup,TimeProvider? clock=null)=>new AcmGuardianEmitterResolver(o,lookup,clock??TimeProvider.System);
 static IAcmR57RuntimeGate Gate(AcmR57NormalOptions o,object emitter,object material,TimeProvider? time=null)=>(IAcmR57RuntimeGate)Activator.CreateInstance(typeof(AcmR57NormalRuntimeGate),o,time??TimeProvider.System,emitter,material)!;
 static AcmR57Pipeline Pipeline(AutomaticFixture f,AcmR57NormalOptions o,Partner partner,Item item,TimeProvider? time=null)
 {var material=Material(o,item,time);var emitter=Emitter(o,partner,time);var prep=(IAcmR57Preparacao)Activator.CreateInstance(typeof(AcmR57NormalPreparation),o,emitter,material)!;return new(f.Versions,prep,Gate(o,emitter,material,time),f.Soap,f.Rest,f.Journal,f.H4);}
 [Fact]public async Task New_unlisted_material_can_complete_only_after_exact_item_and_emitter_GET_proofs()
 {var f=new AutomaticFixture();var(v,c)=f.Add("2919",s=>s with{Material=Sap});Stamp(v,DateTimeOffset.UtcNow);var o=Options();var partner=new Partner();var item=new Item();var r=await Pipeline(f,o,partner,item).ExecutarAsync(c,default);Assert.Equal(StatusFluxoMvp.AguardandoCallback,r.Status);Assert.Equal(1,item.Calls);Assert.Equal(Code,item.Codigo);Assert.Equal(1,partner.Calls);Assert.Equal(Code,f.Soap.Sent!.CodigoItem);Assert.Equal(Code,f.Rest.Sent!.Dados.CodigoItem);Assert.Equal(Sap,v.Snapshot.Material);Assert.Empty(o.ApprovedMaterialMappings);Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Creates);}
 [Theory][InlineData("ACM_MATERIAL_NOT_FOUND")][InlineData("ACM_MATERIAL_INACTIVE")][InlineData("ACM_MATERIAL_LOOKUP_UNVERIFIED")][InlineData("ACM_MATERIAL_AUTHENTICATION_FAILED")][InlineData("ACM_MATERIAL_RESPONSE_INVALID")]
 public async Task Failed_material_proof_stops_before_emitter_or_contract_lookup_and_all_business_writes(string code)
 {var f=new AutomaticFixture();var(v,c)=f.Add("2920",s=>s with{Material=Sap});Stamp(v,DateTimeOffset.UtcNow);var item=new Item{Reason=code};var partner=new Partner();var r=await Pipeline(f,Options(),partner,item).ExecutarAsync(c,default);Assert.Equal(code,r.Erro);Assert.Equal(1,item.Calls);Assert.Equal(0,partner.Calls);Assert.Equal(0,f.Soap.Lookups);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);Assert.Empty(f.H4.Items);}
 [Fact]public async Task Existing_explicit_alias_is_preserved_and_verified_in_Guardian()
 {var f=new AutomaticFixture();var(v,c)=f.Add("2921",s=>s with{Material=Sap});Stamp(v,DateTimeOffset.UtcNow);var o=Options() with{ApprovedMaterialMappings=[new(){Centro="3011",Item="10",SapMaterial=Sap,GuardianCode="ALIAS-9",EvidenceReference="APPROVED_EXISTING_ALIAS"}]};var item=new Item();var r=await Pipeline(f,o,new Partner(),item).ExecutarAsync(c,default);Assert.Equal(StatusFluxoMvp.AguardandoCallback,r.Status);Assert.Equal("ALIAS-9",item.Codigo);Assert.Equal("ALIAS-9",f.Soap.Sent!.CodigoItem);Assert.Equal(Sap,v.Snapshot.Material);}
 [Fact]public async Task Conflicting_existing_aliases_block_without_network_or_fallback()
 {var f=new AutomaticFixture();var(v,c)=f.Add("2922",s=>s with{Material=Sap});Stamp(v,DateTimeOffset.UtcNow);var mapping=new AcmR57ApprovedScopeMaterialMapping{Centro="3011",Item="10",SapMaterial=Sap,GuardianCode=Code,EvidenceReference="APPROVED"};var o=Options() with{ApprovedMaterialMappings=[mapping,mapping with{GuardianCode="OTHER"}]};var item=new Item();var r=await Pipeline(f,o,new Partner(),item).ExecutarAsync(c,default);Assert.Equal("ACM_MATERIAL_MAPPING_AMBIGUOUS",r.Erro);Assert.Equal(0,item.Calls);Assert.Equal(0,f.Soap.Creates);}
 [Theory][InlineData("old")][InlineData("disabled")]
 public async Task Disabled_or_pre_cutoff_materials_keep_the_original_mapping_guard(string mode)
 {var f=new AutomaticFixture();var(v,c)=f.Add("2923",s=>s with{Material=Sap});Stamp(v,mode=="old"?DateTimeOffset.UtcNow.AddDays(-2):DateTimeOffset.UtcNow);var o=Options();if(mode=="disabled")Set(o,"DynamicMaterialValidationEnabled",false);var item=new Item();var r=await Pipeline(f,o,new Partner(),item).ExecutarAsync(c,default);Assert.Equal("R170_MATERIAL_MAPPING_REQUIRED",r.Erro);Assert.Equal(0,item.Calls);Assert.Equal(0,f.Soap.Creates);}
 [Fact]public async Task Execution_disabled_never_queries_item_or_partner()
 {var f=new AutomaticFixture();var(v,c)=f.Add("2924",s=>s with{Material=Sap});Stamp(v,DateTimeOffset.UtcNow);var item=new Item();var partner=new Partner();var r=await Pipeline(f,Options() with{ExecutionAuthorized=false},partner,item).ExecutarAsync(c,default);Assert.Equal("R165U_EXECUTION_DISABLED",r.Erro);Assert.Equal(0,item.Calls);Assert.Equal(0,partner.Calls);}
 [Theory][InlineData("scope","ACM_MATERIAL_SCOPE_MISMATCH")][InlineData("code","ACM_MATERIAL_IDENTITY_MISMATCH")][InlineData("sequence","ACM_MATERIAL_IDENTITY_MISMATCH")]
 public async Task Resolver_rechecks_identity_even_when_a_lookup_port_claims_success(string fault,string expected)
 {var f=new AutomaticFixture();var(v,c)=f.Add("2925",s=>s with{Material=Sap});Stamp(v,DateTimeOffset.UtcNow);var item=new Item{Signature=fault=="scope"?999:133289,Returned=fault=="code"?"OTHER":null,Sequence=fault=="sequence"?0:101};var r=await Pipeline(f,Options(),new Partner(),item).ExecutarAsync(c,default);Assert.Equal(expected,r.Erro);Assert.Equal(0,f.Soap.Creates);}
 [Fact]public async Task Material_success_does_not_bypass_failed_emitter_proof()
 {var f=new AutomaticFixture();var(v,c)=f.Add("2926",s=>s with{Material=Sap});Stamp(v,DateTimeOffset.UtcNow);var r=await Pipeline(f,Options(),new Partner{Confirmed=false},new Item()).ExecutarAsync(c,default);Assert.Equal("R171_EMITTER_NOT_FOUND",r.Erro);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);}
 [Fact]public async Task Material_proof_is_bound_to_integration_fingerprint_and_source_material()
 {var f=new AutomaticFixture();var(v,c)=f.Add("2927",s=>s with{Material=Sap});Stamp(v,DateTimeOffset.UtcNow);dynamic material=Material(Options(),new Item());await material.ValidarAsync(v,CancellationToken.None);Assert.Equal(Code,(string)material.Exigir(v));foreach(var other in new[]{v with{IntegracaoId=Guid.NewGuid()},v with{FingerprintSha256=new string('B',64)},v with{Snapshot=v.Snapshot with{Material="9999999"}}})await Assert.ThrowsAnyAsync<Exception>(()=>Task.Run(()=>(string)material.Exigir(other)));}
 [Fact]public async Task Expired_material_proof_blocks_a_later_write_guard()
 {var f=new AutomaticFixture();var(v,c)=f.Add("2928",s=>s with{Material=Sap});Stamp(v,DateTimeOffset.UtcNow);var o=Options();var clock=new Clock();dynamic material=Material(o,new Item(),clock);dynamic emitter=Emitter(o,new Partner(),clock);await material.ValidarAsync(v,CancellationToken.None);await emitter.ValidarAsync(v,CancellationToken.None);var gate=Gate(o,emitter,material,clock);Assert.Null(gate.Bloqueio(v));clock.Now=clock.Now.AddMinutes(3);Assert.Equal("ACM_MATERIAL_PROOF_EXPIRED",gate.Bloqueio(v));}
 [Fact]public async Task Host_gate_and_preparation_share_both_proofs_only_in_the_same_DI_scope()
 {var f=new AutomaticFixture();var(v,c)=f.Add("2929",s=>s with{Material=Sap});Stamp(v,DateTimeOffset.UtcNow);var o=Options();var item=new Item();var partner=new Partner();var services=new ServiceCollection();services.AddSingleton(o);services.AddSingleton<IGuardianMaterialRead>(item);services.AddSingleton<IGuardianEmitterRead>(partner);services.AddSingleton(TimeProvider.System);services.AddSingleton<IMotorIntegracaoRepository>(new RepositorioMotorIntegracaoFake());var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["AcmR57:Enabled"]="true",["AcmR57:Normal:Enabled"]="true",["AcmR57:Normal:DynamicEmitterValidationEnabled"]="true",["AcmR57:Normal:DynamicMaterialValidationEnabled"]="true"}).Build();FugaTES.HostIntegracao.MvpOrchestrationComposition.AddMvpOrchestration(services,config);using var root=services.BuildServiceProvider();using(var scope=root.CreateScope()){var gate=scope.ServiceProvider.GetRequiredService<IAcmR57RuntimeGate>();var prep=scope.ServiceProvider.GetRequiredService<IAcmR57Preparacao>();Assert.Equal("ACM_MATERIAL_VALIDATION_REQUIRED",gate.Bloqueio(v));var method=prep.GetType().GetMethod("ValidarMaterialAsync");Assert.NotNull(method);await (Task)method.Invoke(prep,[v,CancellationToken.None])!;Assert.Equal("R171_EMITTER_VALIDATION_REQUIRED",gate.Bloqueio(v));await ((IAcmR57DynamicEmitterPreparation)prep).ValidarEmissorAsync(v,default);Assert.Null(gate.Bloqueio(v));}using var other=root.CreateScope();Assert.Equal("ACM_MATERIAL_VALIDATION_REQUIRED",other.ServiceProvider.GetRequiredService<IAcmR57RuntimeGate>().Bloqueio(v));Assert.Equal(1,item.Calls);Assert.Equal(1,partner.Calls);}

 [Theory][InlineData("MAT-TEST.01")][InlineData("000ABC")]
 public async Task Alphanumeric_material_keeps_its_exact_code_including_significant_zeros(string code)
 {var f=new AutomaticFixture();var(v,c)=f.Add("2930",s=>s with{Material=code});Stamp(v,DateTimeOffset.UtcNow);var item=new Item();var r=await Pipeline(f,Options(),new Partner(),item).ExecutarAsync(c,default);Assert.Equal(StatusFluxoMvp.AguardandoCallback,r.Status);Assert.Equal(code,item.Codigo);Assert.Equal(code,f.Soap.Sent!.CodigoItem);}
 [Theory][InlineData("missing_cutoff","ACM_MATERIAL_ADMISSION_IDENTITY_REQUIRED")][InlineData("missing_received","R171_EMITTER_ADMISSION_IDENTITY_REQUIRED")][InlineData("bad_mapping","ACM_MATERIAL_MAPPING_UNPROVEN")]
 public async Task Missing_admission_or_unproven_alias_cannot_fall_back_to_a_raw_material_code(string fault,string expected)
 {var f=new AutomaticFixture();var(v,c)=f.Add("2931",s=>s with{Material=Sap});Stamp(v,DateTimeOffset.UtcNow);var o=Options();if(fault=="missing_cutoff")Set(o,"DynamicMaterialValidationFromUtc",null);if(fault=="missing_received")Set(v,"RecebidoEmUtc",null);if(fault=="bad_mapping")o=o with{ApprovedMaterialMappings=[new(){Centro="3011",Item="10",SapMaterial=Sap,GuardianCode="",EvidenceReference=""}]};var item=new Item();var r=await Pipeline(f,o,new Partner(),item).ExecutarAsync(c,default);Assert.Equal(expected,r.Erro);Assert.Equal(0,item.Calls);Assert.Equal(0,f.Soap.Creates);}
 [Fact]public void Command_composes_the_material_resolver_from_approved_options_before_conditional_registration()
 {
  var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
   ["HostIntegracao:PersistenceMode"]="PostgreSql",["HostIntegracao:ExternalMode"]="Guardian",["HostIntegracao:PollInterval"]="00:00:05",["HostIntegracao:ShutdownGracePeriod"]="00:00:10",
   ["ConnectionStrings:FugaTES"]="Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_acm_hml_app;SearchPath=fugates",
   ["MotorIntegracao:LeaseDuration"]="00:01:00",["MotorIntegracao:LeaseRenewInterval"]="00:00:10",["MotorIntegracao:RetryBackoff"]="00:01:00",["TimeoutProcessamentoIntegracao:Timeout"]="00:00:30",
   ["Guardian:BaseUrl"]="https://offline.invalid/",["Guardian:Timeout"]="00:00:05",["Guardian:Email"]="OFFLINE",["Guardian:Password"]="OFFLINE_ONLY",["Guardian:Signature"]="133289",["Sap:Enabled"]="false",
   ["AcmR57:Enabled"]="true",["AcmR57:Normal:Enabled"]="true",["AcmR57:Normal:DynamicEmitterValidationEnabled"]="false",["AcmR57:Normal:DynamicMaterialValidationEnabled"]="false",["FUGATES_INTEGRATION_WORKER_ENABLED"]="false"}).Build();
  var o=Options();var services=new ServiceCollection();services.AddSingleton<IConfiguration>(config);
  FugaTES.Callback.Http.ManualRetry.AcmManualRetryCommandHost.ConfigureApprovedRuntime(services,config,o);
  using var root=services.BuildServiceProvider();using var scope=root.CreateScope();var prep=scope.ServiceProvider.GetRequiredService<IAcmR57Preparacao>();
  var resolverType=typeof(AcmR57NormalPreparation).Assembly.GetType("FugaTES.Aplicacao.Servicos.Mvp.AcmGuardianMaterialResolver");Assert.NotNull(resolverType);
  var field=prep.GetType().GetFields(BindingFlags.Instance|BindingFlags.NonPublic).Single(f=>f.FieldType==resolverType);
  Assert.NotNull(field.GetValue(prep));Assert.Equal("true",config["AcmR57:Normal:DynamicMaterialValidationEnabled"]);
 }

 [Fact]public async Task Existing_exact_contract_alias_is_preserved_instead_of_numeric_fallback()
 {var f=new AutomaticFixture();var(v,c)=f.Add("2932",s=>s with{Material=Sap});Stamp(v,DateTimeOffset.UtcNow);var o=Options() with{ApprovedMaterialMapping=new(){NumeroContrato=v.NumeroContrato,Item="10",Centro="3011",SapMaterial=Sap,GuardianCode="ITEM-EXACT",EvidenceReference="APPROVED_CONTRACT_ALIAS"}};var item=new Item();var r=await Pipeline(f,o,new Partner(),item).ExecutarAsync(c,default);Assert.Equal(StatusFluxoMvp.AguardandoCallback,r.Status);Assert.Equal("ITEM-EXACT",item.Codigo);Assert.Equal("ITEM-EXACT",f.Soap.Sent!.CodigoItem);}
 [Theory][InlineData("conflict","ACM_MATERIAL_MAPPING_AMBIGUOUS")][InlineData("unproven","ACM_MATERIAL_MAPPING_UNPROVEN")]
 public async Task Applicable_contract_alias_cannot_be_silently_ignored(string fault,string expected)
 {var f=new AutomaticFixture();var(v,c)=f.Add("2933",s=>s with{Material=Sap});Stamp(v,DateTimeOffset.UtcNow);var o=Options() with{ApprovedMaterialMapping=new(){NumeroContrato=v.NumeroContrato,Item="10",Centro="3011",SapMaterial=Sap,GuardianCode="ITEM-EXACT",EvidenceReference=fault=="unproven"?"":"APPROVED_CONTRACT_ALIAS"},ApprovedMaterialMappings=fault=="conflict"?[new(){Centro="3011",Item="10",SapMaterial=Sap,GuardianCode="ITEM-OTHER",EvidenceReference="APPROVED_SCOPE_ALIAS"}]:[]};var item=new Item();var r=await Pipeline(f,o,new Partner(),item).ExecutarAsync(c,default);Assert.Equal(expected,r.Erro);Assert.Equal(0,item.Calls);Assert.Equal(0,f.Soap.Creates);}
 sealed class Partner:IGuardianEmitterRead
 {public int Calls;public bool Confirmed=true;public Task<GuardianEmitterReadResult> ConsultarAsync(string code,CancellationToken ct){Calls++;return Task.FromResult(new GuardianEmitterReadResult(Confirmed,Confirmed?code:"",Confirmed?61:0,133289,Confirmed?200:404,Confirmed?"R171_EMITTER_CONFIRMED":"R171_EMITTER_NOT_FOUND"));}}
 sealed class Item:IGuardianMaterialRead
 {public int Calls,Signature=133289;public long Sequence=101;public string? Codigo,Returned,Reason;public Task<GuardianMaterialReadResult> ConsultarAsync(string code,CancellationToken ct){Calls++;Codigo=code;return Task.FromResult(new GuardianMaterialReadResult(Reason is null,Returned??code,Sequence,Signature,Reason is null?200:404,Reason??"ACM_MATERIAL_CONFIRMED"));}}
 sealed class Clock:TimeProvider{public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
}
