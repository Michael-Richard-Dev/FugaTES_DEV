namespace FugaTES.Aplicacao.Transportadoras;
public sealed record CarrierSyncAuthorization(string Environment, bool ProfileProven, bool SignatureProven, DateTimeOffset UntilUtc, string[] AllowedBps, string[] AllowedSids, bool IncrementalReleased = false) { public bool WritesEnabled { get; init; } = true; }
public sealed class CarrierSyncPolicy(CarrierSyncAuthorization authorization, TimeProvider time)
{
    public bool PodeEscrever(string bp) => authorization.WritesEnabled && authorization.Environment == "HML" && authorization.ProfileProven && authorization.SignatureProven && time.GetUtcNow() < authorization.UntilUtc && authorization.AllowedBps.Contains(bp,StringComparer.Ordinal);
    public bool PodeSolicitar(string bp, string sid) => PodeEscrever(bp) && authorization.AllowedSids.Contains(sid,StringComparer.Ordinal);
    public bool IncrementalReleased => authorization.IncrementalReleased && authorization.Environment == "HML" && authorization.ProfileProven && authorization.SignatureProven && time.GetUtcNow() < authorization.UntilUtc;
    public IReadOnlyList<string> ControlledBps => authorization.AllowedBps;
}