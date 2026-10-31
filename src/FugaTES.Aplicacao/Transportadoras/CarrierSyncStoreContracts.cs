namespace FugaTES.Aplicacao.Transportadoras;

public sealed record CarrierStoredCheckpoint(DateTimeOffset? Watermark, DateTimeOffset? UpperBound, string? PageCursor) { public string[] VisitedPages { get; init; } = []; }
public sealed record CarrierPendingWrite(Guid AttemptId, Guid CycleId, string Bp, string Action, string ExpectedHash, string? GuardianCode, DateTimeOffset StartedUtc);
public sealed record CarrierStoredBp(string Bp, string Name, string? GuardianCode, string ContentHash, string Status, string Action, string ResultCode, DateTimeOffset SapCheckedUtc, DateTimeOffset? GuardianSyncedUtc, Guid? PendingAttemptId);
public sealed record CarrierStoredStep(Guid CycleId, string Bp, string Step, string ResultCode, DateTimeOffset OccurredUtc);
public sealed record CarrierStoredCycle(Guid Id, DateTimeOffset StartedUtc, DateTimeOffset? FinishedUtc, int Consulted, int Created, int Updated, int Unchanged, int Pending, int Failed, string Status);
public sealed record CarrierManualRequest(Guid Id, string Bp, string AuthenticatedSid, DateTimeOffset RequestedUtc);
public interface ICarrierCycleLease : IAsyncDisposable
{
    CancellationToken PerdaToken { get; }
    Task VerificarAtivaAsync(CancellationToken ct);
    Task<CarrierManualRequest?> TomarPedidoAsync(CancellationToken ct);
    Task ConcluirPedidoAsync(Guid id, string resultCode, CancellationToken ct);
    Task IniciarAsync(Guid cycleId, DateTimeOffset startedUtc, CancellationToken ct);
    Task<bool> RegistrarAntesDoWriteAsync(CarrierPendingWrite attempt, CancellationToken ct);
    Task ConcluirBpAsync(CarrierStoredBp result, CancellationToken ct);
    Task ConfirmarPaginaAsync(CarrierStoredCheckpoint checkpoint, CancellationToken ct);
    Task RegistrarEtapaAsync(CarrierStoredStep step, CancellationToken ct);
    Task HeartbeatAsync(DateTimeOffset now, DateTimeOffset nextCycle, CancellationToken ct);
    Task ConcluirAsync(CarrierStoredCycle cycle, CancellationToken ct);
}
public interface ICarrierSyncStore
{
    Task<ICarrierCycleLease?> TentarAdquirirCicloAsync(CancellationToken ct);
    Task<bool> EnfileirarPedidoAsync(Guid requestId, string bp, string authenticatedSid, DateTimeOffset requestedAt, CancellationToken ct);
    Task<CarrierStoredBp?> CarregarEstadoAsync(string bp, CancellationToken ct);
    Task<CarrierStoredCheckpoint> CarregarCheckpointAsync(CancellationToken ct);
}
public sealed record CarrierSyncFiltro(string? CodigoBp = null, string? Nome = null, string? Status = null, DateTimeOffset? DeUtc = null, DateTimeOffset? AteUtc = null, int Limit = 200)
{
    public void Validar()
    {
        if (Limit is < 1 or > 1000 || (DeUtc.HasValue && AteUtc.HasValue && DeUtc > AteUtc)) throw new ArgumentException("CARRIER_FILTER_INVALID");
    }
}
public sealed record CarrierSyncIndicators(bool ServiceActive, int IntervalSeconds, DateTimeOffset? LastCycleUtc, DateTimeOffset? NextCycleUtc, long Found, long Created, long Updated, long Unchanged, long Pending, long Errors) { public long GuardianCount { get; init; } }
public sealed record CarrierSyncSnapshot(IReadOnlyList<CarrierStoredBp> Rows, CarrierSyncIndicators Indicators);
public sealed record CarrierBpDetail(CarrierStoredBp? State, IReadOnlyList<CarrierStoredStep> Steps);
public interface ICarrierSyncReadRepository
{
    Task<CarrierSyncSnapshot> ConsultarAsync(CarrierSyncFiltro filtro, CancellationToken ct);
    Task<CarrierBpDetail> DetalharBpAsync(string bp, CancellationToken ct);
    Task<IReadOnlyList<CarrierStoredCycle>> ConsultarCiclosAsync(CarrierSyncFiltro filtro, CancellationToken ct);
    Task<IReadOnlyList<CarrierStoredStep>> DetalharCicloAsync(Guid id, CancellationToken ct);
}
public static class CarrierPersistencePolicy
{
    private static readonly HashSet<string> Steps = ["SAP_GET", "GUARDIAN_GET", "GUARDIAN_POST", "GUARDIAN_PUT", "GUARDIAN_READBACK", "RESULTADO_FINAL"];
    public static void ValidateCode(string code) { if (string.IsNullOrEmpty(code) || code.Length > 100 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw new ArgumentException("CARRIER_LOG_CODE_INVALID"); }
    public static void ValidateStep(string step) { if (!Steps.Contains(step)) throw new ArgumentException("CARRIER_LOG_STEP_INVALID"); }
    public static void ValidateBp(string bp) { if (string.IsNullOrWhiteSpace(bp) || bp.Length > 40 || bp.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) throw new ArgumentException("CARRIER_BP_INVALID"); }
    public static void ValidateHash(string hash) { if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("CARRIER_HASH_INVALID"); }
    public static bool ServiceActive(bool enabled, DateTimeOffset? heartbeat, DateTimeOffset now) => enabled && heartbeat.HasValue && heartbeat <= now && now - heartbeat <= TimeSpan.FromSeconds(65);
}


