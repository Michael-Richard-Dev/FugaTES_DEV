using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.Aplicacao.Consultas.ProtocolosGuardian;
using FugaTES.Aplicacao.Servicos.ManualRetry;

namespace FugaTES.WinForms.ManualRetry;

public sealed record ProtocolRestRetryPreview(ProtocoloPreCadastroLinha Protocol,ContratoAcmLeitura? Contract,AcmRetryPreview Command);

// UI orchestration only: the existing authenticated command service owns origin proof,
// correction evidence, unit approval, transaction, durable audit, fencing and the worker.
public sealed class ProtocolRestRetryController(AcmManualRetryController command,
    IProtocolosPreCadastroReadRepository protocols,IContratosAcmReadRepository contracts)
{
    public bool Configured=>command.Configured;
    public static string? ProjectionBlockReason(ProtocoloPreCadastroLinha row)
    {
        if(row.ProtocoloCriado||row.EstadoMotor=="CONCLUIDA")return "Protocolo já confirmado: não reenviar.";
        if(row.Origem==ProtocoloPreCadastroLinha.Simulada||row.Contrato?.TrimStart('0')=="986801")return "Contrato sintético: não aciona Guardian real.";
        if(row.ResultadoIndeterminado||row.EstadoMotor=="RESULTADO_INDETERMINADO")return "Resultado indeterminado: reconciliação obrigatória, sem reenvio.";
        if(row.EstadoMotor is "PENDENTE" or "EM_PROCESSAMENTO" or "AGUARDANDO_REPROCESSAMENTO" ||
            (row.ProcessamentoAtivo&&row.EstadoMotor!="AGUARDANDO_ACAO_MANUAL"))return "Integração ativa ou enfileirada: não reenviar.";
        if(row.IntegracaoId==Guid.Empty||!AcmManualRetryPolicy.Evidence(row.Contrato)||
            !AcmManualRetryPolicy.Evidence(row.CorrelationId)||!AcmManualRetryPolicy.Evidence(row.Referencia)||row.CorrelacaoAmbigua)
            return "Identidade, referência ou correlação durável incompleta/divergente.";
        if(!row.ContratoConfirmado)return "Checkpoint SOAP confirmado obrigatório; esta ação não executa SOAP.";
        return null;
    }
    static ProtocolRestRetryPreview Block(ProtocoloPreCadastroLinha row,string code,string message)=>new(row,null,
        new(row.IntegracaoId,row.Contrato??"",row.Item,row.EstadoMotor,null,"",new(false,code,message),""));
    static bool SameIdentity(ProtocoloPreCadastroLinha a,ProtocoloPreCadastroLinha b)=>a.IntegracaoId==b.IntegracaoId&&
        a.Contrato==b.Contrato&&a.CorrelationId==b.CorrelationId&&a.Referencia==b.Referencia&&a.Item==b.Item;
    public async Task<ProtocolRestRetryPreview> InspectAsync(ProtocoloPreCadastroLinha selected,CancellationToken ct)
    {
        if(ProjectionBlockReason(selected) is { } reason)return Block(selected,"PROTOCOL_RETRY_UNSAFE",reason);
        if(!Configured)return Block(selected,"COMMAND_NOT_HOMOLOGATED","Serviço de comando não configurado nesta sessão.");
        try
        {
            var matches=(await protocols.ConsultarAsync(ct)).Where(r=>r.IntegracaoId==selected.IntegracaoId).ToArray();
            if(matches.Length!=1||!SameIdentity(selected,matches[0]))return Block(selected,"PROTOCOL_IDENTITY_CHANGED","Referência/correlação alterada ou integração ausente/ambígua. Atualize a grade.");
            var current=matches[0];
            if(ProjectionBlockReason(current) is { } currentReason)return Block(current,"PROTOCOL_RETRY_UNSAFE",currentReason);
            var h4=(await contracts.ConsultarAsync(ContratosAcmConsulta.CentroInicial,ct)).Where(r=>r.IntegracaoId==current.IntegracaoId).ToArray();
            if(h4.Length!=1||h4[0].NumeroContrato!=current.Contrato||h4[0].CorrelationId!=current.CorrelationId||
                !AcmManualRetryPolicy.Evidence(h4[0].Item)||(current.Item is not null&&current.Item!=h4[0].Item)||
                h4[0].FonteDados=="SIMULACAO_CONFIRMADA")return Block(current,"PROTOCOL_H4_IDENTITY_UNPROVEN","Exatamente um H4 original com contrato, item e correlação correspondentes é necessário.");
            var preview=await command.InspectAsync(h4[0],ct);
            if(preview.Decision.Eligible&&(preview.Decision.Stage!=AcmRetryStage.RestOnly||
                preview.PendingStage is not("R165U_SOAP_CONFIRMED" or "R165U_PROTOCOL_BLOCKED" or "R165Y_REST_REJECTED")||
                preview.IntegrationId!=current.IntegracaoId||preview.Contract!=h4[0].NumeroContrato||preview.Item!=h4[0].Item||
                !AcmManualRetryPolicy.Hash(preview.SnapshotDigest)))
                preview=preview with{Decision=new(false,"REST_ONLY_SCOPE_REQUIRED","Esta ação aceita somente recuperação REST com checkpoint SOAP e identidade comprovados.")};
            // An unattested projection is NEVER promoted to REAL. Only the server can prove business origin,
            // correct a 400 classification or authorize the original immutable H4 snapshot.
            return new(current,h4[0],preview);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch{return Block(selected,"PROTOCOL_PREFLIGHT_UNAVAILABLE","Não foi possível conferir H4, journal e correlação atuais. Nenhum comando enviado.");}
    }
    public async Task<AcmRetryAdmission> SubmitAsync(ProtocolRestRetryPreview expected,string reason,bool confirmed,CancellationToken ct)
    {
        AcmRetryAdmission Denied(string code)=>new("BLOCKED",code,Guid.Empty,expected.Protocol.IntegracaoId);
        if(!confirmed||!AcmManualRetryService.SafeJustification(reason))return Denied("CONFIRMATION_OR_REASON_REQUIRED");
        if(!expected.Command.Decision.Eligible||expected.Command.Decision.Stage!=AcmRetryStage.RestOnly||expected.Contract is null)
            return Denied("REST_ONLY_SCOPE_REQUIRED");
        var fresh=await InspectAsync(expected.Protocol,ct);
        if(!fresh.Command.Decision.Eligible||fresh.Contract is null||fresh.Command.SnapshotDigest!=expected.Command.SnapshotDigest||
            fresh.Contract.NumeroContrato!=expected.Contract.NumeroContrato||fresh.Contract.Item!=expected.Contract.Item)
            return Denied("STALE_OR_BLOCKED_PREVIEW");
        return await command.SubmitRestOnlyAsync(fresh.Contract,fresh.Command,reason,true,ct);
    }
}
