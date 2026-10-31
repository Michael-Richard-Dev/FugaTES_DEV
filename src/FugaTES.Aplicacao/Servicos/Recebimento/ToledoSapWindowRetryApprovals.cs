using FugaTES.Aplicacao.Servicos.ManualRetry;
namespace FugaTES.Aplicacao.Servicos.Recebimento;

// Operator authority is provisioned server-side. The receipt target and observed revision are
// resolved from durable state; the UI still confirms that exact target and a justification.
public sealed record ToledoSapOperatorWindow(string OperadorSid,string OrigemAdmitida,string ReferenciaAutorizacao,
 DateTimeOffset InicioUtc,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]DateTimeOffset FimUtc)
{
 public bool ContinuoAutorizado {get;init;}
 public bool Valida=>System.Text.RegularExpressions.Regex.IsMatch(OperadorSid,@"\AS-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+\z")&&
  AcmManualRetryPolicy.Evidence(OrigemAdmitida)&&AcmManualRetryPolicy.Evidence(ReferenciaAutorizacao)&&
  ToledoSapActivationScope.Valid(InicioUtc,FimUtc,ContinuoAutorizado);
}
public interface IToledoSapAdmissionProof
{
 Task<bool> ProvarAdmissaoAsync(Guid receipt,Guid integration,ToledoSapOperatorWindow window,CancellationToken ct);
}
public sealed class ToledoSapWindowRetryApprovals(IToledoSapRetryStore store,IToledoSapAdmissionProof proof,ToledoSapOperatorWindow window,TimeProvider time):IToledoSapRetryApprovals
{
 public async Task<ToledoSapRetryApproval?> ObterAsync(Guid receipt,AcmRetryActor actor,CancellationToken ct)
 {
  var now=time.GetUtcNow();
  if(!window.Valida||!actor.Authenticated||actor.Sid!=window.OperadorSid||receipt==Guid.Empty||!ToledoSapActivationScope.Active(window.InicioUtc,window.FimUtc,window.ContinuoAutorizado,now))return null;
  var snapshot=await store.LerAsync(receipt,ct);
  if(snapshot is null||snapshot.ReciboId!=receipt||!DevolutivaToledoSapPolicy.Avaliar(snapshot,true).Permitido||
   !await proof.ProvarAdmissaoAsync(receipt,snapshot.IntegracaoId,window,ct))return null;
  return new(receipt,snapshot.IntegracaoId,actor.Sid,snapshot.Revisao,window.ReferenciaAutorizacao,window.InicioUtc,window.FimUtc){EscopoAdmissao=window,ContinuoAutorizado=window.ContinuoAutorizado};
 }
}
