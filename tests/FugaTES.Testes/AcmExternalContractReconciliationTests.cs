using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Contratos.Guardian;
using Xunit;
public sealed class AcmExternalContractReconciliationTests
{
 static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-01T14:00:00Z");
 static AcmRetrySnapshot Before()=>new(){IntegrationId=Guid.Parse("4c8fd7ee-cb56-4605-90c3-13eb77d13327"),Contract="0000001183",Version=1,Fingerprint=new('A',64),CanonicalFingerprint=new('A',64),CorrelationId="SYNTHETIC_CORRELATION",State="AGUARDANDO_REPROCESSAMENTO",Fence=1,AttemptId=Guid.NewGuid(),IdentityVerified=true,AttemptOutcome="FALHA",JournalStatus="AGUARDANDO_DEPENDENCIA_EXTERNA",Stage="R165U_PRE_SEND_BLOCKED",ErrorCode="R171_EMITTER_NOT_FOUND",LatestVersion=true,ReceivedUtc=Now.AddMinutes(-30),Phase="RESULTADO_RECEBIDO",AttemptCount=1,JournalPresent=true};
 static ContratoGuardianSet26 Contract()=>new("1183","SYNTHETIC",150000m,"308102",0m,"309837"){CodigoEmissor="20000434",CodigoItem="7000000"};
 static bool Allowed(AcmRetrySnapshot s,string fault="")
 {
  var p=typeof(AcmManualRetryPolicy).Assembly.GetType("FugaTES.Aplicacao.Servicos.ManualRetry.AcmExternalContractReconciliationProof");Assert.NotNull(p);var proof=Activator.CreateInstance(p)!;
  var fields=new Dictionary<string,object?>{{"OperationId",Guid.NewGuid()},{"IntegrationId",s.IntegrationId},{"SapCode",s.Contract},{"ExternalCode",fault=="other_code"?"1184":"1183"},{"Version",s.Version},{"Fingerprint",s.Fingerprint},{"SnapshotDigest",fault=="digest"?new string('B',64):s.Digest()},{"Lookup",EstadoConsultaContratoGuardian.Existente},{"Sequence",fault=="no_sequence"?0L:608L},{"ObservedUtc",fault=="stale"?Now.AddMinutes(-3):Now},{"SoapHttp",fault=="http"?201:200},{"SoapResponseHash",new string('C',64)},{"Desired",new AcmR57NormalContract(Contract(),"OC1183")},{"Observed",fault=="partner"?Contract() with{CodigoEmissor="999"}:fault=="material"?Contract() with{CodigoItem="999"}:fault=="quantity"?Contract() with{ValorContrato=1}:Contract()},{"OperatorSid","S-1-5-21-1-2-3-1001"},{"AuthorizationReference","OPERATOR_RECONCILIATION_1183"},{"IdentifierMappingEvidence",fault=="no_mapping"?"":"OPERATOR_CONFIRMED_LITERAL_1183"},{"BackupReference","VERIFIED_BACKUP_SYNTHETIC"}};
  if(fault.StartsWith("both_",StringComparison.Ordinal))
  {var altered=fault switch{"both_partner"=>Contract() with{CodigoEmissor="999"},"both_material"=>Contract() with{CodigoItem="999"},"both_quantity"=>Contract() with{ValorContrato=1},"both_description"=>Contract() with{Descricao="OTHER"},_=>Contract()};fields["Desired"]=new AcmR57NormalContract(altered,fault=="both_reference"?"OTHER":"OC1183");fields["Observed"]=altered;}
  foreach(var(k,v)in fields){var prop=p.GetProperty(k);Assert.NotNull(prop);prop.SetValue(proof,v);}
  var policy=p.Assembly.GetType("FugaTES.Aplicacao.Servicos.ManualRetry.AcmExternalContractReconciliationPolicy");Assert.NotNull(policy);return(bool)policy.GetMethod("CanReconcile")!.Invoke(null,[s,proof,new AcmR57NormalContract(Contract() with{Codigo=s.Contract},"OC1183"),Now])!;
 }
 [Fact]public void Exact_confirmed_manual_contract_can_be_reconciled_without_new_attempt()=>Assert.True(Allowed(Before()));
 [Theory][InlineData("other_code")][InlineData("digest")][InlineData("no_sequence")][InlineData("stale")][InlineData("http")][InlineData("partner")][InlineData("material")][InlineData("quantity")][InlineData("no_mapping")][InlineData("both_partner")][InlineData("both_material")][InlineData("both_quantity")][InlineData("both_description")][InlineData("both_reference")]
 public void Unproven_identity_or_content_blocks(string fault)=>Assert.False(Allowed(Before(),fault));
 [Theory][InlineData("active")][InlineData("pending_command")][InlineData("unknown")][InlineData("synthetic")][InlineData("old_version")][InlineData("confirmed")][InlineData("protocol")][InlineData("wrong_stage")]
 public void Unsafe_source_state_blocks(string fault)
 {var s=Before();s=fault switch{"active"=>s with{Worker="OTHER",LeaseUntil=Now.AddMinutes(1)},"pending_command"=>s with{HasPendingCommand=true},"unknown"=>s with{Indeterminate=true},"synthetic"=>s with{Synthetic=true},"old_version"=>s with{LatestVersion=false},"confirmed"=>s with{GuardianConfirmedCode="1183"},"protocol"=>s with{HasProtocolOrCallback=true},_=>s with{Stage="R165U_SOAP_ARMED"}};Assert.False(Allowed(s));}
}
