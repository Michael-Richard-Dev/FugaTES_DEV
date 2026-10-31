namespace FugaTES.Integracao.SAP.BusinessPartner;

public sealed class SapBpEnrichedSnapshotComparerR20B
{
    public SapBpSnapshotComparisonR20B Compare(SapBpEnrichedSnapshotR20B previous, SapBpEnrichedSnapshotR20B current)
    {
        ArgumentNullException.ThrowIfNull(previous); ArgumentNullException.ThrowIfNull(current);
        var keys = previous.Roles.Rows.Select(x => x.BusinessPartner).Union(current.Roles.Rows.Select(x => x.BusinessPartner),
            StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        SapBpSnapshotComparisonR20B Unknown(string reason) => new(false, Array.AsReadOnly(keys.Select(bp =>
            new SapBpChangeR20B(bp, Array.AsReadOnly(new[]{SapBpChangeKindR20B.INDETERMINATE}), reason)).ToArray()), reason);
        if (!previous.Complete || !current.Complete) return Unknown("INCOMPLETE_ENRICHED_SNAPSHOT");
        if (!previous.Valid || !current.Valid) return Unknown("INVALID_OR_INCOMPLETE_COMPARISON_SCOPE");
        if (previous.Policy.ScopeKey != current.Policy.ScopeKey) return Unknown("COMPARISON_SCOPE_MISMATCH");
        if (current.ReferenceAt < previous.ReferenceAt) return Unknown("SNAPSHOT_REFERENCE_ORDER_INVALID");
        var oldRoles = previous.Roles.Rows.ToDictionary(x => x.BusinessPartner, StringComparer.Ordinal);
        var newRoles = current.Roles.Rows.ToDictionary(x => x.BusinessPartner, StringComparer.Ordinal);
        var oldBp = previous.Entries.ToDictionary(x => x.BusinessPartner, StringComparer.Ordinal);
        var newBp = current.Entries.ToDictionary(x => x.BusinessPartner, StringComparer.Ordinal);
        var results = new List<SapBpChangeR20B>();
        foreach (var bp in keys)
        {
            oldRoles.TryGetValue(bp, out var oldRole); newRoles.TryGetValue(bp, out var newRole);
            var before = oldRole is null ? 0 : SapBpSnapshotEnricherR20B.Eligibility(oldRole, previous.ReferenceAt, previous.Policy.TemporalContract);
            var after = newRole is null ? 0 : SapBpSnapshotEnricherR20B.Eligibility(newRole, current.ReferenceAt, current.Policy.TemporalContract);
            var changes = new List<SapBpChangeKindR20B>();
            if (before < 0 || after < 0) changes.Add(SapBpChangeKindR20B.INDETERMINATE);
            else if (newRole is null) changes.Add(SapBpChangeKindR20B.REMOVED_ROLE_CANDIDATE);
            else if (oldRole is null) changes.Add(after == 1 ? SapBpChangeKindR20B.NEW_ELIGIBLE_BP : SapBpChangeKindR20B.CRM010_ROLE_CHANGED);
            else
            {
                var oldData = oldBp[bp].NormalizedSapData; var newData = newBp[bp].NormalizedSapData;
                if (oldData.Count != newData.Count || oldData.Any(p => !newData.TryGetValue(p.Key, out var value) || value != p.Value))
                    changes.Add(SapBpChangeKindR20B.BP_MASTERDATA_CHANGED);
                if (CarrierManualODataDateParser.Parse(oldRole.ValidFrom) != CarrierManualODataDateParser.Parse(newRole.ValidFrom) ||
                    CarrierManualODataDateParser.Parse(oldRole.ValidTo) != CarrierManualODataDateParser.Parse(newRole.ValidTo))
                    changes.Add(SapBpChangeKindR20B.ROLE_VALIDITY_CHANGED);
                if (before == 1 && after == 0) changes.Add(SapBpChangeKindR20B.ELIGIBILITY_EXPIRED);
                else if (before != after) changes.Add(SapBpChangeKindR20B.CRM010_ROLE_CHANGED);
                if (changes.Count == 0) changes.Add(SapBpChangeKindR20B.NO_CHANGE);
            }
            results.Add(new(bp, changes.AsReadOnly(), "APPROVED_SAP_FIELD_SCOPE_ONLY_NO_GUARDIAN_AUTHORIZATION"));
        }
        return new(true, results.AsReadOnly(), "COMPLETE_VALID_ENRICHED_ENUMERATIONS_NOT_TRANSACTIONAL");
    }
}
