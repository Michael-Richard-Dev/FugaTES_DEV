using System.Text.Json;
using FugaTES.Aplicacao.Servicos.ManualRetry;

namespace FugaTES.Callback.Http.ManualRetry;

// One server-owned deployment profile. No business authorization is inferred from legacy settings.
public sealed record AcmManualRetryCommandProfile
{
    public string BaseConfigurationFile { get; init; } = "";
    public string CredentialFile { get; init; } = "";
    public string ApprovalsFile { get; init; } = "";
    public string Passfile { get; init; } = "";
    public string OperatorSid { get; init; } = "";
    public string WorkerIdentity { get; init; } = "";
    public string? NormalConfigurationFile { get; init; }

    /// <summary>
    /// R163: arquivo restrito com as credenciais Guardian (Email/Password/Signature), lido por
    /// PrivateCommandFile com privateContent:true — isto e, modo 0600, sem symlink, dono
    /// conferido e fora do diretorio do release.
    ///
    /// Existe para tirar o segredo da variavel de ambiente do processo, hoje legivel por
    /// qualquer processo do mesmo usuario via /proc/&lt;pid&gt;/environ. Compativel com
    /// systemd LoadCredential=, cujo destino em $CREDENTIALS_DIRECTORY ja satisfaz essas regras.
    ///
    /// Opcional: enquanto for nulo, o host continua lendo do ambiente exatamente como antes.
    /// </summary>
    public string? GuardianSecretsFile { get; init; }
    // Optional, separate SAP-weighing authorizations. Null keeps the existing command surface unchanged.
    public string? ToledoSapRetryApprovalsFile { get; init; }
    public string? ToledoSapOperatorWindowFile { get; init; }
    public string? Contract { get; init; }
    public string? Item { get; init; }

    public void Validate(IReadOnlyList<AcmRetryApproval> approvals)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(OperatorSid, @"\AS-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+\z") ||
            !AcmManualRetryPolicy.Evidence(WorkerIdentity) || WorkerIdentity.Length > 120 ||
            new[] { BaseConfigurationFile, CredentialFile, ApprovalsFile, Passfile }.Any(p =>
                string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p) || p.Any(char.IsControl)) ||
            new[] { BaseConfigurationFile, CredentialFile, ApprovalsFile, Passfile }.Distinct(StringComparer.Ordinal).Count() != 4)
            throw new InvalidOperationException("ACM_COMMAND_PROFILE_INVALID");
        if ((Contract is null) != (Item is null) || (Contract is not null &&
            (Contract.Length == 0 || !Contract.All(char.IsAsciiDigit) || !AcmManualRetryPolicy.Evidence(Item))))
            throw new InvalidOperationException("ACM_COMMAND_SCOPE_INVALID");
        if (approvals.Count > 1 || approvals.Select(a => a.IntegrationId).Distinct().Count() != approvals.Count)
            throw new InvalidOperationException("ACM_COMMAND_APPROVALS_AMBIGUOUS");
        foreach (var a in approvals)
        {
            if (string.IsNullOrWhiteSpace(NormalConfigurationFile) || !Path.IsPathFullyQualified(NormalConfigurationFile) || NormalConfigurationFile.Any(char.IsControl) || Contract is null || a.Contract != Contract || a.Item != Item || a.OperatorSid != OperatorSid ||
                a.IntegrationId == Guid.Empty || !a.Homologated || !a.ExecutionAuthorized || !a.ExclusiveWorker ||
                !AcmManualRetryPolicy.Hash(a.Fingerprint) || !AcmManualRetryPolicy.Hash(a.ObservedSnapshotDigest) ||
                !AcmManualRetryPolicy.Evidence(a.AuthorizationReference) || !AcmManualRetryPolicy.Evidence(a.BusinessOriginEvidence) ||
                a.UntilUtc <= a.NotBeforeUtc || a.AllowedStage is not (AcmRetryStage.SoapThenRest or AcmRetryStage.RestOnly) ||
                (a.AllowedStage == AcmRetryStage.RestOnly && !AcmManualRetryPolicy.Hash(a.ContractMappingHash)))
                throw new InvalidOperationException("ACM_COMMAND_APPROVAL_SCOPE_INVALID");
        }
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}

public sealed class StaticAcmRetryApprovals : IAcmRetryApprovals
{
    readonly IReadOnlyDictionary<Guid, AcmRetryApproval> approvals;
    public StaticAcmRetryApprovals(AcmManualRetryCommandProfile profile, IReadOnlyList<AcmRetryApproval> values)
    {
        profile.Validate(values);
        approvals = values.ToDictionary(a => a.IntegrationId);
    }
    public Task<AcmRetryApproval?> ResolveAsync(AcmRetryActor actor, Guid integrationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(actor.Authenticated && approvals.TryGetValue(integrationId, out var value) && value.OperatorSid == actor.Sid ? value : null);
    }
}

