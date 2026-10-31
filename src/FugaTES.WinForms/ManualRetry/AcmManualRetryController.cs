using System.Collections.Concurrent;
using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.Monitor;
using FugaTES.Aplicacao.Servicos.ManualRetry;

namespace FugaTES.WinForms.ManualRetry;

public interface IAcmManualRetryClient:IAcmManualRetryCommandClient { }

// Installed binding: read-only observation, no command URL, secret, transport or business dispatcher.
public sealed class ReadOnlyAcmManualRetryClient(Func<Guid,CancellationToken,Task<MonitorAcmDetalhe>>? read=null):IAcmManualRetryClient
{
    public bool Configured=>false;
    public Task<AcmRetryPreview> PreviewAsync(ContratoAcmLeitura row,CancellationToken ct)=>BuildPreviewAsync(row,false,ct);
    public Task<AcmRetryPreview> InspectAsync(ContratoAcmLeitura row,CancellationToken ct)=>BuildPreviewAsync(row,true,ct);
    async Task<AcmRetryPreview> BuildPreviewAsync(ContratoAcmLeitura row,bool inspect,CancellationToken ct)
    {
        string code="COMMAND_NOT_HOMOLOGATED",message="Serviço de comando ainda não configurado nesta instalação. A liberação depende do backend de execução autenticado.";
        string? stage=null;
        if(row.FonteDados=="SIMULACAO_CONFIRMADA"||row.NumeroContrato.TrimStart('0')=="986801")
            (code,message)=("SIMULATION","Contrato de simulação: não pode acionar Guardian real.");
        else if(row.EstadoIntegracao=="CONCLUIDA")
            (code,message)=("ALREADY_COMPLETED","Contrato concluído: não reenviar.");
        else if(row.EstadoIntegracao=="RESULTADO_INDETERMINADO")
            (code,message)=("RECONCILIATION_REQUIRED","Resultado indeterminado: reconciliação externa obrigatória; não reenviar.");
        else if(row.EstadoIntegracao is "EM_PROCESSAMENTO" or "PENDENTE" or "AGUARDANDO_REPROCESSAMENTO")
            (code,message)=("BUSY","Integração ativa ou enfileirada: nova tentativa bloqueada.");
        else if(row.FonteDados!="SAP_INBOUND_PROVEN")
            (code,message)=("ORIGIN_UNPROVEN","Origem empresarial SAP não comprovada. É necessária evidência de origem e recuperação; a presença do registro não comprova isso.");
        if(inspect&&read is not null)
        {
            try
            {
                var current=await read(row.IntegracaoId,ct);stage=current.Acm.Protocolo is not null?"PROTOCOLO_VINCULADO":null;
                if(current.Acm.Protocolo is not null)(code,message)=("ALREADY_COMPLETED","Protocolo persistido: não reenviar; conferir correlação no monitor.");
                if(current.Detalhe.Tentativas.Any(t=>t.Desfecho is "INDETERMINADA" or "EM_EXECUCAO"))
                    (code,message)=("RECONCILIATION_REQUIRED","Tentativa ativa ou indeterminada no journal: não reenviar.");
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch{(code,message)=("READ_PREFLIGHT_UNAVAILABLE","Leitura atual de H4/journal indisponível. Nenhum reenvio executado.");}
        }
        return new(row.IntegracaoId,row.NumeroContrato,row.Item,row.EstadoIntegracao,stage,"",new(false,code,message),"");
    }
    public Task<AcmRetryAdmission> SubmitAsync(AcmRetryRequest command,CancellationToken ct)
        =>Task.FromResult(new AcmRetryAdmission("BLOCKED","COMMAND_NOT_HOMOLOGATED",command.CommandId,command.IntegrationId));
}

public sealed class AcmManualRetryController(IAcmManualRetryCommandClient client)
{
    readonly ConcurrentDictionary<Guid,byte> busy=new(),latched=new();
    public bool Configured=>client.Configured;
    public Task<AcmRetryPreview> PreviewAsync(ContratoAcmLeitura row,CancellationToken ct)=>client.PreviewAsync(row,ct);
    public async Task<AcmRetryPreview> InitialPreviewAsync(ContratoAcmLeitura row,CancellationToken ct)
    {
        var preview=await new ReadOnlyAcmManualRetryClient().PreviewAsync(row,ct);
        return client.Configured&&preview.Decision.Code=="COMMAND_NOT_HOMOLOGATED"
            ?preview with{Decision=new(false,"PREFLIGHT_REQUIRED","Clique em Reenviar para consultar a etapa e a elegibilidade atuais no serviço dedicado.")}:preview;
    }
    public async Task<AcmRetryPreview> InspectAsync(ContratoAcmLeitura row,CancellationToken ct)
    {
        if(client is ReadOnlyAcmManualRetryClient local)return await local.InspectAsync(row,ct);
        var preview=await client.PreviewAsync(row,ct);
        if(!preview.Decision.Eligible&&preview.Decision.Code=="COMMAND_NOT_AUTHORIZED")
        {
            var known=await new ReadOnlyAcmManualRetryClient().PreviewAsync(row,ct);
            if(known.Decision.Code is "SIMULATION" or "ALREADY_COMPLETED" or "RECONCILIATION_REQUIRED" or "BUSY" or "ORIGIN_UNPROVEN")
                return preview with{Contract=row.NumeroContrato,Item=row.Item,State=row.EstadoIntegracao,
                    Decision=known.Decision with{Message=known.Decision.Message+" O serviço não possui escopo de recuperação comprovado para esta integração."}};
        }
        return preview;
    }
    public Task<AcmRetryAdmission> SubmitAsync(ContratoAcmLeitura row,AcmRetryPreview expected,string reason,bool confirmed,CancellationToken ct)
        =>SubmitCoreAsync(row,expected,reason,confirmed,AcmRetryStage.None,ct);
    public Task<AcmRetryAdmission> SubmitRestOnlyAsync(ContratoAcmLeitura row,AcmRetryPreview expected,string reason,bool confirmed,CancellationToken ct)
        =>SubmitCoreAsync(row,expected,reason,confirmed,AcmRetryStage.RestOnly,ct);
    async Task<AcmRetryAdmission> SubmitCoreAsync(ContratoAcmLeitura row,AcmRetryPreview expected,string reason,bool confirmed,AcmRetryStage requiredStage,CancellationToken ct)
    {
        AcmRetryAdmission Block(string code)=>new("BLOCKED",code,Guid.Empty,row.IntegracaoId);
        if(!client.Configured)return Block("COMMAND_NOT_HOMOLOGATED");
        if(!confirmed||!AcmManualRetryService.SafeJustification(reason))return Block("CONFIRMATION_OR_REASON_REQUIRED");
        if(latched.ContainsKey(row.IntegracaoId)||!busy.TryAdd(row.IntegracaoId,0))return Block("COMMAND_ALREADY_IN_PROGRESS_NO_RESEND");
        var commandId=Guid.NewGuid();
        try
        {
            var fresh=await client.PreviewAsync(row,ct);
            if(requiredStage!=AcmRetryStage.None&&(expected.Decision.Stage!=requiredStage||fresh.Decision.Stage!=requiredStage))
                return Block("REST_ONLY_SCOPE_REQUIRED");
            if(!fresh.Decision.Eligible||fresh.IntegrationId!=row.IntegracaoId||fresh.Contract!=row.NumeroContrato||fresh.Item!=row.Item||
                fresh.SnapshotDigest!=expected.SnapshotDigest||!AcmManualRetryPolicy.Hash(fresh.SnapshotDigest))return Block("STALE_OR_BLOCKED_PREVIEW");
            var result=await client.SubmitAsync(new(commandId,row.IntegracaoId,fresh.SnapshotDigest,reason,true),ct);
            if(result.IntegrationId!=row.IntegracaoId||result.CommandId!=commandId)
            {latched.TryAdd(row.IntegracaoId,0);return new("UNKNOWN","COMMAND_RECEIPT_MISMATCH_NO_RETRY",commandId,row.IntegracaoId);}
            if(result.State is "QUEUED" or "UNKNOWN" or "ALREADY_RECORDED")latched.TryAdd(row.IntegracaoId,0);
            return result;
        }
        catch{latched.TryAdd(row.IntegracaoId,0);return new("UNKNOWN","COMMAND_OUTCOME_UNVERIFIED_NO_RETRY",commandId,row.IntegracaoId);}
        finally{busy.TryRemove(row.IntegracaoId,out _);}
    }
}
