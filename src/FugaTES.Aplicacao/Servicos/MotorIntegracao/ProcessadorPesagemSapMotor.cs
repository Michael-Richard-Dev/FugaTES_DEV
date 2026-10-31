using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Servicos.Mvp;

namespace FugaTES.Aplicacao.Servicos.MotorIntegracao;

public sealed class ProcessadorPesagemSapMotor(
    IRepositorioCorrelacaoRecebimento correlacoes,
    IPesagemSapMvpGateway sap,IAcmR104EffectiveCarrierSource? effectiveCarrierSource=null,
    IAcmR106SapReturnPreflight? preflight=null,
    IAcmSapTicketEvidenceSource? ticketEvidenceSource=null,
    Action<string>? reportBlockedReason=null,
    IAcmSapTicketEvidenceStore? ticketEvidenceStore=null) : IProcessadorExternoIntegracao
{
    public async Task<ResultadoProcessamentoExterno> ProcessarAsync(
        ClaimIntegracaoMotor claim, CancellationToken cancellationToken)
    {
        if (claim.Operacao != GuardianPesagemSapMotorWork.Operacao || claim.Origem != "GUARDIAN" ||
            claim.Destino != "SAP" || string.IsNullOrWhiteSpace(claim.ChaveNegocio))
            return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);

        CorrelacaoRecebimentoMvp? correlacao;
        try
        {
            correlacao = await correlacoes.ObterPorProtocoloAsync(claim.ChaveNegocio, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ClassificacaoFalhaIntegracao.FalhaTransitoriaSegura);
        }

        if (correlacao is not null && string.IsNullOrWhiteSpace(correlacao.Entrada.ContratoAcmSap))
        {
            reportBlockedReason?.Invoke("GUARDIAN_TICKET_ACM_CONTRACT_REQUIRED");
            return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);
        }

        if (correlacao is null || correlacao.ProtocoloGuardian != claim.ChaveNegocio ||
            string.IsNullOrWhiteSpace(correlacao.CallbackFingerprint) ||
            claim.FingerprintSha256 != GuardianPesagemSapMotorWork.Fingerprint(
                claim.ChaveNegocio, correlacao.CallbackFingerprint) ||
            correlacao.Pesos is null || string.IsNullOrWhiteSpace(correlacao.TicketGuardian) ||
            string.IsNullOrWhiteSpace(correlacao.Entrada.ContratoAcmSap) ||
            string.IsNullOrWhiteSpace(correlacao.Entrada.Centro) ||
            string.IsNullOrWhiteSpace(correlacao.Entrada.Material) ||
            (!AcmR106SapReturnPreflight.RequiresAcmGuards(correlacao.Entrada) &&
             string.IsNullOrWhiteSpace(correlacao.Entrada.BusinessPartnerTransportadora)) ||
            string.IsNullOrWhiteSpace(correlacao.Entrada.BusinessPartnerEmissor))
            return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);

        var entrada = correlacao.Entrada;
        var pesos = correlacao.Pesos;
        var isAcm=AcmR106SapReturnPreflight.RequiresAcmGuards(entrada);
        string? carrier=isAcm ? null : entrada.BusinessPartnerTransportadora;
        int? appointment=isAcm ? null : 1;
        if(isAcm)
        {
            if (entrada.Centro != "3011" || entrada.TipoContrato != "AC3P" ||
                claim.CorrelationId != correlacao.DocumentoSap ||
                !AcmR106SapReturnPreflight.EventoFinalComprovado(correlacao) || preflight is null)
                return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);
            try
            {
                if (!await preflight.ValidarAsync(correlacao, cancellationToken))
                    return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva); }
            // A provisional carrier on the original request is never the final weighing authority.
            if(effectiveCarrierSource is not null)
            {
            AcmR104EffectiveCarrier? actual;
            try{actual=await effectiveCarrierSource.ObterAsync(correlacao,cancellationToken);}
            catch{return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);}
            if(actual is not null && (!actual.IdentityConfirmedForTicket || actual.Protocolo!=correlacao.ProtocoloGuardian ||
                actual.Ticket!=correlacao.TicketGuardian || actual.CallbackFingerprint!=correlacao.CallbackFingerprint ||
                string.IsNullOrWhiteSpace(actual.SapBusinessPartner) || actual.SapBusinessPartner.Length>10 ||
                string.IsNullOrWhiteSpace(actual.EvidenceReference)))
                return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);
            carrier=actual?.SapBusinessPartner;
            }
        }
        AcmSapTicketEvidence? ticketEvidence=null;
        if(isAcm)
        {
            // Correlation and R106 guards are mandatory; a missing final ticket
            // evidence source never falls back to the SAP example or the H4 contract.
            if(ticketEvidenceSource is null)return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);
            try { ticketEvidence=await ticketEvidenceSource.ObterAsync(correlacao,cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva); }
            if(ticketEvidence is null || string.IsNullOrWhiteSpace(ticketEvidence.EvidenceReference) ||
                ticketEvidence.ProtocoloGuardian!=correlacao.ProtocoloGuardian ||
                ticketEvidence.TicketGuardian!=correlacao.TicketGuardian ||
                ticketEvidence.CallbackFingerprint!=correlacao.CallbackFingerprint ||
                ticketEvidence.ContratoACM!=entrada.ContratoAcmSap ||
                ticketEvidence.Centro!=entrada.Centro || ticketEvidence.Material!=entrada.Material ||
                ticketEvidence.BusinessPartnerContrato!=entrada.BusinessPartnerEmissor ||
                ticketEvidence.TipoOperacaoSap is not > 0 ||
                AcmSapWeighingDirection.ToSap(ticketEvidence.TipoPesagem) is null)
                return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);
        }
        var pesagem = new PesagemSapMvp("Toledo", entrada.Centro, isAcm ? AcmSapWeighingDirection.ToSap(ticketEvidence!.TipoPesagem)! : "E", correlacao.TicketGuardian, appointment,
            carrier, entrada.Material, entrada.BusinessPartnerEmissor,
            pesos.PesoBruto, pesos.Tara, pesos.PesoLiquido, entrada.TipoDocumento,
            entrada.PedidoCompras, entrada.DocumentoLogisticoReferencia, isAcm ? ticketEvidence!.TipoOperacaoSap!.Value : entrada.TipoOperacaoSap,
            entrada.ContratoAcmSap!, correlacao.ProtocoloGuardian, correlacao.ProtocoloCriadoEm, ticketEvidence);

        try
        {
            if(isAcm)
            {
                if(ticketEvidenceStore is null)
                    return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);
                // Persistence must complete before any gateway/CSRF/POST side effect.
                try { await ticketEvidenceStore.PreservarAsync(correlacao,pesagem,cancellationToken); }
                catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){throw;}
                catch { return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva); }
            }
            await sap.EnviarAsync(pesagem, cancellationToken);
            return ResultadoProcessamentoExterno.Sucesso;
        }
        catch (FalhaPesagemSapMvpException exception)
        {
            // ACM failures require manual action. Only an uncertain outcome retains INDETERMINATE;
            // a gateway's transient classification must not schedule a second SAP POST automatically.
            return new(isAcm && exception.Classificacao!=ClassificacaoFalhaIntegracao.ResultadoIndeterminado
                ?ClassificacaoFalhaIntegracao.FalhaDefinitiva:exception.Classificacao);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ClassificacaoFalhaIntegracao.ResultadoIndeterminado);
        }
    }
}
