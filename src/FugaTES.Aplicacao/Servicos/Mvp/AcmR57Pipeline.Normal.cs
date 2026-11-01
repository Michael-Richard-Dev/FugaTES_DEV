using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;

namespace FugaTES.Aplicacao.Servicos.Mvp;

public sealed partial class AcmR57Pipeline
{
    private async Task<ResultadoFluxoMvp> ExecutarNormalAsync(ClaimIntegracaoMotor claim,
        IAcmR57NormalPreparacao normal, CancellationToken ct, Action? antesEscrita)
    {
        const string pre = "R165U_PRE_SEND_BLOCKED", soapArmed = "R165U_SOAP_ARMED",
            soapDone = "R165U_SOAP_CONFIRMED", protocolBlocked = "R165U_PROTOCOL_BLOCKED",
            restArmed = "R165U_REST_ARMED", done = "R165U_CONFIRMED";
        ct.ThrowIfCancellationRequested();
        ResultadoFluxoMvp Block(string code, string stage = pre, string? metadata = null) =>
            new ResultadoFluxoMvp(StatusFluxoMvp.AguardandoDependenciaExterna, claim.CorrelationId, stage,
                Erro: code, ResultadoFuncional: metadata, ErroReprocessavel: false).Normalizar();
        ResultadoFluxoMvp Unknown(string code) => new ResultadoFluxoMvp(StatusFluxoMvp.ResultadoIndeterminado,
            claim.CorrelationId, "R165U_RECONCILIATION_REQUIRED", Erro: code, ErroReprocessavel: false).Normalizar();
        async Task<bool> Save(string? expected, ResultadoFluxoMvp value, CancellationToken? receiptToken = null)
        {
            try { return await journal.ReservarEtapaAsync(claim, expected, value.Normalizar(), receiptToken ?? ct); }
            catch { return false; }
        }
        static string Digest(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
        static bool Hash(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiHexDigit(c) && !char.IsLower(c));
        static bool HasExternalReceipt(AcmGuardianVinculo? link) => link is not null &&
            (link.ProtocoloIdGuardian is not null || link.NumeroTicketGuardian is not null ||
             link.CallbackIdGuardian is not null || link.CallbackSequencialGuardian is not null || link.CallbackFingerprint is not null);
        var version = await contratos.ObterPorIntegracaoIdAsync(claim.IntegracaoId, ct);
        if (version is null || version.IntegracaoId != claim.IntegracaoId || version.NumeroContrato != claim.ChaveNegocio ||
            claim.Operacao != "SAP_CONTRATO_TOLEDO" || claim.Origem != "SAP" || claim.Destino != "TOLEDO" ||
            version.NumeroContrato != version.Snapshot.NumeroContrato || version.Versao <= 0 || claim.FencingToken <= 0 ||
            version.FingerprintSha256 != claim.FingerprintSha256 ||
            new ContratoSapToledoCanonicalizer().Canonicalizar(version.Snapshot).FingerprintSha256 != version.FingerprintSha256)
            return Block("R165U_INBOUND_IDENTITY_INVALID");
        ResultadoFluxoMvp? previous;
        try { await journal.ExigirDisponibilidadeAsync(ct); previous = await journal.ObterAsync(claim, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Block("R165U_DURABLE_JOURNAL_UNAVAILABLE"); }
        if (previous?.Status is StatusFluxoMvp.ResultadoIndeterminado or StatusFluxoMvp.FalhaNaoReprocessavel)
            return previous;
        var stage = previous?.Etapa;
        if (previous is null && claim.FencingToken > 1) return Unknown("R165U_REPLAY_HISTORY_MISSING");
        if (stage is not null && stage != pre && stage != soapDone && stage != protocolBlocked && stage != done)
            return Unknown("R165U_DURABLE_STAGE_UNPROVEN");
        async Task<ResultadoFluxoMvp> PreBlock(string code)
        {
            var result = Block(code);
            if (stage is null or pre)
                return await Save(stage, result) ? result : Unknown("R165U_FENCED_COMMIT_UNPROVEN");
            return result;
        }
        var runtimeBlock = await AcmR57DynamicAdmission.BloqueioAsync(gate,preparacao,version,ct);
        if (runtimeBlock is not null) return await PreBlock(runtimeBlock);
        if (!gate.CorrelationPermitida(claim.CorrelationId)) return await PreBlock("R165AG_ADMISSION_MARKER_REQUIRED");
        if (await contratos.TemPredecessorNaoTerminalAsync(version.NumeroContrato, version.Versao, ct))
            return await PreBlock("R165U_PREDECESSOR_PENDING");
        AcmR57NormalContract contract;
        try { contract = normal.PrepararContrato(version); }
        catch (AcmR57BlockedException ex) { return await PreBlock(ex.Code); }
        var contractDigest = Digest(contract);
        long? sequence = null;
        string protocolDigest = "";
        string Metadata() => "U:" + contractDigest + ":" + protocolDigest + ":" + sequence?.ToString(CultureInfo.InvariantCulture);
        ResultadoFluxoMvp State(StatusFluxoMvp status, string step, string? error = null, ProtocoloAcmR57? protocol = null) =>
            new ResultadoFluxoMvp(status, claim.CorrelationId, step, ResultadoFuncional: Metadata(), Erro: error,
                ProtocoloGuardian: protocol?.Id.ToString(CultureInfo.InvariantCulture), NumeroTicketGuardian: protocol?.NumeroTicket,
                ErroReprocessavel: error is null ? null : false).Normalizar();
        var confirmedContract = stage is soapDone or protocolBlocked or done;
        if (confirmedContract)
        {
            var pieces = previous!.ResultadoFuncional?.Split(':');
            if (pieces is null || pieces.Length != 4 || pieces[0] != "U" || pieces[1] != contractDigest ||
                (stage == done ? !Hash(pieces[2]) : pieces[2].Length != 0))
                return Unknown("R165U_MAPPING_OR_STAGE_CONFLICT");
            protocolDigest = pieces[2];
            if (pieces[3].Length > 0)
            {
                if (!long.TryParse(pieces[3], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
                    return Unknown("R165U_CONTRACT_SEQUENCE_UNPROVEN");
                sequence = value;
            }
        }
        var h4 = new AcmR57H4CorrelationAdapter(vinculos);
        AcmGuardianVinculo? existing;
        try
        {
            existing = await vinculos.ObterPorIntegracaoAsync(version.IntegracaoId, ct);
            if (existing is not null) AcmR57H4CorrelationAdapter.Validate(version, contract, existing);
        }
        catch { return Unknown("R165U_H4_PREFLIGHT_UNVERIFIED"); }
        if (stage == done)
        {
            if (previous!.Status != StatusFluxoMvp.AguardandoCallback || existing is null ||
                existing.CodigoContratoConfirmado != contract.Contrato.Codigo || existing.ProtocoloIdGuardian is not > 0 ||
                previous.ProtocoloGuardian != existing.ProtocoloIdGuardian.Value.ToString(CultureInfo.InvariantCulture) ||
                previous.NumeroTicketGuardian != existing.NumeroTicketGuardian)
                return Unknown("R165U_CONFIRMED_PROTOCOL_LINK_MISMATCH");
            return previous;
        }
        if (HasExternalReceipt(existing))
            return Unknown("R165U_EXISTING_PROTOCOL_RECONCILIATION_REQUIRED");
        if (confirmedContract && existing?.CodigoContratoConfirmado != contract.Contrato.Codigo)
            return Unknown("R165U_CONFIRMED_CONTRACT_LINK_MISSING");
        if (!confirmedContract && existing?.CodigoContratoConfirmado is not null)
            return Unknown("R165U_CONTRACT_HISTORY_RECONCILIATION_REQUIRED");
        AcmGuardianVinculo link;
        try { link = await h4.ReserveAsync(version, contract, ct); }
        catch { return await PreBlock("R165U_H4_REFERENCE_RESERVATION_FAILED"); }
        // Reservation readback also closes the reference-reservation race.
        if (HasExternalReceipt(link) ||
            (!confirmedContract && link.CodigoContratoConfirmado is not null))
            return Unknown("R165U_CONCURRENT_LINK_RECONCILIATION_REQUIRED");
        if (!confirmedContract)
        {
            ConsultaContratoGuardian lookup;
            try { lookup = await soap.ConsultarAsync(contract.Contrato.Codigo, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return await PreBlock("R165U_SOAP_LOOKUP_UNPROVEN"); }
            if (lookup.Estado == EstadoConsultaContratoGuardian.Existente)
            {
                if (gate.ExigeContratoNovo)
                    return await PreBlock("R165AG_EXISTING_CONTRACT_RECONCILIATION_REQUIRED");
                if (lookup.Contrato is null || !SincronizarContratoGuardian.Identicos(lookup.Contrato, contract.Contrato) || lookup.SequencialContrato is <= 0)
                    return await PreBlock("R165U_EXISTING_CONTRACT_RECONCILIATION_REQUIRED");
                sequence = lookup.SequencialContrato;
                // Reserve an uncertain stage even for the read-only adoption: no other dispatcher may race the identity commit.
                var adoption = State(StatusFluxoMvp.ResultadoIndeterminado, soapArmed, "R165U_CONTRACT_LINK_COMMIT_UNKNOWN");
                if (!await Save(stage, adoption)) return Unknown("R165U_FENCED_COMMIT_UNPROVEN");
                try
                {
                    using var capture = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var confirmed = await vinculos.RegistrarCodigoConfirmadoAsync(version.IntegracaoId, contract.Contrato.Codigo, capture.Token);
                    AcmR57H4CorrelationAdapter.Validate(version, contract, confirmed);
                    if (confirmed.CodigoContratoConfirmado != contract.Contrato.Codigo ||
                        !await Save(soapArmed, State(StatusFluxoMvp.EmProcessamento, soapDone), capture.Token)) return adoption;
                }
                catch { return adoption; }
            }
            else
            {
                if (lookup.Estado != EstadoConsultaContratoGuardian.InexistenteComprovado || lookup.Contrato is not null)
                    return await PreBlock("R165U_GUARDIAN_ABSENCE_UNPROVEN");
                if (contract.Contrato.Codigo.TrimStart('0') == "1031")
                    return await PreBlock("R165U_KNOWN_CONTRACT_RECONCILIATION_REQUIRED");
                runtimeBlock = gate.Bloqueio(version);
                if (runtimeBlock is not null) return await PreBlock(runtimeBlock);
                var armed = State(StatusFluxoMvp.ResultadoIndeterminado, soapArmed, "R165U_SOAP_OUTCOME_UNKNOWN");
                if (!await Save(stage, armed)) return Unknown("R165U_FENCED_COMMIT_UNPROVEN");
                ct.ThrowIfCancellationRequested();
                antesEscrita?.Invoke();
                runtimeBlock = gate.Bloqueio(version);
                if (runtimeBlock is not null)
                {
                    var blocked = Block(runtimeBlock);
                    return await Save(soapArmed, blocked) ? blocked : Unknown("R165U_FENCED_COMMIT_UNPROVEN");
                }
                ResultadoContratoGuardianComResposta response;
                try { response = await soap.CadastrarComRespostaAsync(contract.Contrato, ct); }
                catch (Exception ex) when (ex is IAcmGuardianBusinessFailure { BusinessErrorCode: not null and not 0 })
                {
                    using var capture = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var failed = State(StatusFluxoMvp.FalhaNaoReprocessavel, "R165U_SOAP_FAILED", "R165U_SOAP_BUSINESS_REJECTED");
                    return await Save(soapArmed, failed, capture.Token) ? failed : armed;
                }
                catch { return armed; }
                if (response.CodigoContratoConfirmado != contract.Contrato.Codigo || response.SequencialContrato is <= 0) return armed;
                sequence = response.SequencialContrato;
                try
                {
                    // Capture a known external identity even if the sending token was cancelled.
                    // The journal still enforces the original claim/lease/fencing at commit time.
                    using var capture = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var confirmed = await vinculos.RegistrarCodigoConfirmadoAsync(version.IntegracaoId, response.CodigoContratoConfirmado, capture.Token);
                    AcmR57H4CorrelationAdapter.Validate(version, contract, confirmed);
                    if (confirmed.CodigoContratoConfirmado != contract.Contrato.Codigo ||
                        !await Save(soapArmed, State(StatusFluxoMvp.EmProcessamento, soapDone), capture.Token)) return armed;
                }
                catch { return armed; }
            }
            stage = soapDone;
        }
        ct.ThrowIfCancellationRequested();
        // Revalidate the persisted link at the SOAP -> REST boundary. A receipt that appeared
        // during reservation/SOAP is evidence of a concurrent or unexplained external effect,
        // never permission to create another protocol. Keep the existing claim/fenced journal.
        try
        {
            var beforeRest = await h4.ReadAsync(version, contract, ct);
            if (beforeRest.CodigoContratoConfirmado != contract.Contrato.Codigo || HasExternalReceipt(beforeRest))
                return Unknown("R165U_REST_BOUNDARY_RECONCILIATION_REQUIRED");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Unknown("R165U_REST_BOUNDARY_LINK_UNVERIFIED"); }
        // A server-owned nominal SOAP-only approval never grants protocol dispatch, even
        // if callback/global configuration would otherwise permit it. Preserve confirmed
        // contract identity and its sequence in the same fenced durable journal.
        if (normal.SomenteCadastroSoap(version))
        {
            var pending = Block("ACM_MANUAL_SOAP_ONLY_PROTOCOL_NOT_AUTHORIZED", protocolBlocked, Metadata());
            return await Save(stage, pending) ? pending : Unknown("R165U_FENCED_COMMIT_UNPROVEN");
        }
        PreCadastroAcmR57 request;
        try { request = normal.PrepararProtocolo(version, contract); }
        catch (AcmR57BlockedException ex)
        {
            var blocked = Block(ex.Code, protocolBlocked, Metadata());
            return await Save(stage, blocked) ? blocked : Unknown("R165U_FENCED_COMMIT_UNPROVEN");
        }
        protocolDigest = Digest(request);
        runtimeBlock = gate.Bloqueio(version);
        if (runtimeBlock is not null) return Block(runtimeBlock, protocolBlocked, Metadata());
        var restPrepared = State(StatusFluxoMvp.ResultadoIndeterminado, restArmed, "R165U_REST_OUTCOME_UNKNOWN");
        if (!await Save(stage, restPrepared)) return Unknown("R165U_FENCED_COMMIT_UNPROVEN");
        ct.ThrowIfCancellationRequested();
        antesEscrita?.Invoke();
        runtimeBlock = gate.Bloqueio(version);
        if (runtimeBlock is not null)
        {
            var blocked = Block(runtimeBlock, protocolBlocked, Metadata());
            return await Save(restArmed, blocked) ? blocked : Unknown("R165U_FENCED_COMMIT_UNPROVEN");
        }
        try
        {
            var protocol = await rest.EnviarMinimoAcmAsync(request, ct);
            if (!AcmR57Mapping.RespostaConfere(protocol, request)) return restPrepared;
            using var capture = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var saved = await vinculos.RegistrarProtocoloConfirmadoAsync(version.IntegracaoId, protocol.Id, protocol.NumeroTicket, capture.Token);
            AcmR57H4CorrelationAdapter.Validate(version, contract, saved);
            var readback = await h4.ReadAsync(version, contract, capture.Token);
            if (saved.CodigoContratoConfirmado != contract.Contrato.Codigo || saved.ProtocoloIdGuardian != protocol.Id ||
                saved.NumeroTicketGuardian != protocol.NumeroTicket || readback != saved) return restPrepared;
            var result = State(StatusFluxoMvp.AguardandoCallback, done, protocol: protocol);
            return await Save(restArmed, result, capture.Token) ? result : restPrepared;
        }
        catch (Exception ex) when (ex is IAcmGuardianHttpFailure)
        {
            var failure = (IAcmGuardianHttpFailure)ex;
            using var capture = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var failed = State(failure.ResultadoIndeterminado ? StatusFluxoMvp.ResultadoIndeterminado :
                StatusFluxoMvp.FalhaNaoReprocessavel,
                failure.ResultadoIndeterminado ? restArmed : "R165Y_REST_REJECTED", failure.SafeCode) with
                { ErroCategoria = "GUARDIAN_REST_HTTP_" + failure.HttpStatusCode.ToString(CultureInfo.InvariantCulture) };
            return await Save(restArmed, failed, capture.Token) ? failed : restPrepared;
        }
        catch { return restPrepared; }
    }
}
