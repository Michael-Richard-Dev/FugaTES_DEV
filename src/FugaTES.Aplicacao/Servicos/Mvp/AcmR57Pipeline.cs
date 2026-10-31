using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
namespace FugaTES.Aplicacao.Servicos.Mvp;

public sealed partial class AcmR57Pipeline(IContratoSapToledoVersaoStore contratos,IAcmR57Preparacao preparacao,
    IAcmR57RuntimeGate gate,IGuardianContratoSincronizacaoGateway soap,IAcmPreCadastroMinimoGatewayR57 rest,
    IResultadoFluxoMvpStore journal,IAcmGuardianVinculoStore vinculos,IAcmR104CallbackCorrelation? callbackCorrelation=null)
{
    public async Task<ResultadoFluxoMvp> ExecutarAsync(ClaimIntegracaoMotor claim,CancellationToken ct,Action? antesEscrita=null)
    {
        if (preparacao is IAcmR57NormalPreparacao normal)
            return await ExecutarNormalAsync(claim, normal, ct, antesEscrita);
        ct.ThrowIfCancellationRequested();
        ResultadoFluxoMvp Block(string code)=>new(StatusFluxoMvp.AguardandoDependenciaExterna,claim.CorrelationId,"R57_PRE_SEND_BLOCKED",Erro:code,ErroReprocessavel:false);
        ResultadoFluxoMvp Unknown(string code)=>new(StatusFluxoMvp.ResultadoIndeterminado,claim.CorrelationId,"R57_RECONCILIATION_REQUIRED",Erro:code,ErroReprocessavel:false);
        var version=await contratos.ObterPorIntegracaoIdAsync(claim.IntegracaoId,ct);
        if(version is null || claim.Operacao!="SAP_CONTRATO_TOLEDO" || claim.Origem!="SAP" || claim.Destino!="TOLEDO" ||
            version.NumeroContrato!=claim.ChaveNegocio || version.FingerprintSha256!=claim.FingerprintSha256 || claim.FencingToken<=0 ||
            new ContratoSapToledoCanonicalizer().Canonicalizar(version.Snapshot).FingerprintSha256!=version.FingerprintSha256)
            return Block("R57_INBOUND_IDENTITY_INVALID");
        ResultadoFluxoMvp? previous;
        try{await journal.ExigirDisponibilidadeAsync(ct);previous=await journal.ObterAsync(claim,ct);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch{return Block("R57_DURABLE_JOURNAL_UNAVAILABLE");}
        if(previous?.Status is StatusFluxoMvp.FalhaNaoReprocessavel or StatusFluxoMvp.ResultadoIndeterminado)
            return previous;
        var completed=previous?.Status is StatusFluxoMvp.AguardandoCallback or StatusFluxoMvp.Concluido;
        // Only a persisted pre-dispatch block may be retried safely; an armed or unknown stage never is.
        var initialStage=previous?.Etapa;
        if(previous is null && claim.FencingToken>1)
            return new(StatusFluxoMvp.ResultadoIndeterminado,claim.CorrelationId,"R57_REPLAY_UNPROVEN",Erro:"R57_DURABLE_REPLAY_RESULT_MISSING");
        if(previous is not null && !(completed && initialStage=="R57_CONFIRMED") &&
            !(previous.Status==StatusFluxoMvp.AguardandoDependenciaExterna && initialStage=="R57_PRE_SEND_BLOCKED") &&
            !(previous.Status==StatusFluxoMvp.EmProcessamento && initialStage=="R57_SOAP_CONFIRMED"))
            return new(StatusFluxoMvp.ResultadoIndeterminado,claim.CorrelationId,"R57_REPLAY_UNPROVEN",Erro:"R57_DURABLE_STAGE_UNPROVEN");
        // A lost COMMIT acknowledgement is not a rejection and must never authorize dispatch.
        async Task<bool?> Reserve(string? expected,ResultadoFluxoMvp state,CancellationToken token)
        {
            try{return await journal.ReservarEtapaAsync(claim,expected,state,token);}
            catch{return null;}
        }
        // A losing dispatcher must not adopt another request's successful payload without preflight.
        Task<ResultadoFluxoMvp> ReservationFailed(bool? accepted)
            =>Task.FromResult(Unknown(accepted is null?"R57_JOURNAL_COMMIT_UNCONFIRMED":"R57_STAGE_RESERVATION_UNPROVEN"));
        async Task<ResultadoFluxoMvp> PreBlock(string code)
        {
            var blocked=Block(code).Normalizar();
            if(initialStage is null or "R57_PRE_SEND_BLOCKED")
            {
                var accepted=await Reserve(initialStage,blocked,ct);
                if(accepted is true)return blocked;
                // A concurrent dispatcher may have armed the stage. Never overwrite its durable marker.
                return await ReservationFailed(accepted);
            }
            return blocked;
        }
        if(!completed)
        {
            var runtimeBlock=gate.Bloqueio(version);
            if(runtimeBlock is not null)return await PreBlock(runtimeBlock);
            if(await contratos.TemPredecessorNaoTerminalAsync(version.NumeroContrato,version.Versao,ct))return await PreBlock("R57_PREDECESSOR_PENDING");
        }
        AcmR57Plano plan;
        AcmR57Vinculos bindings;
        try{bindings=preparacao.Obter(version);plan=AcmR57Mapping.Criar(version,bindings);}
        catch(AcmR57BlockedException ex){return await PreBlock(ex.Code);}
        if(callbackCorrelation is null)return await PreBlock("R104_CALLBACK_CORRELATION_UNAVAILABLE");
        var digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan))));
        string Metadata(long? sequence)=>"R57:"+(sequence?.ToString(CultureInfo.InvariantCulture)??"")+":"+digest;
        ResultadoFluxoMvp State(StatusFluxoMvp status,string step,long? sequence=null,string? code=null,ProtocoloAcmR57? protocol=null)
            =>new(status,claim.CorrelationId,step,ResultadoFuncional:Metadata(sequence),Erro:code,
                ProtocoloGuardian:protocol?.Id.ToString(CultureInfo.InvariantCulture),NumeroTicketGuardian:protocol?.NumeroTicket,
                ErroReprocessavel:code is null?null:false);
        var soapWasConfirmed=initialStage=="R57_SOAP_CONFIRMED";
        if((soapWasConfirmed || completed) && (previous!.ResultadoFuncional is null || !previous.ResultadoFuncional.EndsWith(":"+digest,StringComparison.Ordinal)))
            return Block("R57_REPLAY_MAPPING_CHANGED");
        if(completed)
        {
            var marker=previous!.ResultadoFuncional!.Split(':');
            if(marker.Length!=3 || marker[0]!="R57" || marker[2]!=digest ||
                (marker[1].Length>0 && (!long.TryParse(marker[1],NumberStyles.None,CultureInfo.InvariantCulture,out var parsed)||parsed<=0)))
                return Unknown("R57_DURABLE_STAGE_UNPROVEN");
        }
        ResultadoFluxoMvp? CheckLink(AcmGuardianVinculo? current)
        {
            if(current is null)return completed?Unknown("R57_CONFIRMED_PROTOCOL_LINK_MISSING"):null;
            if(current.ProtocoloIdGuardian==3)return Unknown("R104_FORBIDDEN_PROTOCOL_ID");
            if(current.IntegracaoIdOrigem!=version.IntegracaoId || current.NumeroContratoSap!=version.NumeroContrato || current.VersaoH4!=version.Versao ||
                current.NumeroReferenciaSolicitado!=plan.Protocolo.Dados.NumeroReferencia || current.CodigoContratoSolicitado!=plan.Contrato.Codigo ||
                (current.CodigoContratoConfirmado is not null && current.CodigoContratoConfirmado!=plan.Contrato.Codigo))
                return Unknown("R57_DURABLE_LINK_IDENTITY_MISMATCH");
            if(completed)
            {
                if(current.ProtocoloIdGuardian is not >0 || current.CodigoContratoConfirmado!=plan.Contrato.Codigo ||
                    previous!.ProtocoloGuardian!=current.ProtocoloIdGuardian.Value.ToString(CultureInfo.InvariantCulture) ||
                    previous.NumeroTicketGuardian!=current.NumeroTicketGuardian)
                    return Unknown("R57_CONFIRMED_PROTOCOL_LINK_MISMATCH");
                return previous;
            }
            // Even without a journal, a pre-existing or partially correlated protocol is not a fresh request.
            if(current.ProtocoloIdGuardian is not null || current.NumeroTicketGuardian is not null || current.CallbackIdGuardian is not null ||
                current.CallbackSequencialGuardian is not null || current.CallbackFingerprint is not null)
                return Unknown("R57_EXISTING_PROTOCOL_RECONCILIATION_REQUIRED");
            return null;
        }
        AcmGuardianVinculo? existing;
        try{existing=await vinculos.ObterPorIntegracaoAsync(version.IntegracaoId,ct);}
        catch{return Unknown("R57_DURABLE_LINK_PREFLIGHT_UNAVAILABLE");}
        var preflight=CheckLink(existing);
        if(preflight is not null && (!completed || !ReferenceEquals(preflight,previous)))return preflight;
        // This read/write preparation happens before even a SOAP lookup. The callback's parent
        // snapshot is reserved now; its protocol is associated only after the actual REST response.
        try{await callbackCorrelation.PrepararAsync(version,plan,bindings.CorrelationBinding,completed?existing?.ProtocoloIdGuardian:null,ct);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(AcmR57BlockedException ex){return completed?Unknown(ex.Code):await PreBlock(ex.Code);}
        catch{return completed?Unknown("R104_CALLBACK_PREFLIGHT_UNCERTAIN"):await PreBlock("R104_CALLBACK_PREFLIGHT_UNAVAILABLE");}
        if(preflight is not null)return preflight;
        AcmGuardianVinculo link;
        try{link=await vinculos.ReservarSolicitacaoUnicaAsync(version,plan.Protocolo.Dados.NumeroReferencia,plan.Contrato.Codigo,ct);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch{return await PreBlock("R57_DURABLE_REFERENCE_RESERVATION_FAILED");}
        // Recheck the result of the durable reservation to close the preflight/reservation race.
        var reservedLink=CheckLink(link);
        if(reservedLink is not null)return reservedLink;
        long? sequence=null;
        if(soapWasConfirmed)
        {
            var parts=previous!.ResultadoFuncional?.Split(':');
            if(parts is null || parts.Length!=3 || parts[0]!="R57" || link.CodigoContratoConfirmado!=plan.Contrato.Codigo)
                return Block("R57_CONFIRMED_CONTRACT_UNPROVEN");
            if(parts[1].Length>0){if(!long.TryParse(parts[1],out var parsed)||parsed<=0)return Block("R57_CONTRACT_SEQUENCE_UNPROVEN");sequence=parsed;}
        }
        else
        {
            ConsultaContratoGuardian lookup;
            try{lookup=await soap.ConsultarAsync(plan.Contrato.Codigo,ct);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch{return await PreBlock("R57_SOAP_LOOKUP_UNPROVEN");}
            if(lookup.Estado==EstadoConsultaContratoGuardian.Existente)
            {
                if(lookup.Contrato is null || !SincronizarContratoGuardian.Identicos(lookup.Contrato,plan.Contrato))
                    return await PreBlock("R57_CONTRACT_RECONCILIATION_REQUIRED");
                sequence=lookup.SequencialContrato;
                if(sequence is <=0)return await PreBlock("R57_CONTRACT_SEQUENCE_UNPROVEN");
                var accepted=await Reserve(initialStage,State(StatusFluxoMvp.EmProcessamento,"R57_SOAP_CONFIRMED",sequence).Normalizar(),ct);
                if(accepted is not true)return await ReservationFailed(accepted);
                try{await vinculos.RegistrarCodigoConfirmadoAsync(version.IntegracaoId,plan.Contrato.Codigo,ct);}
                catch{var unknown=State(StatusFluxoMvp.ResultadoIndeterminado,"R57_SOAP_UNCONFIRMED",sequence,"R57_CONTRACT_IDENTITY_NOT_PERSISTED").Normalizar();await Save("R57_SOAP_CONFIRMED",unknown);return unknown;}
            }
            else
            {
                // User-attested manual registration: 1031 must only be reconciled, never created again.
                if(plan.Contrato.Codigo.TrimStart('0')=="1031")return await PreBlock("R57_KNOWN_CREATED_CONTRACT_RECONCILIATION_REQUIRED");
                if(lookup.Estado!=EstadoConsultaContratoGuardian.InexistenteComprovado || lookup.Contrato is not null)
                    return await PreBlock("R57_SOAP_ABSENCE_UNPROVEN");
                ct.ThrowIfCancellationRequested();
                var armed=State(StatusFluxoMvp.ResultadoIndeterminado,"R57_SOAP_ARMED",code:"R57_SOAP_DISPATCH_OUTCOME_UNKNOWN").Normalizar();
                var accepted=await Reserve(initialStage,armed,ct);
                if(accepted is not true)return await ReservationFailed(accepted);
                antesEscrita?.Invoke();
                ResultadoContratoGuardianComResposta created;
                try{created=await soap.CadastrarComRespostaAsync(plan.Contrato,ct);}
                catch(Exception ex) when(ex is IAcmGuardianBusinessFailure{BusinessErrorCode:not null and not 0})
                {
                    var failed=State(StatusFluxoMvp.FalhaNaoReprocessavel,"R57_SOAP_FAILED",code:"R57_SOAP_BUSINESS_REJECTED").Normalizar();
                    return await Save("R57_SOAP_ARMED",failed)?failed:armed;
                }
                catch{return armed;}
                if(created.CodigoContratoConfirmado!=plan.Contrato.Codigo || created.SequencialContrato is <=0)return armed;
                sequence=created.SequencialContrato;
                try
                {
                    using var identityTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await vinculos.RegistrarCodigoConfirmadoAsync(version.IntegracaoId,created.CodigoContratoConfirmado,identityTimeout.Token);
                    var confirmed=State(StatusFluxoMvp.EmProcessamento,"R57_SOAP_CONFIRMED",sequence).Normalizar();
                    if(!await Save("R57_SOAP_ARMED",confirmed))return armed;
                }
                catch{return armed;}
            }
        }
        ct.ThrowIfCancellationRequested();
        var restArmed=State(StatusFluxoMvp.ResultadoIndeterminado,"R57_REST_ARMED",sequence,"R57_REST_DISPATCH_OUTCOME_UNKNOWN").Normalizar();
        var restAccepted=await Reserve("R57_SOAP_CONFIRMED",restArmed,ct);
        if(restAccepted is not true)return await ReservationFailed(restAccepted);
        antesEscrita?.Invoke();
        try
        {
            var protocol=await rest.EnviarMinimoAcmAsync(plan.Protocolo,ct);
            if(protocol.Id==3 || !AcmR57Mapping.RespostaConfere(protocol,plan.Protocolo))return restArmed;
            using var identityTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await vinculos.RegistrarProtocoloConfirmadoAsync(version.IntegracaoId,protocol.Id,protocol.NumeroTicket,identityTimeout.Token);
            await callbackCorrelation.ConfirmarAsync(version,plan,bindings.CorrelationBinding,protocol,identityTimeout.Token);
            var confirmed=State(StatusFluxoMvp.AguardandoCallback,"R57_CONFIRMED",sequence,protocol:protocol).Normalizar();
            return await Save("R57_REST_ARMED",confirmed)?confirmed:restArmed;
        }
        catch{return restArmed;}

        async Task<bool> Save(string expected,ResultadoFluxoMvp state)
        {
            using var bounded=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            return await Reserve(expected,state,bounded.Token) is true;
        }
    }
}
