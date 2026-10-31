using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace R165AL;

public sealed record PilotPlan(Guid RunId, string Contract, string Description)
{
    public const string SyntheticContract = "0000986801";
    public const string SyntheticDescription = "R165AL SYNTHETIC POSTGRESQL TEST - NO REAL GUARDIAN";
    public const string Partner = "TEST_R165AL_PARTNER";
    public const string Material = "TEST_R165AL_MATERIAL";
    public const long SimulatedProtocol = 986801;
    public static PilotPlan Create() => new(Guid.NewGuid(), SyntheticContract, SyntheticDescription);
    public string Reference => "OC" + Contract.TrimStart('0');
    public void Validate()
    {
        if (RunId == Guid.Empty || Contract != SyntheticContract || Description != SyntheticDescription)
            throw new InvalidOperationException("R165AL_SYNTHETIC_PLAN_REQUIRED");
    }
    public string Hash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
    public void Authorize(WriteApproval approval, DateTimeOffset now)
    {
        Validate();
        if (!approval.Authorized || approval.PlanSha256 != Hash() || approval.RunId != RunId || approval.Contract != Contract ||
            string.IsNullOrWhiteSpace(approval.OperatorReference) || approval.OperatorReference.Length > 160 ||
            approval.OperatorReference.Any(char.IsControl) || approval.ValidUntilUtc.Offset != TimeSpan.Zero ||
            approval.ValidUntilUtc <= now || approval.ValidUntilUtc > now.AddHours(1))
            throw new InvalidOperationException("R165AL_NOMINAL_WRITE_AUTHORIZATION_REQUIRED");
    }
    public object Payload() => new { Contrato = new[] { new {
        NumeroContrato = Contract, TipoContrato = "AC3P", DescricaoTipoContrato = Description,
        Centro = "3011", TipoPesagem = "E", OperacaoContrato = "10", StatusContrato = "T",
        IdParceiro = Partner, Material, QuantidadeContrato = 1m } } };
}

public sealed record WriteApproval(bool Authorized = false, string PlanSha256 = "", Guid RunId = default,
    string Contract = "", string OperatorReference = "", DateTimeOffset ValidUntilUtc = default);

public static class PilotMode
{
    public static bool AllowsWrites(string mode) => mode switch {
        "execute" => true, "plan" or "readback" => false,
        _ => throw new InvalidOperationException("R165AL_UNKNOWN_MODE") };
}

public sealed record PhysicalFrame
{
    public string Schema { get; init; } = "R165AL_PHYSICAL_SIMULATED_GUARDIAN_V1";
    public Guid RunId { get; init; }
    public Guid IntegrationId { get; init; }
    public string Contract { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public string Description { get; init; } = "";
    public string State { get; init; } = "";
    public string? Stage { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
    public string? Protocol { get; init; }
    public string? Reference { get; init; }
    public string? ConfirmedCode { get; init; }
    public string? PayloadJson { get; init; }
    public long H4Version { get; init; }
    public long Attempts { get; init; }
    public long Fencing { get; init; }
    public Guid? AttemptId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset CapturedUtc { get; init; }
    public string Cluster { get; init; } = "";
    public int Port { get; init; }
    public string Database { get; init; } = "";
    public string Role { get; init; } = "";
    public bool ReadOnly { get; init; }
    public bool GuardianSimulated { get; init; } = true;
    public string? H4Protocol { get; init; }
    public bool RestartVerified { get; init; }

    public void Validate(PilotPlan plan, Guid expectedId)
    {
        plan.Validate();
        if (Schema != "R165AL_PHYSICAL_SIMULATED_GUARDIAN_V1" || RunId != plan.RunId ||
            IntegrationId == Guid.Empty || IntegrationId != expectedId || Contract != plan.Contract ||
            Description != plan.Description || H4Version != 1 || !ReadOnly || !GuardianSimulated ||
            Cluster != "18/fugatesacmdev" || Port != 5433 || Database != "fugates_dev" || Role != "fugates_acm_hml_app" ||
            Fingerprint.Length != 64 || !Fingerprint.All(Uri.IsHexDigit) ||
            !CorrelationId.StartsWith("r165ag:cpi:" + plan.RunId.ToString("N") + ":", StringComparison.Ordinal) ||
            CorrelationId.Length != 76 || !Guid.TryParseExact(CorrelationId[^32..], "N", out var suffix) || suffix == Guid.Empty ||
            Attempts is < 0 or > 1 || Fencing is < 0 or > 1 ||
            DateTimeOffset.UtcNow - CapturedUtc > TimeSpan.FromSeconds(15) || CapturedUtc > DateTimeOffset.UtcNow.AddSeconds(5))
            throw new InvalidOperationException("R165AL_PHYSICAL_READBACK_IDENTITY_REJECTED");
        if (RestartVerified) RequireSuccessfulTerminal(plan);
    }

    public void RequireSuccessfulTerminal(PilotPlan plan)
    {
        if (State != "CONCLUIDA" || Stage != "R165U_CONFIRMED" || Attempts != 1 || Fencing != 1 ||
            Protocol != PilotPlan.SimulatedProtocol.ToString() || H4Protocol != Protocol || ConfirmedCode != plan.Contract ||
            Reference != plan.Reference || Error is not null)
            throw new InvalidOperationException("R165AL_SUCCESSFUL_TERMINAL_FACTS_REQUIRED");
    }
}
