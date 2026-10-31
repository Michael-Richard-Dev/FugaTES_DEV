using FugaTES.Callback.Http;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Integracao.SAP.Autenticacao;
using Microsoft.Extensions.Configuration;
namespace FugaTES.Testes;
public sealed class ToledoSapContinuousTests
{
 internal static Dictionary<string,string?> Profile()=>new(){["ToledoSapReturn:Enabled"]="true",["ToledoSapReturn:Environment"]="DEV",["ToledoSapReturn:ExecutionAuthorized"]="true",["ToledoSapReturn:AutomaticProcessingEnabled"]="true",["ToledoSapReturn:OriginalDocumentApproved"]="true",["ToledoSapReturn:NotBeforeUtc"]="2026-10-31T11:29:11Z",["ToledoSapReturn:ContinuousEnabled"]="true",["ToledoSapReturn:ApprovedSourceIdentity"]="OFFLINE_PRIVATE_SOURCE",["ToledoSapReturn:AuthorizationReference"]="OFFLINE_CONTINUOUS_APPROVAL",["Sap:BaseUrl"]="https://vhfufds4ci.sap.fugacouros.com.br:44300",["Sap:SapClient"]="110"};
 [Fact] public void Explicit_continuous_mode_accepts_new_receipts_after365days_without_future_expiry_date()
 {
  var services=new ServiceCollection();services.AddSingleton<IAutenticacaoSap,Auth>();
  var window=ToledoSapReturnComposition.Configure(services,new ConfigurationBuilder().AddInMemoryCollection(Profile()).Build());
  Assert.NotNull(window);Assert.Equal(default,window.FimUtc);Assert.True(window.Admite(DateTimeOffset.Parse("2027-11-01T11:00:00Z"),"OFFLINE_PRIVATE_SOURCE"));
  Assert.False(window.Admite(DateTimeOffset.Parse("2026-10-31T11:29:10Z"),"OFFLINE_PRIVATE_SOURCE"));
 }
 [Theory][InlineData(null)][InlineData("false")][InlineData("maybe")]
 public void Missing_or_invalid_continuous_authorization_never_opens_a_missing_expiry(string? flag)
 {var profile=Profile();profile["ToledoSapReturn:ContinuousEnabled"]=flag;var services=new ServiceCollection();services.AddSingleton<IAutenticacaoSap,Auth>();Assert.Throws<InvalidOperationException>(()=>ToledoSapReturnComposition.Configure(services,new ConfigurationBuilder().AddInMemoryCollection(profile).Build()));}
 [Fact] public void Continuous_mode_with_an_expiry_is_rejected_as_ambiguous()
 {var profile=Profile();profile["ToledoSapReturn:UntilUtc"]="2026-11-01T11:29:11Z";var services=new ServiceCollection();services.AddSingleton<IAutenticacaoSap,Auth>();Assert.Throws<InvalidOperationException>(()=>ToledoSapReturnComposition.Configure(services,new ConfigurationBuilder().AddInMemoryCollection(profile).Build()));}
 [Theory][InlineData("historical")][InlineData("source")][InlineData("missingStart")][InlineData("reference")][InlineData("contradictoryEnd")]
 public void Continuous_activation_keeps_source_lower_cutoff_and_evidence_guards(string defect)
 {var start=DateTimeOffset.Parse("2026-10-31T11:29:11Z");var w=new ToledoSapAdmissionWindow(start,default,"OFFLINE_PRIVATE_SOURCE","OFFLINE_AUTHORIZATION"){ContinuoAutorizado=true};
  var received=start.AddYears(2);var source="OFFLINE_PRIVATE_SOURCE";
  if(defect=="historical")received=start.AddSeconds(-1);if(defect=="source")source="OTHER";if(defect=="missingStart")w=w with{InicioUtc=default};if(defect=="reference")w=w with{ReferenciaAutorizacao=""};if(defect=="contradictoryEnd")w=w with{FimUtc=start.AddHours(1)};
  Assert.False(w.Admite(received,source));}
 [Fact] public void Bounded_mode_still_expires_and_cannot_exceed24hours()
 {var start=DateTimeOffset.Parse("2026-10-31T11:29:11Z");var w=new ToledoSapAdmissionWindow(start,start.AddDays(1),"SOURCE","REFERENCE");Assert.True(w.Valida);Assert.False(w.Admite(start.AddDays(1),"SOURCE"));Assert.False((w with{FimUtc=start.AddDays(2)}).Valida);Assert.False((w with{FimUtc=default}).Valida);}
 [Fact] public void Protected_operator_profile_has_explicit_continuous_flag_and_no_artificial_end()
 {var raw="""{"operadorSid":"S-1-5-21-111-222-333-1001","origemAdmitida":"OFFLINE_PRIVATE_SOURCE","referenciaAutorizacao":"OFFLINE_APPROVAL","inicioUtc":"2026-10-31T11:29:11Z","continuoAutorizado":true}""";
  var w=System.Text.Json.JsonSerializer.Deserialize<ToledoSapOperatorWindow>(raw,FugaTES.Callback.Http.ManualRetry.AcmManualRetryCommandProfile.Json)!;Assert.True(w.Valida);Assert.True(w.ContinuoAutorizado);Assert.Equal(default,w.FimUtc);
  var closed=System.Text.Json.JsonSerializer.Deserialize<ToledoSapOperatorWindow>(raw.Replace("true","false"),FugaTES.Callback.Http.ManualRetry.AcmManualRetryCommandProfile.Json)!;Assert.False(closed.Valida);}
 [Theory][InlineData("ok")][InlineData("completed")][InlineData("unknown")][InlineData("http400")][InlineData("synthetic")][InlineData("historical")][InlineData("source")][InlineData("actor")][InlineData("revision")][InlineData("confirmation")]
 public async Task Continuous_retry_remains_targeted_and_guarded_after365days(string condition)
 {
  var start=DateTimeOffset.Parse("2026-10-31T11:29:11Z");var clock=new Clock(start.AddDays(366));var actor=new FugaTES.Aplicacao.Servicos.ManualRetry.AcmRetryActor(true,"S-1-5-21-111-222-333-1001");
  var w=new ToledoSapOperatorWindow(actor.Sid,"OFFLINE_PRIVATE_SOURCE","OFFLINE_APPROVAL",start,default){ContinuoAutorizado=true};
  var store=new Store();if(condition=="completed")store.Row=store.Row with{Estado="CONCLUIDA"};if(condition=="unknown")store.Row=store.Row with{ResultadoIndeterminado=true};if(condition=="http400")store.Row=store.Row with{Codigo="RejeicaoHttp",HttpStatus=400};if(condition=="synthetic")store.Row=store.Row with{Sintetico=true};if(condition is "historical" or "source")store.Proven=false;if(condition=="actor")actor=actor with{Sid="S-1-5-21-1-2-3-1001"};
  var approvals=new ToledoSapWindowRetryApprovals(store,store,w,clock);var service=new ReenvioToledoSapService(store,approvals,clock);var row=store.Row;
  var cmd=new ComandoReenvioToledoSap(Guid.NewGuid(),row.ReciboId,row.IntegracaoId,condition=="revision"?new string('C',64):row.Revisao,"Operator requests recovery of this exact proven before-POST failure",condition!="confirmation");
  var result=await service.SolicitarAsync(cmd,actor,default);Assert.Equal(condition=="ok"?"QUEUED":"BLOCKED",result.Estado);Assert.Equal(condition=="ok"?1:0,store.Commands);
 }
 [Fact] public void A_bare_permanent_approval_cannot_replace_server_side_admission_proof()
 {var start=DateTimeOffset.Parse("2026-10-31T11:29:11Z");var receipt=Guid.NewGuid();var actor=new FugaTES.Aplicacao.Servicos.ManualRetry.AcmRetryActor(true,"S-1-5-21-111-222-333-1001");var a=new ToledoSapRetryApproval(receipt,Guid.NewGuid(),actor.Sid,new string('A',64),"OFFLINE_APPROVAL",start,default){ContinuoAutorizado=true};Assert.False(ReenvioToledoSapService.Autorizado(a,actor,receipt,start.AddDays(365)));}
 sealed class Clock(DateTimeOffset now):TimeProvider{public override DateTimeOffset GetUtcNow()=>now;}
 sealed class Store:IToledoSapRetryStore,IToledoSapAdmissionProof
 {
  public DevolutivaToledoSapSnapshot Row=new(Guid.NewGuid(),Guid.NewGuid(),"FALHA_DEFINITIVA","FalhaAntesDoPost",503,1,false,false,true,false,true,new('A',64),new('B',64),DateTimeOffset.UtcNow);public bool Proven=true;public int Commands;
  public Task<DevolutivaToledoSapSnapshot?> LerAsync(Guid id,CancellationToken ct)=>Task.FromResult<DevolutivaToledoSapSnapshot?>(id==Row.ReciboId?Row:null);
  public Task<bool> ProvarAdmissaoAsync(Guid receipt,Guid integration,ToledoSapOperatorWindow w,CancellationToken ct)=>Task.FromResult(Proven&&receipt==Row.ReciboId&&integration==Row.IntegracaoId);
  public Task<ReciboComandoToledoSap> AdmitirAsync(ComandoReenvioToledoSap c,FugaTES.Aplicacao.Servicos.ManualRetry.AcmRetryActor actor,ToledoSapRetryApproval a,CancellationToken ct){Commands++;return Task.FromResult(new ReciboComandoToledoSap(c.ComandoId,c.ReciboId,"QUEUED"));}
 }
 sealed class Auth:IAutenticacaoSap{public Task AplicarAsync(HttpRequestMessage r,CancellationToken ct)=>Task.CompletedTask;}
}
