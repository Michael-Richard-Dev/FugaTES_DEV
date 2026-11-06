using System.Text;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
namespace FugaTES.Testes;
public sealed class MmMasterdataGateTests
{
 [Theory][InlineData(false,false,"MM_PRECHECK_MASTERS_MISSING")][InlineData(false,true,"MM_PRECHECK_EMITTER_MISSING")][InlineData(true,false,"MM_PRECHECK_MATERIAL_MISSING")][InlineData(true,true,"MM_PRECHECK_CONFIRMED")]
 public async Task Both_registered_active_identities_are_required_before_business_POST(bool e,bool m,string code)
 {
  var sender=new Sender();var gate=new MmMasterdataGate(new Emitters(e),new Materials(m),133289);var gateway=new MmValidatedGuardianGateway(gate,sender);
  var payload=Encoding.UTF8.GetBytes(MmRetryPolicyTests.Record().JsonEnviado);var check=await gate.CheckAsync(payload,default);Assert.Equal(code,check.Code);
  var result=await gateway.EnviarAdmitidoAsync(payload,_=>Task.FromResult(true),default);Assert.Equal(e&&m?1:0,sender.Calls);Assert.Equal(e&&m?EstadoProtocoloMm.Criado:EstadoProtocoloMm.Recusado,result.Estado);
 }
 [Fact] public async Task Unverified_or_foreign_identity_does_not_pass(){var gate=new MmMasterdataGate(new Emitters(true,"WRONG"),new Materials(true),133289);Assert.False((await gate.CheckAsync(Encoding.UTF8.GetBytes(MmRetryPolicyTests.Record().JsonEnviado),default)).Confirmed);}
 [Fact] public async Task Lookup_exception_before_POST_is_a_determinate_pre_post_block()
 {
  var sender=new Sender();var gateway=new MmValidatedGuardianGateway(new ThrowingGate(),sender);
  var result=await gateway.EnviarAdmitidoAsync(Encoding.UTF8.GetBytes(MmRetryPolicyTests.Record().JsonEnviado),_=>Task.FromResult(true),default);
  Assert.Equal(EstadoProtocoloMm.Recusado,result.Estado);Assert.Null(result.Http);Assert.Equal(0,sender.Calls);Assert.True(MmRetryPolicy.IsPrecheck(result.Mensagem));
 }
 sealed class ThrowingGate:IMmMasterdataGate{public Task<MmMasterdataCheck> CheckAsync(byte[] b,CancellationToken ct)=>throw new IOException("SIMULATED_READ_ERROR");}
 [Fact] public async Task Automatic_sender_rechecks_lease_after_slow_masterdata_GETs()
 {
  bool owned=true;var sender=new Sender();var gate=new RevokeGate(()=>owned=false);var gateway=new MmValidatedGuardianGateway(gate,sender);
  var registry=new R158.SapInboundReadiness.Tests.MmGuardianDispatchTests.Registry();
  await new EnvioAutomaticoMm(registry,gateway,"https://example.invalid/mm",_=>Task.FromResult(owned)).ProcessarAsync([MmRetryPolicyTests.Source()],default);
  Assert.Equal(0,sender.Calls);
 }
 sealed class RevokeGate(Action revoke):IMmMasterdataGate{public Task<MmMasterdataCheck> CheckAsync(byte[] b,CancellationToken ct){revoke();return Task.FromResult(new MmMasterdataCheck(true,"MM_PRECHECK_CONFIRMED","20000162","1000247",DateTimeOffset.UtcNow));}}
 sealed class Emitters(bool found,string code="20000162"):IGuardianEmitterRead{public Task<GuardianEmitterReadResult> ConsultarAsync(string c,CancellationToken ct)=>Task.FromResult(new GuardianEmitterReadResult(found,found?code:"",found?4:0,133289,found?200:404,found?"R171_EMITTER_CONFIRMED":"R171_EMITTER_NOT_FOUND"));}
 sealed class Materials(bool found):IGuardianMaterialRead{public Task<GuardianMaterialReadResult> ConsultarAsync(string c,CancellationToken ct)=>Task.FromResult(new GuardianMaterialReadResult(found,found?c:"",found?5:0,133289,found?200:404,found?"ACM_MATERIAL_CONFIRMED":"ACM_MATERIAL_NOT_FOUND"));}
 sealed class Sender:IEnviarProtocoloMmGateway{public int Calls;public Task<ResultadoProtocoloMm> EnviarAsync(byte[] b,CancellationToken ct){Calls++;return Task.FromResult(new ResultadoProtocoloMm(EstadoProtocoloMm.Criado,201,42,"ok"));}}
}
