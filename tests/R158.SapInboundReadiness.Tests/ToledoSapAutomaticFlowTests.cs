using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Callback.Http;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Integracao.SAP.Autenticacao;
using Microsoft.Extensions.Configuration;
namespace FugaTES.Testes;
public sealed class ToledoSapAutomaticFlowTests
{
 const string Original="""{"SistemaBalanca":"Toledo","Centro":"3011","IdPesagem":"900002","to_Pesagem":[{"ContratoACM":"0099999902"}]}""";
 static Dictionary<string,string?> Profile()=>new(){["ToledoSapReturn:Enabled"]="true",["ToledoSapReturn:Environment"]="DEV",["ToledoSapReturn:ExecutionAuthorized"]="true",["ToledoSapReturn:AutomaticProcessingEnabled"]="true",["ToledoSapReturn:OriginalDocumentApproved"]="true",["ToledoSapReturn:NotBeforeUtc"]=DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),["ToledoSapReturn:UntilUtc"]=DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),["ToledoSapReturn:ApprovedSourceIdentity"]="OFFLINE_APPROVED_SOURCE",["ToledoSapReturn:AuthorizationReference"]="OFFLINE_USER_APPROVAL",["Sap:BaseUrl"]="https://vhfufds4ci.sap.fugacouros.com.br:44300",["Sap:SapClient"]="110"};
 [Theory][InlineData("fugates_dev_read_app")][InlineData("fugates_acm_hml_app")]
 public void Toledo_read_repository_accepts_only_explicit_authorized_reader_bindings_without_connecting(string role)
 {Assert.NotNull(new FugaTES.Persistencia.Consultas.RecebimentosToledo.RecebimentosToledoPostgreSqlReadRepository(null!,role));}
 [Theory][InlineData("postgres")][InlineData("fugates_sap_inbound_app")][InlineData("fugates_dev_app")][InlineData("fugates_dev_owner")]
 public void Toledo_read_repository_rejects_unapproved_roles_before_opening_a_connection(string role)
 {Assert.Throws<InvalidOperationException>(()=>new FugaTES.Persistencia.Consultas.RecebimentosToledo.RecebimentosToledoPostgreSqlReadRepository(null!,role));}
 [Fact] public void Automatic_return_is_disabled_by_default_and_creates_no_worker()
 {var services=new ServiceCollection();Assert.Null(ToledoSapReturnComposition.Configure(services,new ConfigurationBuilder().Build()));Assert.Empty(services);}
 [Fact] public void Enabled_return_requires_existing_SAP_authentication_and_never_creates_a_credential_provider()
 {var services=new ServiceCollection();Assert.Throws<InvalidOperationException>(()=>ToledoSapReturnComposition.Configure(services,new ConfigurationBuilder().AddInMemoryCollection(Profile()).Build()));Assert.DoesNotContain(services,x=>x.ServiceType==typeof(IAutenticacaoSap));}
 [Theory][InlineData("ToledoSapReturn:Environment","HML")][InlineData("ToledoSapReturn:ExecutionAuthorized","false")][InlineData("ToledoSapReturn:AutomaticProcessingEnabled","false")][InlineData("ToledoSapReturn:OriginalDocumentApproved","false")][InlineData("Sap:SapClient","100")][InlineData("Sap:BaseUrl","http://sap.invalid")][InlineData("FUGATES_INTEGRATION_WORKER_ENABLED","true")]
 public void Wrong_scope_or_global_worker_cannot_be_enabled(string key,string value)
 {var config=Profile();config[key]=value;var services=new ServiceCollection();services.AddSingleton<IAutenticacaoSap,newAuth>();Assert.Throws<InvalidOperationException>(()=>ToledoSapReturnComposition.Configure(services,new ConfigurationBuilder().AddInMemoryCollection(config).Build()));}
 [Fact] public void Prepared_composition_has_one_typed_worker_and_does_not_open_a_database()
 {var services=new ServiceCollection();services.AddSingleton<IAutenticacaoSap,newAuth>();var window=ToledoSapReturnComposition.Configure(services,new ConfigurationBuilder().AddInMemoryCollection(Profile()).Build());Assert.NotNull(window);Assert.Single(services,d=>d.ServiceType==typeof(Microsoft.Extensions.Hosting.IHostedService));}
 [Fact] public void Existing_systemd_provider_is_selected_only_explicitly_with_FUGATES_user_and_without_reading_secrets()
 {var config=Profile();config["ToledoSapReturn:SapAuthenticationProvider"]="CarrierLinuxSapAuthentication";config["ToledoSapReturn:ExpectedSapUser"]="FUGATES";var services=new ServiceCollection();Assert.NotNull(ToledoSapReturnComposition.Configure(services,new ConfigurationBuilder().AddInMemoryCollection(config).Build()));Assert.Single(services,d=>d.ServiceType==typeof(IAutenticacaoSap));}
 [Fact] public async Task Existing_systemd_provider_rejects_wrong_origin_before_any_credential_read()
 {var auth=new FugaTES.HostIntegracao.CarrierLinuxSapAuthentication(new Uri("https://sap.invalid"),new FugaTES.HostIntegracao.CarrierSystemdCredentials("/NOT_A_REAL_CREDENTIAL_DIRECTORY"),"FUGATES");await Assert.ThrowsAsync<FugaTES.HostIntegracao.CarrierServiceStartupException>(()=>auth.AplicarAsync(new HttpRequestMessage(HttpMethod.Get,"https://wrong.invalid"),default));}
 [Fact] public void Actual_JSON_boolean_configuration_is_accepted_without_string_casing_assumptions()
 {var profile=new{ToledoSapReturn=new{Enabled=true,Environment="DEV",ExecutionAuthorized=true,AutomaticProcessingEnabled=true,OriginalDocumentApproved=true,NotBeforeUtc=DateTimeOffset.UtcNow.AddMinutes(-1),UntilUtc=DateTimeOffset.UtcNow.AddMinutes(5),ApprovedSourceIdentity="OFFLINE_SOURCE",AuthorizationReference="OFFLINE_APPROVAL"},Sap=new{BaseUrl="https://vhfufds4ci.sap.fugacouros.com.br:44300",SapClient="110"}};using var stream=new MemoryStream(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(profile));var config=new ConfigurationBuilder().AddJsonStream(stream).Build();var services=new ServiceCollection();services.AddSingleton<IAutenticacaoSap,newAuth>();Assert.NotNull(ToledoSapReturnComposition.Configure(services,config));}
 [Fact] public void Admission_window_rejects_old_receipts_other_origins_and_unbounded_authorization()
 {var now=DateTimeOffset.UtcNow;var w=new ToledoSapAdmissionWindow(now,now.AddMinutes(5),"APPROVED_SOURCE","OFFLINE_AUTHORIZATION");Assert.True(w.Admite(now,"APPROVED_SOURCE"));Assert.False(w.Admite(now.AddSeconds(-1),"APPROVED_SOURCE"));Assert.False(w.Admite(now,"OTHER"));Assert.False((w with{FimUtc=now.AddDays(2)}).Valida);}
 [Theory][InlineData(ResultadoToledoSapCodigo.FalhaAntesDoPost,EstadoIntegracao.FalhaDefinitiva)][InlineData(ResultadoToledoSapCodigo.RejeicaoHttp,EstadoIntegracao.FalhaDefinitiva)][InlineData(ResultadoToledoSapCodigo.ResultadoIndeterminado,EstadoIntegracao.ResultadoIndeterminado)][InlineData(ResultadoToledoSapCodigo.AceiteHttpSemConfirmacaoEmpresarial,EstadoIntegracao.ResultadoIndeterminado)]
 public async Task Existing_motor_runs_the_new_receipt_once_and_preserves_attempt_audit_and_terminal_block(ResultadoToledoSapCodigo code,EstadoIntegracao final)
 {
  var receipt=Guid.NewGuid();var hash=RegistrarPesagemToledoCasoDeUso.Fingerprint(Original);var db=new RepositorioMotorIntegracaoFake();var candidate=ToledoSapWork.Candidato(receipt,hash);
  Assert.Equal(ResultadoRegistroIntegracao.Criada,await db.RegistrarCandidatoAsync(candidate,DateTimeOffset.UtcNow.AddSeconds(-1),default));Assert.Equal(ResultadoRegistroIntegracao.JaExistente,await db.RegistrarCandidatoAsync(candidate,DateTimeOffset.UtcNow,default));
  var work=Assert.Single(db.Integracoes);var source=new Source(new(receipt,"3011","900002",Original,hash,true,false));var gateway=new FakeGateway(new(code,code==ResultadoToledoSapCodigo.RejeicaoHttp?400:code==ResultadoToledoSapCodigo.AceiteHttpSemConfirmacaoEmpresarial?200:null,code!=ResultadoToledoSapCodigo.FalhaAntesDoPost));
  var motor=new MotorIntegracaoServico(db,new ProcessadorDocumentoToledoSap(source,gateway),new Detector(),new ValidadorGenericoIntegracao(),new Worker(),new Timeout(),TimeProvider.System,new(){LeaseDuration=TimeSpan.FromSeconds(30),LeaseRenewInterval=TimeSpan.FromSeconds(5),RetryBackoff=TimeSpan.FromSeconds(1)});
  await motor.ExecutarPorIdAsync(work.Id,default);await motor.ExecutarPorIdAsync(work.Id,default);
  Assert.Equal(1,gateway.Calls);Assert.Equal(final,Assert.Single(db.Integracoes).Estado);Assert.Single(db.Tentativas);Assert.NotEmpty(db.Auditorias);Assert.Equal(Original,source.Document.DocumentoJson);
 }
 [Fact] public void Full_document_approval_is_explicit_and_does_not_change_the_default_H4_policy()
 {var services=new ServiceCollection();services.AddSingleton<IAutenticacaoSap,newAuth>();var config=Profile();var window=ToledoSapReturnComposition.Configure(services,new ConfigurationBuilder().AddInMemoryCollection(config).Build());Assert.False(window!.DocumentoCompletoAprovado);config["ToledoSapReturn:CompleteDocumentAuthorized"]="true";services=new();services.AddSingleton<IAutenticacaoSap,newAuth>();window=ToledoSapReturnComposition.Configure(services,new ConfigurationBuilder().AddInMemoryCollection(config).Build());Assert.True(window!.DocumentoCompletoAprovado);}
 sealed class Source(DocumentoToledoSap doc):IDevolutivaToledoSapStore
 {public DocumentoToledoSap Document=doc;public Task<DocumentoToledoSap?> ObterAsync(ClaimIntegracaoMotor c,CancellationToken ct)=>Task.FromResult<DocumentoToledoSap?>(Document);public Task<bool> MarcarDespachoAsync(ClaimIntegracaoMotor c,CancellationToken ct)=>Task.FromResult(true);public Task<bool> ValidarDespachoAsync(ClaimIntegracaoMotor c,PlanoEnvioToledoSap plano,CancellationToken ct)=>Task.FromResult(true);public Task RegistrarRetornoAsync(ClaimIntegracaoMotor c,RetornoToledoSap r,CancellationToken ct)=>Task.CompletedTask;}
 sealed class FakeGateway(RetornoToledoSap result):IDocumentoToledoSapGateway
 {public int Calls;public async Task<RetornoToledoSap> EnviarOriginalAsync(DocumentoToledoSap d,Func<PlanoEnvioToledoSap,CancellationToken,Task<bool>> gate,CancellationToken ct){Assert.True(await gate(new(new('A',64),"TOLEDO_SAP_ODATA_V1",1767225600000),ct));Calls++;return result;}}
 sealed class Detector:IDetectorCandidatosIntegracao{public Task<IReadOnlyList<CandidatoIntegracao>> DetectarAsync(CancellationToken ct)=>throw new InvalidOperationException("GLOBAL_POLL_MUST_NOT_RUN");}
 sealed class Worker:IIdentidadeWorkerProvider{public IdentidadeWorkerIntegracao Obter()=>new("OFFLINE_AUTOMATIC");}
 sealed class Timeout:IPoliticaTimeoutProcessamentoIntegracao{public TimeSpan ObterTimeout(ClaimIntegracaoMotor c)=>TimeSpan.FromSeconds(5);}
 sealed class newAuth:IAutenticacaoSap{public Task AplicarAsync(HttpRequestMessage r,CancellationToken ct)=>Task.CompletedTask;}
}
