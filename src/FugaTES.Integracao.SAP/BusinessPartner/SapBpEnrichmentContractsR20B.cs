using System.Collections.ObjectModel;
using System.Text.Json;
namespace FugaTES.Integracao.SAP.BusinessPartner;

public enum SapBpFieldR20B { BusinessPartner, BusinessPartnerName, BusinessPartnerFullName,
    BusinessPartnerCategory, OrganizationBPName1, FirstName, LastName, BusinessPartnerIsBlocked,
    BusinessPartnerTax, BusinessPartnerAddress }
public enum SapBpNormalizationR20B { ExactOrdinal, Unapproved }
public enum SapRoleTemporalContractR20B { Unknown, EdmDateTimeOffset, R25TBusinessDate }
public enum SapBpComparisonStatusR20B { Complete, Indeterminate }
public sealed class SapBpComparisonPolicyR20B
{
    public SapBpComparisonPolicyR20B(IEnumerable<SapBpFieldR20B> fields, bool approved,
        bool completeFieldScope, string evidenceReference,
        SapBpNormalizationR20B normalization = SapBpNormalizationR20B.ExactOrdinal,
        SapRoleTemporalContractR20B temporalContract = SapRoleTemporalContractR20B.Unknown)
    {
        Fields = Array.AsReadOnly(fields.ToArray()); Approved = approved;
        CompleteFieldScope = completeFieldScope; EvidenceReference = evidenceReference;
        Normalization = normalization; TemporalContract = temporalContract;
    }
    public IReadOnlyList<SapBpFieldR20B> Fields { get; }
    public bool Approved { get; }
    public bool CompleteFieldScope { get; }
    public string EvidenceReference { get; }
    public SapBpNormalizationR20B Normalization { get; }
    public SapRoleTemporalContractR20B TemporalContract { get; }
    internal string ScopeKey => string.Join(",", Fields.Order()) + "|" + EvidenceReference + "|" + Normalization + "|" + TemporalContract;
}
// Raw JSON stays in memory. Never include it in logs or diagnostic exceptions.
public sealed record SapBpReadResultR20B(string BusinessPartner, JsonElement RawSapData, bool Complete = true, string Reason = "BP_READ_COMPLETE");
public interface ISapBpMasterReaderR20B
{
    Task<SapBpReadResultR20B> ReadAsync(string businessPartner, CancellationToken cancellationToken);
}
public sealed class SapBpEnrichedEntryR20B
{
    internal SapBpEnrichedEntryR20B(string bp, JsonElement raw, IDictionary<string,string> normalized,
        SapBpComparisonStatusR20B status, string reason)
    {
        BusinessPartner = bp; RawSapData = raw.Clone();
        NormalizedSapData = new ReadOnlyDictionary<string,string>(new Dictionary<string,string>(normalized, StringComparer.Ordinal));
        ComparisonStatus = status; Reason = reason;
    }
    public string BusinessPartner { get; }
    public JsonElement RawSapData { get; }
    // Exact structural projection only; no trim, case folding, tax/address/UF transformation.
    public IReadOnlyDictionary<string,string> NormalizedSapData { get; }
    public SapBpComparisonStatusR20B ComparisonStatus { get; }
    public string Reason { get; }
}
public sealed class SapBpEnrichedSnapshotR20B
{
    internal SapBpEnrichedSnapshotR20B(SapRoleSnapshotR20A roles,
        IEnumerable<SapBpEnrichedEntryR20B> entries, bool complete, string reason,
        DateTimeOffset referenceAt, SapBpComparisonPolicyR20B policy)
    {
        Roles = roles; Entries = Array.AsReadOnly(entries.ToArray()); Complete = complete;
        Reason = reason; ReferenceAt = referenceAt; Policy = policy;
    }
    public SapRoleSnapshotR20A Roles { get; }
    public IReadOnlyList<SapBpEnrichedEntryR20B> Entries { get; }
    public bool Complete { get; }
    public bool Valid => Complete && Policy.Approved && Policy.CompleteFieldScope &&
        Policy.Normalization == SapBpNormalizationR20B.ExactOrdinal &&
        Policy.TemporalContract is (SapRoleTemporalContractR20B.EdmDateTimeOffset or SapRoleTemporalContractR20B.R25TBusinessDate) &&
        !string.IsNullOrWhiteSpace(Policy.EvidenceReference) &&
        Entries.All(x => x.ComparisonStatus == SapBpComparisonStatusR20B.Complete);
    public bool ConsolidationCandidate => Complete && Valid;
    public string Reason { get; }
    public DateTimeOffset ReferenceAt { get; }
    public SapBpComparisonPolicyR20B Policy { get; }
    public bool TransactionalConsistencyProven => false;
    public bool CorporateCompletenessProven => false;
}
public enum SapBpChangeKindR20B { NEW_ELIGIBLE_BP, BP_MASTERDATA_CHANGED, CRM010_ROLE_CHANGED,
    ROLE_VALIDITY_CHANGED, ELIGIBILITY_EXPIRED, REMOVED_ROLE_CANDIDATE, NO_CHANGE, INDETERMINATE }
public sealed record SapBpChangeR20B(string BusinessPartner, IReadOnlyList<SapBpChangeKindR20B> Changes, string Reason)
{
    public bool GuardianWriteAuthorized => false;
    public bool GuardianDeleteOrDeactivate => false;
}
public sealed record SapBpSnapshotComparisonR20B(bool CompleteAndValidPair, IReadOnlyList<SapBpChangeR20B> Results, string Reason)
{
    public bool GuardianMappingApproved => false;
    public bool TransactionalConsistencyProven => false;
}
