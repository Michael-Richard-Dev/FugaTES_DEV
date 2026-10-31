using FugaTES.Integracao.SAP.BusinessPartner;
namespace FugaTES.WinForms.Transportadoras.MasterdataR3;
public sealed record MasterdataSapReadAuthorizationR20C(bool Approved, bool AppOpen, bool SapProfileVerified,
    string Environment, string SapClient, string ApprovalReference, DateTimeOffset UntilUtc);
public sealed record MasterdataReconciliationCycleLimitsR20C(int NewPagesPerCycle, int NewBpsPerCycle, SapRoleScanLimitsR20A ScanLimits);
public enum MasterdataReconciliationCycleStateR20C { Inactive, Blocked, Busy, InProgress, Promoted, Canceled, TimedOut, Failed, AppClosed }
public sealed record MasterdataReconciliationCycleResultR20C(MasterdataReconciliationCycleStateR20C State,
    string Code, Guid? ScanId, int NewPages, int NewBps, int CachedPages, int CachedBps,
    IReadOnlyList<SapBpChangeR20B> Candidates)
{
    public bool GuardianWritesAuthorized => false;
    public bool DurableCheckpoint => false;
}
