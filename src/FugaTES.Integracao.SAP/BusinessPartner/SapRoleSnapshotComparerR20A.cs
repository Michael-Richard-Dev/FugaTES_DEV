namespace FugaTES.Integracao.SAP.BusinessPartner;

public sealed class SapRoleSnapshotComparerR20A
{
    public SapRoleComparisonR20A Compare(SapRoleSnapshotR20A previous,
        SapRoleSnapshotR20A current, DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (!previous.Complete || !current.Complete)
            return new(false, Array.Empty<SapRoleCandidateR20A>(), "INCOMPLETE_SCAN_NOT_CONSOLIDATABLE");
        if (evaluatedAt < previous.EndedAt || evaluatedAt < current.EndedAt)
            return new(false, Array.Empty<SapRoleCandidateR20A>(), "EVALUATION_PRECEDES_SCAN");
        var oldRows = previous.Rows.ToDictionary(x => x.BusinessPartner, StringComparer.Ordinal);
        var newRows = current.Rows.ToDictionary(x => x.BusinessPartner, StringComparer.Ordinal);
        var result = new List<SapRoleCandidateR20A>();
        foreach (var bp in oldRows.Keys.Union(newRows.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            oldRows.TryGetValue(bp, out var oldRow); newRows.TryGetValue(bp, out var newRow);
            var before = oldRow is null ? 0 : Eligible(oldRow, previous.EndedAt);
            var after = newRow is null ? 0 : Eligible(newRow, evaluatedAt);
            SapRoleCandidateKindR20A kind;
            string reason;
            if (before < 0 || after < 0)
            { kind = SapRoleCandidateKindR20A.INDETERMINATE; reason = "ROLE_DATE_OR_TEMPORAL_SEMANTICS_AMBIGUOUS"; }
            else if (newRow is null)
            { kind = SapRoleCandidateKindR20A.REMOVED_ROLE_CANDIDATE; reason = "MISSING_IN_COMPLETE_ENUMERATION_ONLY"; }
            else if (before == 1 && after == 0)
            { kind = SapRoleCandidateKindR20A.ELIGIBILITY_EXPIRED; reason = "ELIGIBILITY_REEVALUATED"; }
            else if (oldRow is null && after == 1)
            { kind = SapRoleCandidateKindR20A.NEW_ELIGIBLE_BP; reason = "NEW_ELIGIBLE_ROLE"; }
            else if (oldRow is null)
            { kind = SapRoleCandidateKindR20A.CHANGED_BP_ROLE; reason = "NEW_ROLE_NOT_CURRENTLY_ELIGIBLE"; }
            else if (oldRow.ValidFrom != newRow.ValidFrom || oldRow.ValidTo != newRow.ValidTo)
            { kind = SapRoleCandidateKindR20A.ROLE_VALIDITY_CHANGED; reason = "ROLE_VALIDITY_FIELDS_CHANGED"; }
            else if (before != after)
            { kind = SapRoleCandidateKindR20A.CHANGED_BP_ROLE; reason = "ROLE_ELIGIBILITY_TRANSITION"; }
            else
            { kind = SapRoleCandidateKindR20A.NO_CHANGE; reason = "ROLE_FIELDS_ONLY_UNCHANGED"; }
            // Role enumeration cannot detect or certify changes to BP master data.
            // Every present eligible BP still requires a master GET before operational comparison.
            result.Add(new(bp, kind, reason, newRow is not null && (after == 1 || after < 0)));
        }
        return new(true, result.AsReadOnly(), "COMPLETE_ENUMERATIONS_ONLY_NOT_TRANSACTIONAL_SNAPSHOT");
    }

    static int Eligible(SapRoleRowR20A row, DateTimeOffset reference)
    {
        var from = CarrierManualODataDateParser.Parse(row.ValidFrom);
        var to = CarrierManualODataDateParser.Parse(row.ValidTo);
        if (from is null || to is null || from > to) return -1;
        // Same conservative uncertainty envelope as R15: no invented SAP timezone,
        // inclusive end-of-day convention or guaranteed instant interpretation.
        var start = Bounds(from.Value, false); var end = Bounds(to.Value, true);
        if (reference >= start.Latest && reference < end.Earliest) return 1;
        if (reference >= start.Earliest && reference <= end.Latest) return -1;
        return 0;
    }
    static (DateTimeOffset Earliest, DateTimeOffset Latest) Bounds(DateTimeOffset value, bool end)
    {
        var values = new List<DateTimeOffset> { value };
        foreach (var date in new[] { value.UtcDateTime.Date, value.Date })
        {
            var ticks = date.Ticks + (end ? TimeSpan.TicksPerDay : 0);
            foreach (var shift in new[] { -14, 14 })
                values.Add(new DateTimeOffset(Math.Clamp(ticks + TimeSpan.FromHours(shift).Ticks,
                    DateTimeOffset.MinValue.Ticks, DateTimeOffset.MaxValue.Ticks), TimeSpan.Zero));
        }
        return (values.Min(), values.Max());
    }
}
