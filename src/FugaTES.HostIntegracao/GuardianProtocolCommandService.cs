using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Integracao.Guardian;

namespace FugaTES.HostIntegracao;

public sealed record GuardianProtocolCommandOptions
{
    public bool Enabled { get; init; }
    public string Environment { get; init; } = "";
    public Guid IntegrationId { get; init; }
    public string Contract { get; init; } = "";
    public string OperatorSid { get; init; } = "";
    public bool ExecutionAuthorized { get; init; }
    public string? AuthorizationReference { get; init; }
    public DateTimeOffset? UntilUtc { get; init; }
}

public sealed record GuardianProtocolCommandView
{
    public Guid IntegrationId { get; init; }
    public string Contract { get; init; } = "";
    public int Version { get; init; }
    public string SnapshotFingerprint { get; init; } = "";
    public string? PayloadJson { get; init; }
    public string? PayloadSha256 { get; init; }
    public string? PreviewId { get; init; }
    public string Endpoint { get; init; } = "";
    public string Method { get; init; } = "POST";
    public string ContentType { get; init; } = "application/json";
    public string Accept { get; init; } = "*/*";
    public string TransferMode { get; init; } = "FIXED_CONTENT_LENGTH";
    public string AuthenticationContext { get; init; } = "GUARDIAN_HML_BACKEND";
    public bool CanSend { get; init; }
    public string? BlockCode { get; init; }
    public string State { get; init; } = "BLOCKED";
    public int? HttpStatus { get; init; }
    public string? SafeBusinessMessage { get; init; }
    public string? SafeResponseBody { get; init; }
    public long? ProtocolId { get; init; }
}

public interface IGuardianProtocolCommandService
{
    Task<GuardianProtocolCommandView> PreviewAsync(Guid integrationId, CancellationToken ct);
    Task<GuardianProtocolCommandView> SendAsync(Guid integrationId, string payloadSha256, string previewId, bool confirm, CancellationToken ct);
}

public sealed class GuardianProtocolCommandService(
    IContratoSapToledoVersaoStore versions, IAcmGuardianVinculoStore links,
    IMotorIntegracaoRepository repository, IResultadoFluxoMvpStore journal,
    IAcmR57Preparacao preparation, IAcmR57RuntimeGate gate,
    MotorIntegracaoServico motor, GuardianSet26HttpClient guardian,
    GuardianHttpOptions httpOptions, GuardianProtocolCommandOptions options, TimeProvider timeProvider)
    : IGuardianProtocolCommandService
{
    private sealed record FrozenPreview(Guid Id,long Version,string Fingerprint,string PayloadHash,AcmR57NormalContract Contract,PreCadastroAcmR57 Request);
    private FrozenPreview? frozenPreview;
    private const string Operation = "SAP_CONTRATO_TOLEDO";
    private const string Route = "/api/v1/Guardian/ProtocoloPreCadastro";

    public async Task<GuardianProtocolCommandView> PreviewAsync(Guid integrationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        frozenPreview=null;
        var view = new GuardianProtocolCommandView { IntegrationId = integrationId };
        GuardianProtocolCommandView Block(string code, string state = "BLOCKED")
            => view with { CanSend = false, BlockCode = code, State = state };
        if (options.Environment != "HML" || options.IntegrationId == Guid.Empty ||
            integrationId != options.IntegrationId || !Evidence(options.Contract))
            return Block("GUARDIAN_COMMAND_SCOPE_MISMATCH");
        try
        {
            if (httpOptions.BaseUrl is not { IsAbsoluteUri: true, Scheme: "https" } baseUrl ||
                baseUrl.UserInfo.Length != 0 || baseUrl.Query.Length != 0 || baseUrl.Fragment.Length != 0 ||
                httpOptions.PreCadastroEndpoint != Route || httpOptions.Timeout <= TimeSpan.Zero)
                return Block("GUARDIAN_COMMAND_HTTP_PROFILE_INVALID");
            view = view with { Endpoint = new Uri(baseUrl, Route).AbsoluteUri };
            var version = await versions.ObterPorIntegracaoIdAsync(integrationId, ct);
            var integration = await repository.ObterPorIdAsync(integrationId, ct);
            if (version is null || integration is null || version.IntegracaoId != integrationId ||
                integration.Id != integrationId || version.NumeroContrato != options.Contract ||
                version.Snapshot.NumeroContrato != version.NumeroContrato || version.Versao is <= 0 or > int.MaxValue ||
                integration.Origem != "SAP" || integration.Destino != "TOLEDO" || integration.Operacao != Operation ||
                integration.ChaveNegocio != version.NumeroContrato || !Evidence(integration.CorrelationId) ||
                integration.FingerprintSha256 != version.FingerprintSha256 ||
                new ContratoSapToledoCanonicalizer().Canonicalizar(version.Snapshot).FingerprintSha256 != version.FingerprintSha256)
                return Block("GUARDIAN_COMMAND_H4_IDENTITY_INVALID");
            view = view with
            {
                Contract = version.NumeroContrato, Version = (int)version.Versao,
                SnapshotFingerprint = version.FingerprintSha256
            };
            if (preparation is not IAcmR57NormalPreparacao normal)
                return Block("GUARDIAN_COMMAND_NORMAL_PREPARATION_REQUIRED");
            if(gate.Bloqueio(version) is "R171_EMITTER_VALIDATION_REQUIRED" or "R171_EMITTER_PROOF_EXPIRED" && await AcmR57DynamicAdmission.BloqueioAsync(gate,preparation,version,ct) is {} emitterBlock)
                throw new AcmR57BlockedException(emitterBlock);
            var contract = normal.PrepararContrato(version);
            var request = normal.PrepararProtocolo(version, contract);
            var bytes = GuardianSet26HttpClient.SerializarPayloadR57(request);
            view = view with
            {
                PayloadJson = Encoding.UTF8.GetString(bytes),
                PayloadSha256 = Convert.ToHexString(SHA256.HashData(bytes))
            };
            view = view with { PreviewId = ComputePreviewId(view) };
            var link = await links.ObterPorIntegracaoAsync(integrationId, ct);
            if (link is null) return Block("GUARDIAN_COMMAND_CONFIRMED_CONTRACT_REQUIRED");
            AcmR57H4CorrelationAdapter.Validate(version, contract, link);
            if (link.CodigoContratoConfirmado != contract.Contrato.Codigo)
                return Block("GUARDIAN_COMMAND_CONFIRMED_CONTRACT_REQUIRED");
            view = view with { ProtocolId = link.ProtocoloIdGuardian };
            await journal.ExigirDisponibilidadeAsync(ct);
            var previous = await journal.ObterPorIntegracaoAsync(integrationId, integration.CorrelationId, Operation, ct);
            if (HasExternalResult(link) || previous?.ProtocoloGuardian is not null || previous?.NumeroTicketGuardian is not null)
                return Block("GUARDIAN_COMMAND_EXISTING_EXTERNAL_RESULT");
            if (!SafeStage(previous, integration.CorrelationId, contract))
                return Block("GUARDIAN_COMMAND_JOURNAL_NOT_SAFE", previous?.Status == StatusFluxoMvp.ResultadoIndeterminado ? "UNKNOWN" : "BLOCKED");
            var now = timeProvider.GetUtcNow();
            if (integration.Estado != EstadoIntegracao.Pendente || integration.FaseEnvio != FaseEnvioIntegracao.NaoIniciado ||
                integration.LeaseExpiraEm > now || integration.ProximaTentativaEm is null || integration.ProximaTentativaEm > now ||
                integration.TipoDisparoPendente is not (TipoDisparoPendente.Automatico or TipoDisparoPendente.IntegrarAgora))
                return Block("GUARDIAN_COMMAND_INTEGRATION_NOT_READY");
            if (!await repository.EstaDisponivelAsync(ct) ||
                await repository.ExistePausaAplicavelAsync(integration.Origem, integration.Destino, integration.Operacao, ct))
                return Block("GUARDIAN_COMMAND_MOTOR_UNAVAILABLE");
            if (gate.Bloqueio(version) is { } runtimeBlock) return Block(runtimeBlock);
            if (await versions.TemPredecessorNaoTerminalAsync(version.NumeroContrato, version.Versao, ct))
                return Block("GUARDIAN_COMMAND_PREDECESSOR_PENDING");
            if (AuthorizationBlock() is { } authorizationBlock) return Block(authorizationBlock);
            frozenPreview=new(integrationId,version.Versao,version.FingerprintSha256,view.PayloadSha256!,contract,request);
            return view with { CanSend = true, BlockCode = null, State = "READY" };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (AcmR57BlockedException ex) { return Block(ex.Code); }
        catch { return Block("GUARDIAN_COMMAND_PREFLIGHT_UNVERIFIED"); }
    }

    public async Task<GuardianProtocolCommandView> SendAsync(Guid integrationId, string payloadSha256, string previewId, bool confirm, CancellationToken ct)
    {
        var preview = await PreviewAsync(integrationId, ct);
        if (!preview.CanSend) return preview;
        var frozen=frozenPreview;
        if(frozen is null||frozen.Id!=integrationId||frozen.Version!=preview.Version||frozen.Fingerprint!=preview.SnapshotFingerprint||frozen.PayloadHash!=preview.PayloadSha256)
            return preview with{CanSend=false,BlockCode="GUARDIAN_COMMAND_PREPARED_PAYLOAD_UNAVAILABLE",State="BLOCKED"};
        if (!confirm) return preview with { CanSend = false, BlockCode = "GUARDIAN_COMMAND_CONFIRMATION_REQUIRED", State = "BLOCKED" };
        if (payloadSha256 != preview.PayloadSha256)
            return preview with { CanSend = false, BlockCode = "GUARDIAN_COMMAND_PAYLOAD_CHANGED", State = "BLOCKED" };
        if (previewId is not { Length: 64 } || !previewId.All(char.IsAsciiHexDigit))
            return preview with { CanSend = false, BlockCode = "GUARDIAN_COMMAND_PREVIEW_ID_INVALID", State = "BLOCKED" };
        if (previewId != preview.PreviewId)
            return preview with { CanSend = false, BlockCode = "GUARDIAN_COMMAND_PREVIEW_CHANGED", State = "BLOCKED" };
        if (AuthorizationBlock() is { } block)
            return preview with { CanSend = false, BlockCode = block, State = "BLOCKED" };
        ct.ThrowIfCancellationRequested();
        var remaining = options.UntilUtc!.Value - timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            return preview with { CanSend = false, BlockCode = "GUARDIAN_COMMAND_AUTHORIZATION_REQUIRED", State = "BLOCKED" };
        // Respect the platform timer limit conservatively; a long window may cancel early, never late.
        var maximumDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        using var expiry = new CancellationTokenSource(remaining <= maximumDelay ? remaining : maximumDelay, timeProvider);
        using var dispatch = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token);
        var before = guardian.LastR57Diagnostic;
        CicloMotorIntegracaoResultado? outcome = null;
        // Only the existing motor owns claim/fencing and external dispatch. Never reset or repair its journal here.
        try
        {
            dispatch.Token.ThrowIfCancellationRequested();
            outcome = await motor.ExecutarPorIdAsync(integrationId, dispatch.Token);
        }
        catch { /* After entering the motor, an interrupted call is not evidence of a safe retry. */ }
        var result = preview with { CanSend = false, BlockCode = "GUARDIAN_COMMAND_OUTCOME_UNVERIFIED", State = "UNKNOWN" };
        var diagnostic = guardian.LastR57Diagnostic;
        if (ReferenceEquals(before, diagnostic) || diagnostic?.PayloadSha256 != preview.PayloadSha256) diagnostic = null;
        if (diagnostic is not null)
            result = result with
            {
                HttpStatus = diagnostic.HttpStatus > 0 ? diagnostic.HttpStatus : null,
                SafeBusinessMessage = diagnostic.SafeBusinessMessage, SafeResponseBody = diagnostic.SafeResponseBody
            };
        // Read known receipts even if the sending request was cancelled. This token never authorizes another send.
        using var capture = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var integration = await repository.ObterPorIdAsync(integrationId, capture.Token);
            var version = await versions.ObterPorIntegracaoIdAsync(integrationId, capture.Token);
            if (integration is null || version is null || version.IntegracaoId != integrationId ||
                integration.Id != integrationId || version.NumeroContrato != preview.Contract ||
                version.Snapshot.NumeroContrato != preview.Contract ||
                version.Versao != preview.Version || version.FingerprintSha256 != preview.SnapshotFingerprint ||
                integration.FingerprintSha256 != preview.SnapshotFingerprint || integration.Operacao != Operation ||
                integration.Origem != "SAP" || integration.Destino != "TOLEDO" || integration.ChaveNegocio != preview.Contract ||
                new ContratoSapToledoCanonicalizer().Canonicalizar(version.Snapshot).FingerprintSha256 != preview.SnapshotFingerprint ||
                preparation is not IAcmR57NormalPreparacao normal)
                return result;
            // Receipt collection is read-only: use the exact preflight values, never re-admit remotely after sending.
            var contract = frozen.Contract;
            var request = frozen.Request;
            if (Convert.ToHexString(SHA256.HashData(GuardianSet26HttpClient.SerializarPayloadR57(request))) != preview.PayloadSha256)
                return result;
            var link = await links.ObterPorIntegracaoAsync(integrationId, capture.Token);
            if (link is null) return result;
            AcmR57H4CorrelationAdapter.Validate(version, contract, link);
            var receipt = await journal.ObterPorIntegracaoAsync(integrationId, integration.CorrelationId, Operation, capture.Token);
            if (receipt?.Correlacao != integration.CorrelationId) return result;
            if (link.ProtocoloIdGuardian is > 0)
            {
                result = result with { ProtocolId = link.ProtocoloIdGuardian };
                if (receipt.Status == StatusFluxoMvp.AguardandoCallback && receipt.Etapa == "R165U_CONFIRMED" &&
                    !receipt.ErroResultadoIndeterminado && link.CodigoContratoConfirmado == contract.Contrato.Codigo &&
                    MatchesMetadata(receipt, contract, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)))) &&
                    receipt.ProtocoloGuardian == link.ProtocoloIdGuardian.Value.ToString(CultureInfo.InvariantCulture) &&
                    receipt.NumeroTicketGuardian == link.NumeroTicketGuardian &&
                    (diagnostic is null || diagnostic.ProtocolId is null || diagnostic.ProtocolId == link.ProtocoloIdGuardian))
                    return result with { State = "CONFIRMED", BlockCode = "GUARDIAN_COMMAND_ALREADY_SENT" };
                return result;
            }
            if (receipt.Status == StatusFluxoMvp.FalhaNaoReprocessavel)
                return result with { State = "FAILED", BlockCode = "GUARDIAN_COMMAND_REJECTED" };
            if (outcome is CicloMotorIntegracaoResultado.ClaimNaoAdquirido or CicloMotorIntegracaoResultado.NenhumTrabalho or
                CicloMotorIntegracaoResultado.ValidacaoReprovada or CicloMotorIntegracaoResultado.DegradedDb)
                return result with { State = "BLOCKED", BlockCode = "GUARDIAN_COMMAND_MOTOR_NOT_DISPATCHED" };
        }
        catch { /* Missing/unreadable durable evidence leaves the outcome unknown; never submit again. */ }
        return result;
    }

    private string ComputePreviewId(GuardianProtocolCommandView view)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            view.IntegrationId, view.Contract, view.Version, view.SnapshotFingerprint,
            view.PayloadSha256, view.Endpoint, view.Method,
            options.OperatorSid, options.AuthorizationReference,
            UntilUtc = options.UntilUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        });
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private string? AuthorizationBlock()
    {
        if (!options.Enabled || !options.ExecutionAuthorized) return "GUARDIAN_COMMAND_EXECUTION_DISABLED";
        if (!Evidence(options.AuthorizationReference) || !Evidence(options.OperatorSid) ||
            options.UntilUtc is null || options.UntilUtc <= timeProvider.GetUtcNow())
            return "GUARDIAN_COMMAND_AUTHORIZATION_REQUIRED";
        return null;
    }

    private static bool Evidence(string? value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() &&
        value.Length <= 160 && !value.Any(char.IsControl);

    private static bool HasExternalResult(AcmGuardianVinculo link) => link.ProtocoloIdGuardian is not null ||
        link.NumeroTicketGuardian is not null || link.CallbackIdGuardian is not null ||
        link.CallbackSequencialGuardian is not null || link.CallbackFingerprint is not null;

    private static bool SafeStage(ResultadoFluxoMvp? previous, string correlation, AcmR57NormalContract contract)
    {
        if (previous is null || previous.Correlacao != correlation || previous.ErroResultadoIndeterminado ||
            previous.DecisaoPendente is not null ||
            !(previous.Etapa == "R165U_SOAP_CONFIRMED" && previous.Status == StatusFluxoMvp.EmProcessamento ||
              previous.Etapa == "R165U_PROTOCOL_BLOCKED" && previous.Status == StatusFluxoMvp.AguardandoDependenciaExterna))
            return false;
        return MatchesMetadata(previous, contract, "");
    }

    private static bool MatchesMetadata(ResultadoFluxoMvp result, AcmR57NormalContract contract, string protocolDigest)
    {
        var pieces = result.ResultadoFuncional?.Split(':');
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(contract)));
        return pieces is { Length: 4 } && pieces[0] == "U" && pieces[1] == digest && pieces[2] == protocolDigest &&
            (pieces[3].Length == 0 || long.TryParse(pieces[3], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) && sequence > 0);
    }
}
