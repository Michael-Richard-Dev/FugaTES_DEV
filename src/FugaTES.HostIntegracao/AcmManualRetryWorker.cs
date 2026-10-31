using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;

namespace FugaTES.HostIntegracao;

// Candidate bounded worker adapter. Never registered/started automatically in this gate.
public sealed class AcmManualRetryWorker(IAcmManualRetryStore store,MotorIntegracaoServico motor,TimeProvider time,bool globalWorkerEnabled=true)
{
    public async Task<string> RunApprovedWindowAsync(AcmRetryApproval approval,string worker,CancellationToken ct)
    {
        if(globalWorkerEnabled||!motor.ManualRetryScopeMatches(approval.Contract,approval.Item,worker))return "EXCLUSIVE_WORKER_BINDING_REQUIRED";
        while(!ct.IsCancellationRequested&&time.GetUtcNow()>=approval.NotBeforeUtc&&time.GetUtcNow()<approval.UntilUtc)
        {
            Guid? command;
            try{command=await store.PendingCommandAsync(approval,ct);}catch{return "QUEUE_UNVERIFIED_NO_RETRY";}
            if(command is { } id)return await ExecuteApprovedAsync(id,approval,worker,ct); // At most one command; no retry loop after dispatch/unknown.
            await Task.Delay(TimeSpan.FromSeconds(1),time,ct);
        }
        return "WINDOW_CLOSED";
    }
    public async Task<string> ExecuteApprovedAsync(Guid commandId,AcmRetryApproval approval,string worker,CancellationToken ct)
    {
        if(globalWorkerEnabled||!motor.ManualRetryScopeMatches(approval.Contract,approval.Item,worker))return "EXCLUSIVE_WORKER_BINDING_REQUIRED";
        AcmRetryWork? work;
        try{work=await store.StartAsync(commandId,approval,worker,time.GetUtcNow(),ct);}
        catch{return "START_COMMIT_UNVERIFIED_NO_RETRY";}
        if(work is null)return "NOT_STARTED";
        if(work.CommandId!=commandId||work.IntegrationId!=approval.IntegrationId||work.Worker!=worker)return "START_RECEIPT_UNVERIFIED_NO_DISPATCH";
        string result;
        try
        {
            var remaining=approval.UntilUtc-time.GetUtcNow();
            if(remaining<=TimeSpan.Zero||time.GetUtcNow()<approval.NotBeforeUtc||ct.IsCancellationRequested)
                result="WINDOW_CLOSED_NO_DISPATCH";
            else
            {
                using var deadline=new CancellationTokenSource(remaining,time);
                using var execution=CancellationTokenSource.CreateLinkedTokenSource(ct,deadline.Token);
                result=(await motor.ExecutarPorIdAsync(work.IntegrationId,execution.Token)).ToString();
            }
        }
        catch{result="UNKNOWN";}
        try
        {
            using var capture=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await store.CompleteAsync(work,result,time.GetUtcNow(),capture.Token);
        }
        catch{return "OUTCOME_AUDIT_UNVERIFIED_NO_RETRY";}
        return result;
    }
}
