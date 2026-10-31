using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Net;
using System.Text;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Infraestrutura.Recebimento;
using FugaTES.Integracao.Guardian;

// All fixture values are SYNTHETIC. No live 1106 payload or operational persistence is attested here.
public class R57PipelineTests
{
 [Fact] public async Task New_1106_journals_before_each_dispatch_and_confirms_correlations()
 {
  var f=new Fixture();var result=await f.Pipeline().ExecutarAsync(f.Claim,default);
  Assert.Equal(StatusFluxoMvp.AguardandoCallback,result.Status);Assert.Equal("9471",result.ProtocoloGuardian);Assert.Null(result.NumeroTicketGuardian);
  Assert.Equal(new[]{"reserve_reference","lookup","R57_SOAP_ARMED","soap","contract_identity","R57_SOAP_CONFIRMED","R57_REST_ARMED","rest","protocol_identity","R57_CONFIRMED"},f.Events);
  Assert.Equal("1106",f.Link.Current!.CodigoContratoConfirmado);Assert.Equal(9471,f.Link.Current.ProtocoloIdGuardian);
  Assert.Contains("581",f.Journal.Current!.ResultadoFuncional);Assert.Equal(12.345m,f.Soap.Last!.ValorContrato);Assert.Equal("7000000",f.Soap.Last.CodigoItem);
  Assert.Equal("2.5",f.Rest.Last!.Dados.Documentos.Single().PesoOrigem);Assert.Equal("DOC-OFFLINE-1106",f.Rest.Last.Dados.Documentos.Single().Descricao);
  var correlated=await f.Correlations.ObterPorProtocoloAsync("9471",default);Assert.NotNull(correlated);Assert.Equal("0000001106",correlated.Entrada.ContratoAcmSap);Assert.Equal("OC1106",correlated.DocumentoSap);
 }
 [Fact] public async Task Existing_1031_never_creates_and_reconciles_first()
 {
  var f=new Fixture("0000001031","1031");f.Soap.Existing=true;
  var r=await f.Pipeline().ExecutarAsync(f.Claim,default);Assert.Equal(StatusFluxoMvp.AguardandoCallback,r.Status);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Soap.Updates);Assert.Equal(1,f.Rest.Posts);
 }
 [Fact] public async Task Known_manual_1031_is_not_created_even_if_absence_is_reported()
 {
  var f=new Fixture("0000001031","1031");var r=await f.Pipeline().ExecutarAsync(f.Claim,default);
  Assert.NotEqual(StatusFluxoMvp.AguardandoCallback,r.Status);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Posts);
 }
 [Fact] public async Task Existing_contract_difference_blocks_protocol_and_never_silently_updates()
 {
  var f=new Fixture();f.Soap.Existing=true;f.Soap.Mismatch=true;var r=await f.Pipeline().ExecutarAsync(f.Claim,default);
  Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna,r.Status);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Soap.Updates);Assert.Equal(0,f.Rest.Posts);
 }
 [Fact] public async Task Soap_nonzero_error_stops_sequence_as_known_failure()
 {var f=new Fixture();f.Soap.Error=new GuardianHttpException("synthetic rejection"){BusinessErrorCode=7};var r=await f.Pipeline().ExecutarAsync(f.Claim,default);Assert.Equal(StatusFluxoMvp.FalhaNaoReprocessavel,r.Status);Assert.Equal(0,f.Rest.Posts);}
 [Fact] public async Task Soap_timeout_is_indeterminate_and_replay_never_retries()
 {var f=new Fixture();f.Soap.Error=new TimeoutException();var p=f.Pipeline();var r=await p.ExecutarAsync(f.Claim,default);Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,r.Status);await f.Pipeline().ExecutarAsync(f.Claim,default);Assert.Equal(1,f.Soap.Creates);Assert.Equal(0,f.Rest.Posts);}
 [Fact] public async Task Soap_wrong_identity_blocks_rest_as_indeterminate()
 {var f=new Fixture();f.Soap.WrongCode=true;var r=await f.Pipeline().ExecutarAsync(f.Claim,default);Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,r.Status);Assert.Equal(0,f.Rest.Posts);}
 [Fact] public async Task Rest_timeout_is_indeterminate_and_replay_never_posts_again()
 {var f=new Fixture();f.Rest.Error=new TimeoutException();var r=await f.Pipeline().ExecutarAsync(f.Claim,default);Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,r.Status);await f.Pipeline().ExecutarAsync(f.Claim,default);Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Posts);}
 [Theory][InlineData("id")][InlineData("contract")][InlineData("item")][InlineData("emitter")][InlineData("reference")]
 public async Task Rest_inconsistent_response_is_indeterminate(string field)
 {var f=new Fixture();f.Rest.WrongField=field;var r=await f.Pipeline().ExecutarAsync(f.Claim,default);Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,r.Status);Assert.Null(f.Link.Current!.ProtocoloIdGuardian);}
 [Fact] public async Task Replay_after_restart_returns_journal_without_new_dispatch()
 {var f=new Fixture();await f.Pipeline().ExecutarAsync(f.Claim,default);var r=await f.Pipeline().ExecutarAsync(f.Claim with{FencingToken=2},default);Assert.Equal(StatusFluxoMvp.AguardandoCallback,r.Status);Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Posts);}
 [Fact] public async Task Missing_atomic_reservation_blocks_write()
 {var f=new Fixture();f.Journal.Refuse=true;var r=await f.Pipeline().ExecutarAsync(f.Claim,default);Assert.NotEqual(StatusFluxoMvp.AguardandoCallback,r.Status);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Posts);}
 [Fact] public async Task Duplicate_reference_blocks_before_soap_lookup()
 {var f=new Fixture();f.Link.Collision=true;var r=await f.Pipeline().ExecutarAsync(f.Claim,default);Assert.NotEqual(StatusFluxoMvp.AguardandoCallback,r.Status);Assert.Equal(0,f.Soap.Lookups);Assert.Equal(0,f.Rest.Posts);}
 [Fact] public async Task Default_runtime_gate_blocks_all_network()
 {var f=new Fixture();var p=new AcmR57Pipeline(f.Source,f.Prep,new AcmR57RuntimeDisabled(),f.Soap,f.Rest,f.Journal,f.Link);var r=await p.ExecutarAsync(f.Claim,default);Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna,r.Status);Assert.Equal(0,f.Soap.Lookups);Assert.Equal(0,f.Rest.Posts);Assert.Equal("R57_PRE_SEND_BLOCKED",f.Journal.Current!.Etapa);}
 [Fact] public async Task Lost_journal_after_fencing_change_never_retries()
 {var f=new Fixture();var r=await f.Pipeline().ExecutarAsync(f.Claim with{FencingToken=2},default);Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,r.Status);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Posts);}
 [Fact] public async Task Overlapping_calls_only_one_dispatch_per_stage()
 {var f=new Fixture();f.Soap.HoldBothLookups=true;await Task.WhenAll(f.Pipeline().ExecutarAsync(f.Claim,default),f.Pipeline().ExecutarAsync(f.Claim,default));Assert.Equal(2,f.Soap.Lookups);Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Posts);}
 [Fact] public async Task Journal_failure_after_soap_prevents_rest_and_replay()
 {var f=new Fixture();f.Journal.FailOnConfirm=true;var r=await f.Pipeline().ExecutarAsync(f.Claim,default);Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,r.Status);await f.Pipeline().ExecutarAsync(f.Claim,default);Assert.Equal(1,f.Soap.Creates);Assert.Equal(0,f.Rest.Posts);}
 [Theory][InlineData("emitter")][InlineData("item")][InlineData("document")][InlineData("weightsource")][InlineData("additionalsource")][InlineData("callback")][InlineData("unit")][InlineData("itemSap")]
 public async Task Missing_source_data_blocks_before_any_network(string field)
 {
  var f=new Fixture();f.Prep.Value=field switch{ "emitter"=>f.Prep.Value with{CodigoEmissorGuardian=""},"item"=>f.Prep.Value with{CodigoItemGuardian=""},"document"=>f.Prep.Value with{DocumentoOperacao=""},"weightsource"=>f.Prep.Value with{FontePeso=""},"callback"=>f.Prep.Value with{CallbackHomologado=false},_=>f.Prep.Value};
  // Field 1000 now comes from the SAP snapshot, not the legacy free-form source label.
  if(field=="additionalsource")f.Source.Version=f.Source.Version with{Snapshot=f.Source.Version.Snapshot with{TipoContrato=null}};
  if(field=="unit")f.Source.Version=f.Source.Version with{Snapshot=f.Source.Version.Snapshot with{UnidCont=null}};
  if(field=="itemSap")f.Source.Version=f.Source.Version with{Snapshot=f.Source.Version.Snapshot with{Item=null}};
  var r=await f.Pipeline().ExecutarAsync(f.Claim,default);Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna,r.Status);Assert.Equal(0,f.Soap.Lookups);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Posts);
 }
 [Fact] public void Minimal_rest_whitelist_omits_all_unapproved_fields()
 {
  var f=new Fixture();var p=AcmR57Mapping.Criar(f.Source.Version,f.Prep.Value);var json=JsonSerializer.Serialize(GuardianSet26HttpClient.CriarPayloadR57(p.Protocolo),new JsonSerializerOptions(JsonSerializerDefaults.Web));using var doc=JsonDocument.Parse(json);var data=doc.RootElement.GetProperty("dadosProtocoloPreCadastro");
  // R104 approved composer adds mandatory carrier/flow; R69 fields come from the bound H4 snapshot.
  Assert.Equal(new[]{"id","codigoItem","codigoEmissor","codigoContrato","documentos","numeroReferencia","camposAdicionais","codigoTransportadora","codigoFluxo"}.Order(StringComparer.Ordinal),data.EnumerateObject().Select(x=>x.Name).Order(StringComparer.Ordinal));
  Assert.Equal(0,data.GetProperty("id").GetInt32());Assert.Equal(new[]{1000,1002,1003},data.GetProperty("camposAdicionais").EnumerateArray().Select(x=>x.GetProperty("numero").GetInt32()));
  Assert.Equal(new[]{"AC3P","3011","10"},data.GetProperty("camposAdicionais").EnumerateArray().Select(x=>x.GetProperty("valor").GetString()));
  Assert.Equal("SYNTH-CARRIER",data.GetProperty("codigoTransportadora").GetString());Assert.Equal("FLX_RECEBIMENTO",data.GetProperty("codigoFluxo").GetString());Assert.DoesNotContain("10000.000",json);Assert.DoesNotContain("placa",json);Assert.DoesNotContain("idAgenda",json);Assert.DoesNotContain("versaoContrato",json);
 }
 [Fact] public void Fingerprint_distinguishes_sap_item_and_preserves_decimal_identity()
 {var f=new Fixture();var c=new ContratoSapToledoCanonicalizer();var v=f.Source.Version;var other=c.Canonicalizar(v.Snapshot with{Item="000030"});Assert.NotEqual(v.FingerprintSha256,other.FingerprintSha256);Assert.Equal(12.345m,other.Snapshot.QuantidadeContrato);Assert.Equal(1,v.Versao);}
 [Fact] public async Task Existing_h4_dispatch_uses_r57_pipeline_when_explicitly_composed()
 {
  var f=new Fixture();var services=Services(f);
  FugaTES.HostIntegracao.MvpOrchestrationComposition.AddMvpOrchestration(services,new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"AcmR57:Enabled","true"}}).Build());
  using var provider=services.BuildServiceProvider();
  var result=await provider.GetRequiredService<FugaTES.HostIntegracao.ProcessadorFluxosMvp>().ProcessarAsync(f.Claim,default);
  Assert.Null(result.Falha);Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Posts);Assert.Equal("R57_CONFIRMED",f.Journal.Current!.Etapa);
  Assert.DoesNotContain(services,d=>d.ServiceType==typeof(Microsoft.Extensions.Hosting.IHostedService));
 }
 [Fact] public async Task R57_di_defaults_to_disabled_runtime_without_reusing_functional_approval()
 {
  var f=new Fixture();var services=Services(f);var descriptor=services.Single(d=>d.ServiceType==typeof(IAcmR57RuntimeGate));services.Remove(descriptor);
  FugaTES.HostIntegracao.MvpOrchestrationComposition.AddMvpOrchestration(services,new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"AcmR57:Enabled","true"}}).Build());
  using var provider=services.BuildServiceProvider();var result=await provider.GetRequiredService<FugaTES.HostIntegracao.ProcessadorFluxosMvp>().ProcessarAsync(f.Claim,default);
  Assert.NotNull(result.Falha);Assert.Equal(0,f.Soap.Lookups);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Posts);
 }
 [Fact] public async Task Complete_use_case_resumes_after_missing_document_is_resolved_without_duplicates()
 {
  var f=new Fixture();var valid=f.Prep.Value;f.Prep.Value=valid with{DocumentoOperacao=""};
  var services=Services(f);FugaTES.HostIntegracao.MvpOrchestrationComposition.AddMvpOrchestration(services,new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"AcmR57:Enabled","true"}}).Build());
  using var provider=services.BuildServiceProvider();var useCase=provider.GetRequiredService<FugaTES.Aplicacao.CasosDeUso.Mvp.ProcessarContratoAcm>();
  var blocked=await useCase.ExecutarAsync(f.Claim,default);Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna,blocked.Status);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Posts);
  f.Prep.Value=valid;var resumed=await useCase.ExecutarAsync(f.Claim with{FencingToken=2},default);
  Assert.Equal(StatusFluxoMvp.AguardandoCallback,resumed.Status);await useCase.ExecutarAsync(f.Claim with{FencingToken=3},default);Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Posts);
 }
 [Fact] public async Task Complete_use_case_does_not_overwrite_armed_stage_during_concurrent_dispatch()
 {
  var f=new Fixture();f.Soap.HoldBothLookups=true;var services=Services(f);FugaTES.HostIntegracao.MvpOrchestrationComposition.AddMvpOrchestration(services,new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"AcmR57:Enabled","true"}}).Build());
  using var provider=services.BuildServiceProvider();var useCase=provider.GetRequiredService<FugaTES.Aplicacao.CasosDeUso.Mvp.ProcessarContratoAcm>();
  await Task.WhenAll(useCase.ExecutarAsync(f.Claim,default),useCase.ExecutarAsync(f.Claim,default));
  Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Posts);Assert.Equal(StatusFluxoMvp.AguardandoCallback,f.Journal.Current!.Status);
 }
 static Microsoft.Extensions.DependencyInjection.ServiceCollection Services(Fixture f)
 {
  var s=new Microsoft.Extensions.DependencyInjection.ServiceCollection();s.AddSingleton<TimeProvider>(TimeProvider.System);
  s.AddSingleton<FugaTES.Aplicacao.Contratos.MotorIntegracao.IMotorIntegracaoRepository>(new FugaTES.Infraestrutura.MotorIntegracao.RepositorioMotorIntegracaoFake());
  s.AddSingleton<IContratoSapToledoVersaoStore>(f.Source);s.AddSingleton<IResultadoFluxoMvpStore>(f.Journal);s.AddSingleton<IAcmGuardianVinculoStore>(f.Link);
  s.AddSingleton<IAcmR57Preparacao>(f.Prep);s.AddSingleton<IAcmR57RuntimeGate>(new ApprovedTestGate());s.AddSingleton<IGuardianContratoSincronizacaoGateway>(f.Soap);
  s.AddSingleton<IAcmR104CallbackCorrelation>(f.Correlation);
  s.AddSingleton<FugaTES.Aplicacao.Contratos.Guardian.ICadastraContratoGuardianGateway>(f.Soap);s.AddSingleton<IAcmPreCadastroMinimoGatewayR57>(f.Rest);return s;
 }

 sealed class Fixture
 {
  public List<string> Events=new();public Source Source;public Prep Prep;public Soap Soap;public Rest Rest;public Journal Journal;public Link Link;public ClaimIntegracaoMotor Claim;
  public RepositorioCorrelacaoRecebimentoMemoria Correlations=new();public AcmR104CallbackCorrelation Correlation;
  public Fixture(string sap="0000001106",string code="1106")
  {
   var s=new ContratoSapToledoSnapshot(null,sap,"AC3P","SYNTHETIC CONTRACT",null,null,null,null,null,"T",12.345m,null,"0020000184","7000000",null,null,null,null,null,99m,"3011","10",[]){Item="000020",UnidCont="KG"};
   var canonical=new ContratoSapToledoCanonicalizer().Canonicalizar(s);var id=Guid.NewGuid();Source=new(new(ResultadoRegistroVersaoContrato.Criada,sap,1,canonical.FingerprintSha256,id,canonical.Snapshot));
   // R69 scenario constants and R104 identity bindings are exercised only against offline dependencies.
   var reference="OC"+code;const string callback="https://callback.invalid/approved";
   var snapshot=new RecebimentoSapCpiMvp("RECEBIMENTO","FLX_RECEBIMENTO",s.Material!,s.IdParceiro!,"SYNTH-BP",sap,reference,"AC3P","3011","10","AC3P",null,10,sap,callback);
   Prep=new(new(sap,"000020","3011",canonical.FingerprintSha256,code,"308102","309837",s.IdParceiro!,s.Material!,"20000184","7000000","DOC-OFFLINE-1106",2.5m,reference,"SYNTHETIC-ORIGIN",callback,"SYNTHETIC-MAPPING-PROOF","SYNTHETIC-DOCUMENT-PROOF","SYNTHETIC-WEIGHT-PROOF","SYNTHETIC-ADDITIONAL-PROOF",true)
   {
    RestBinding=new(id,1,sap,canonical.FingerprintSha256,"SYNTH-CARRIER","FLX_RECEBIMENTO","SYNTHETIC-CARRIER-PROOF","SYNTHETIC-FLOW-PROOF"),
    CallbackBinding=new("HML",callback,"192.0.2.1/32",true,true,"SYNTHETIC-CALLBACK-PROOF"),
    CorrelationBinding=new(id,1,sap,canonical.FingerprintSha256,"SYNTHETIC-SNAPSHOT-PROOF",snapshot)
   });
   Correlation=new(Correlations,TimeProvider.System);
   Soap=new(this);Rest=new(this);Journal=new(this);Link=new(this);Claim=new(id,Guid.NewGuid(),"synthetic-correlation","SAP","TOLEDO","SAP_CONTRATO_TOLEDO",sap,canonical.FingerprintSha256,1,"synthetic-worker",DateTimeOffset.UtcNow.AddMinutes(1));
  }
  public AcmR57Pipeline Pipeline()=>new(Source,Prep,new ApprovedTestGate(),Soap,Rest,Journal,Link,Correlation);
 }
 sealed class ApprovedTestGate:IAcmR57RuntimeGate {public string? Bloqueio(VersaoContratoSapToledo v)=>null;}
 sealed class Prep(AcmR57Vinculos v):IAcmR57Preparacao {public AcmR57Vinculos Value=v;public AcmR57Vinculos Obter(VersaoContratoSapToledo v)=>Value;}
 sealed class Source(VersaoContratoSapToledo v):IContratoSapToledoVersaoStore
 {public VersaoContratoSapToledo Version=v;public Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id,CancellationToken ct)=>Task.FromResult<VersaoContratoSapToledo?>(id==Version.IntegracaoId?Version:null);public Task<bool> TemPredecessorNaoTerminalAsync(string n,long v,CancellationToken ct)=>Task.FromResult(false);public Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot s,string f,string p,string c,CancellationToken ct)=>throw new NotSupportedException();}
 sealed class Soap(Fixture f):IGuardianContratoSincronizacaoGateway
 {
  public int Creates,Updates,Lookups;public bool Existing,Mismatch,WrongCode,HoldBothLookups;public Exception? Error;public ContratoGuardianSet26? Last;
  readonly TaskCompletionSource both=new(TaskCreationOptions.RunContinuationsAsynchronously);
  public async Task<ConsultaContratoGuardian> ConsultarAsync(string code,CancellationToken ct){var count=Interlocked.Increment(ref Lookups);lock(f.Events)f.Events.Add("lookup");if(HoldBothLookups){if(count==2)both.TrySetResult();await both.Task.WaitAsync(TimeSpan.FromSeconds(5),ct);}var c=AcmR57Mapping.Criar(f.Source.Version,f.Prep.Value).Contrato;return Existing?new ConsultaContratoGuardian(EstadoConsultaContratoGuardian.Existente,Mismatch?c with{ValorContrato=777m}:c,581):new(EstadoConsultaContratoGuardian.InexistenteComprovado);}
  public Task CadastrarAsync(ContratoGuardianSet26 c,CancellationToken ct)=>throw new NotSupportedException();
  public Task<ResultadoContratoGuardianComResposta> CadastrarComRespostaAsync(ContratoGuardianSet26 c,CancellationToken ct){Creates++;Last=c;f.Events.Add("soap");if(Error is not null)throw Error;return Task.FromResult(new ResultadoContratoGuardianComResposta(WrongCode?"OTHER":c.Codigo,581));}
  public Task<ResultadoContratoGuardianComResposta> AlterarComRespostaAsync(ContratoGuardianSet26 a,ContratoGuardianSet26 d,CancellationToken ct){Updates++;throw new NotSupportedException();}
 }
 sealed class Rest(Fixture f):IAcmPreCadastroMinimoGatewayR57
 {
  public int Posts;public Exception? Error;public string? WrongField;public PreCadastroAcmR57? Last;
  public Task<ProtocoloAcmR57> EnviarMinimoAcmAsync(PreCadastroAcmR57 r,CancellationToken ct){Posts++;Last=r;f.Events.Add("rest");if(Error is not null)throw Error;return Task.FromResult(new ProtocoloAcmR57(WrongField=="id"?0:9471,WrongField=="contract"?"OTHER":r.Dados.CodigoContrato,WrongField=="item"?"OTHER":r.Dados.CodigoItem,WrongField=="emitter"?"OTHER":r.Dados.CodigoEmissor,WrongField=="reference"?"OTHER":r.Dados.NumeroReferencia,null));}
 }
 sealed class Journal(Fixture f):IResultadoFluxoMvpStore
 {
  public ResultadoFluxoMvp? Current;public bool Refuse,FailOnConfirm;readonly object sync=new();
  public Task ExigirDisponibilidadeAsync(CancellationToken ct)=>Task.CompletedTask;
  public Task<ResultadoFluxoMvp?> ObterAsync(ClaimIntegracaoMotor c,CancellationToken ct)=>Task.FromResult(Current);
  public Task<bool> PersistirAsync(ClaimIntegracaoMotor c,ResultadoFluxoMvp r,CancellationToken ct){lock(sync){if(FailOnConfirm&&r.Etapa=="R57_SOAP_CONFIRMED")throw new InvalidOperationException();Current=r;f.Events.Add(r.Etapa);return Task.FromResult(true);}}
  public Task<bool> ReservarEtapaAsync(ClaimIntegracaoMotor c,string? before,ResultadoFluxoMvp r,CancellationToken ct){lock(sync){if(Refuse||Current?.Etapa!=before)return Task.FromResult(false);if(FailOnConfirm&&r.Etapa=="R57_SOAP_CONFIRMED")throw new InvalidOperationException();Current=r;f.Events.Add(r.Etapa);return Task.FromResult(true);}}
 }
 sealed class Link(Fixture f):IAcmGuardianVinculoStore
 {
  public AcmGuardianVinculo? Current;public bool Collision;
  public Task<AcmGuardianVinculo> ReservarSolicitacaoUnicaAsync(VersaoContratoSapToledo v,string r,string code,CancellationToken ct){if(Collision)throw new InvalidOperationException();f.Events.Add("reserve_reference");Current??=new(v.NumeroContrato,v.Versao,v.IntegracaoId,r,code,null,null,null,null,null,null);return Task.FromResult(Current);}
  public Task<AcmGuardianVinculo> ReservarAsync(VersaoContratoSapToledo v,string r,string c,CancellationToken ct)=>throw new NotSupportedException();
  public Task<AcmGuardianVinculo?> ObterPorIntegracaoAsync(Guid id,CancellationToken ct)=>Task.FromResult(Current);
  public Task<AcmGuardianVinculo> RegistrarCodigoConfirmadoAsync(Guid id,string c,CancellationToken ct){f.Events.Add("contract_identity");Current=Current! with{CodigoContratoConfirmado=c};return Task.FromResult(Current);}
  public Task<AcmGuardianVinculo> RegistrarProtocoloConfirmadoAsync(Guid id,long p,string? t,CancellationToken ct){f.Events.Add("protocol_identity");Current=Current! with{ProtocoloIdGuardian=p,NumeroTicketGuardian=t};return Task.FromResult(Current);}
  public Task<AcmGuardianVinculo> VincularCallbackComprovadoAsync(Guid id,long c,long s,CancellationToken ct)=>throw new NotSupportedException();
  public Task<AcmGuardianVinculo?> ResolverCallbackAsync(long c,long s,CancellationToken ct)=>throw new NotSupportedException();
  public Task<StatusEventoAcmGuardian> RegistrarFingerprintCallbackAsync(long c,long s,string f,CancellationToken ct)=>throw new NotSupportedException();
 }
}
