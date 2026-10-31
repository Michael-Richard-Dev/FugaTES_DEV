using FugaTES.Aplicacao.Servicos.ManualRetry;
namespace FugaTES.Aplicacao.Servicos.Recebimento;

public sealed record ToledoSapRetryApproval(Guid ReciboId,Guid IntegracaoId,string OperadorSid,string Revisao,
    string ReferenciaAutorizacao,DateTimeOffset InicioUtc,DateTimeOffset FimUtc)
{ public ToledoSapOperatorWindow? EscopoAdmissao {get;init;} public bool ContinuoAutorizado {get;init;} }
public interface IToledoSapRetryApprovals
{
    Task<ToledoSapRetryApproval?> ObterAsync(Guid reciboId,AcmRetryActor actor,CancellationToken ct);
}
public interface IToledoSapRetryStore
{
    Task<DevolutivaToledoSapSnapshot?> LerAsync(Guid receipt,CancellationToken ct);
    // Re-read and lock, recheck revision/authority/attempt and persist command+operator+reason in one COMMIT.
    Task<ReciboComandoToledoSap> AdmitirAsync(ComandoReenvioToledoSap command,AcmRetryActor actor,ToledoSapRetryApproval approval,CancellationToken ct);
}
public sealed class ReenvioToledoSapService(IToledoSapRetryStore store,IToledoSapRetryApprovals approvals,TimeProvider time)
{
    public static bool Autorizado(ToledoSapRetryApproval? p,AcmRetryActor actor,Guid receipt,DateTimeOffset now)
      =>actor.Authenticated&&actor.Sid.StartsWith("S-1-5-21-",StringComparison.Ordinal)&&p is not null&&p.ReciboId==receipt&&
        p.IntegracaoId!=Guid.Empty&&p.OperadorSid==actor.Sid&&AcmManualRetryPolicy.Hash(p.Revisao)&&
        AcmManualRetryPolicy.Evidence(p.ReferenciaAutorizacao)&&ToledoSapActivationScope.Active(p.InicioUtc,p.FimUtc,p.ContinuoAutorizado,now)&&
        (!p.ContinuoAutorizado||(p.EscopoAdmissao is{ContinuoAutorizado:true} scope&&scope.Valida&&scope.OperadorSid==actor.Sid&&
          scope.InicioUtc==p.InicioUtc&&scope.FimUtc==p.FimUtc&&scope.ReferenciaAutorizacao==p.ReferenciaAutorizacao));
    public async Task<PreviaReenvioToledoSap?> PrepararAsync(Guid receipt,AcmRetryActor actor,CancellationToken ct)
    {
        var approval=await approvals.ObterAsync(receipt,actor,ct);
        if(!Autorizado(approval,actor,receipt,time.GetUtcNow()))return null;
        var snapshot=await store.LerAsync(receipt,ct);
        if(snapshot is null||snapshot.IntegracaoId!=approval!.IntegracaoId||snapshot.Revisao!=approval.Revisao)return null;
        return new(snapshot,DevolutivaToledoSapPolicy.Avaliar(snapshot,true));
    }
    public async Task<ReciboComandoToledoSap> SolicitarAsync(ComandoReenvioToledoSap request,AcmRetryActor actor,CancellationToken ct)
    {
        ReciboComandoToledoSap Block()=>new(request.ComandoId,request.ReciboId,"BLOCKED");
        if(!request.Confirmado||request.ComandoId==Guid.Empty||request.ReciboId==Guid.Empty||request.IntegracaoId==Guid.Empty||
            !AcmManualRetryPolicy.Hash(request.Revisao)||!AcmManualRetryService.SafeJustification(request.Justificativa))return Block();
        var approval=await approvals.ObterAsync(request.ReciboId,actor,ct);
        if(!Autorizado(approval,actor,request.ReciboId,time.GetUtcNow())||request.Revisao!=approval!.Revisao||request.IntegracaoId!=approval.IntegracaoId)return Block();
        return await store.AdmitirAsync(request,actor,approval,ct);
    }
}
