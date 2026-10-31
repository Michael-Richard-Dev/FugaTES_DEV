namespace FugaTES.Integracao.SAP.BusinessPartner;

public sealed record SapRoleRowR20A(string BusinessPartner, string BusinessPartnerRole, string? ValidFrom, string? ValidTo);
public sealed record SapRolePageRequestR20A(int PageNumber, int Top, int Skip)
{
    public string RelativeUri => "A_BusinessPartnerRole?$filter=BusinessPartnerRole%20eq%20'CRM010'&$select=BusinessPartner,BusinessPartnerRole,ValidFrom,ValidTo&$orderby=BusinessPartner%20asc&$top=" + Top.ToString(System.Globalization.CultureInfo.InvariantCulture) + "&$skip=" + Skip.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
public sealed record SapRolePageR20A(IReadOnlyList<SapRoleRowR20A> Rows);
// An implementation must honor the request exactly. This offline phase supplies no HTTP adapter.
public interface ISapRolePageReaderR20A
{
    Task<SapRolePageR20A> ReadAsync(SapRolePageRequestR20A request, CancellationToken cancellationToken);
}
public sealed record SapRoleScanLimitsR20A(int PageSize = 200, int MaxPages = 1000, int MaxRows = 100000);
public sealed record SapRoleScanProgressR20A(int PagesRead, int RowsRead, int NextSkip);
public sealed class SapRoleSnapshotR20A
{
    internal SapRoleSnapshotR20A(IEnumerable<SapRoleRowR20A> rows, bool complete, string reason,
        int pages, DateTimeOffset started, DateTimeOffset ended)
    {
        Rows = Array.AsReadOnly(rows.ToArray()); Complete = complete; Reason = reason;
        PagesRead = pages; StartedAt = started; EndedAt = ended;
    }
    public IReadOnlyList<SapRoleRowR20A> Rows { get; }
    public bool Complete { get; }
    public bool ConsolidationCandidate => Complete;
    public string Reason { get; }
    public int PagesRead { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset EndedAt { get; }
    public bool TransactionalConsistencyProven => false;
    public bool CorporateCompletenessProven => false;
    public static IReadOnlyList<string> KnownLimits { get; } = Array.AsReadOnly(new[] {
        "TOP_SKIP_CONCURRENT_MUTATION", "CORPORATE_COMPLETENESS_UNPROVEN" });
}
public enum SapRoleCandidateKindR20A
{
    NEW_ELIGIBLE_BP, CHANGED_BP_ROLE, ROLE_VALIDITY_CHANGED, ELIGIBILITY_EXPIRED,
    REMOVED_ROLE_CANDIDATE, NO_CHANGE, INDETERMINATE
}
public sealed record SapRoleCandidateR20A(string BusinessPartner, SapRoleCandidateKindR20A Kind,
    string Reason, bool RequiresBpMasterRead)
{
    // Classification never grants permission for a Guardian write or deactivation.
    public bool GuardianWriteAuthorized => false;
    public bool GuardianDeleteOrDeactivate => false;
}
public sealed record SapRoleComparisonR20A(bool CompleteEnumerationPair,
    IReadOnlyList<SapRoleCandidateR20A> Candidates, string Reason)
{
    public bool TransactionalConsistencyProven => false;
    public bool BpMasterEnrichmentImplemented => false;
}
