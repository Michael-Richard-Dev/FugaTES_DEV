using System.Text;
using System.Text.Json;
namespace FugaTES.Integracao.SAP.BusinessPartner;

public sealed class SapBpSnapshotEnricherR20B(ISapBpMasterReaderR20B reader, TimeProvider? clock = null)
{
    public async Task<SapBpEnrichedSnapshotR20B> EnrichAsync(SapRoleSnapshotR20A roles,
        SapBpComparisonPolicyR20B policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader); ArgumentNullException.ThrowIfNull(roles); ArgumentNullException.ThrowIfNull(policy);
        // Read time once, before any master-data GET: every BP uses this exact reference.
        var reference = (clock ?? TimeProvider.System).GetUtcNow();
        var entries = new List<SapBpEnrichedEntryR20B>();
        SapBpEnrichedSnapshotR20B Finish(bool complete, string reason) => new(roles, entries, complete, reason, reference, policy);
        if (!roles.Complete) return Finish(false, "ROLE_SCAN_INCOMPLETE");
        if (reference < roles.EndedAt) return Finish(false, "ENRICHMENT_REFERENCE_PRECEDES_SCAN");
        if (!policy.Approved || string.IsNullOrWhiteSpace(policy.EvidenceReference) || policy.Fields.Count == 0 ||
            policy.Fields.Any(f => !Enum.IsDefined(f)) || policy.Fields.Distinct().Count() != policy.Fields.Count)
            return Finish(false, "BP_COMPARISON_POLICY_NOT_APPROVED");
        foreach (var role in roles.Rows)
        {
            if (cancellationToken.IsCancellationRequested) return Finish(false, "CANCELED");
            SapBpReadResultR20B data;
            try { data = await reader.ReadAsync(role.BusinessPartner, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Finish(false, "CANCELED"); }
            catch { return Finish(false, "BP_READ_FAILED"); }
            if (cancellationToken.IsCancellationRequested) return Finish(false, "CANCELED");
            if (data is null) return Finish(false, "BP_READ_FAILED");
            if (!data.Complete) return Finish(false, SafeReason(data.Reason));
            var raw = data.RawSapData;
            if (SapBpReadAdapterR20B.RawProblem(raw) is { } rawProblem) return Finish(false, rawProblem);
            if (data.BusinessPartner != role.BusinessPartner || !raw.TryGetProperty("BusinessPartner", out var rawBp) ||
                rawBp.ValueKind != JsonValueKind.String || rawBp.GetString() != role.BusinessPartner)
                return Finish(false, "BP_IDENTITY_MISMATCH");
            if (!RoleMatches(raw, role)) return Finish(false, "BP_ROLE_CHANGED_DURING_ENRICHMENT");
            var normalized = new Dictionary<string,string>(StringComparer.Ordinal);
            string reason = "APPROVED_EXACT_FIELD_SCOPE_COMPLETE";
            var status = SapBpComparisonStatusR20B.Complete;
            if (policy.Normalization != SapBpNormalizationR20B.ExactOrdinal)
            { status = SapBpComparisonStatusR20B.Indeterminate; reason = "NORMALIZATION_NOT_APPROVED"; }
            else
            {
                foreach (var field in policy.Fields)
                {
                    if (!TryProject(raw, field, out var value))
                    { status = SapBpComparisonStatusR20B.Indeterminate; reason = "REQUIRED_BP_FIELD_MISSING_OR_INVALID"; continue; }
                    normalized[field.ToString()] = Canonical(value);
                }
            }
            if (!policy.CompleteFieldScope)
            { status = SapBpComparisonStatusR20B.Indeterminate; reason = "BP_COMPARISON_SCOPE_INCOMPLETE"; }
            if (Eligibility(role, reference, policy.TemporalContract) < 0)
            { status = SapBpComparisonStatusR20B.Indeterminate; reason = "ROLE_TEMPORAL_INDETERMINATE"; }
            entries.Add(new(role.BusinessPartner, raw, normalized, status, reason));
        }
        if (cancellationToken.IsCancellationRequested) return Finish(false, "CANCELED");
        return Finish(true, entries.All(x => x.ComparisonStatus == SapBpComparisonStatusR20B.Complete)
            ? "BP_ENRICHMENT_COMPLETE" : "BP_ENRICHMENT_INDETERMINATE");
    }
    static string SafeReason(string? reason) => reason switch
    {
        "BP_NAVIGATION_INCOMPLETE" or "BP_DUPLICATE_FIELD_CONFLICT" or "BP_IDENTITY_MISMATCH" or
        "BP_HTTP_GET_FAILED" or "BP_READ_FAILED" or "BP_RAW_INVALID" => reason,
        _ => "BP_READ_INCOMPLETE"
    };
    internal static int Eligibility(SapRoleRowR20A row, DateTimeOffset reference, SapRoleTemporalContractR20B contract)
    {
        if(contract==SapRoleTemporalContractR20B.R25TBusinessDate)
        {
            if(row.BusinessPartnerRole!="CRM010")return -1;
            var status=SapSnapshotWhitelistProjectionR24.EvaluateApprovedTemporal(row.ValidFrom,row.ValidTo,reference).Status;
            return status switch
            {
                FugaTES.Aplicacao.Transportadoras.SnapshotsR24.AvaliacaoCrm010R24.EVALUATED_VALID=>1,
                FugaTES.Aplicacao.Transportadoras.SnapshotsR24.AvaliacaoCrm010R24.EVALUATED_INVALID=>0,
                _=>-1
            };
        }
        if (contract != SapRoleTemporalContractR20B.EdmDateTimeOffset || row.BusinessPartnerRole != "CRM010") return -1;
        var from = CarrierManualODataDateParser.Parse(row.ValidFrom); var to = CarrierManualODataDateParser.Parse(row.ValidTo);
        if (from is null || to is null || from >= to) return -1;
        // Exact Edm.DateTimeOffset instants. No timezone selection or end-of-day extension.
        // The endpoint boundary inclusion rule is not approved: equality is indeterminate.
        if (reference == from || reference == to) return -1;
        return reference > from && reference < to ? 1 : 0;
    }
    static bool RoleMatches(JsonElement raw, SapRoleRowR20A expected)
    {
        // When the existing expanded GET returns roles, reject a detectable mid-scan change.
        if (!raw.TryGetProperty("to_BusinessPartnerRole", out var nav)) return true;
        if (nav.ValueKind != JsonValueKind.Object || !nav.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array) return false;
        var crm = rows.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object &&
            x.TryGetProperty("BusinessPartnerRole", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() == "CRM010").ToArray();
        if (crm.Length != 1) return false;
        var item = crm[0];
        if (item.TryGetProperty("BusinessPartner", out var bp) && (bp.ValueKind != JsonValueKind.String || bp.GetString() != expected.BusinessPartner)) return false;
        static string? Text(JsonElement v, string name) => v.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        return CarrierManualODataDateParser.Parse(Text(item,"ValidFrom")) is { } from &&
            CarrierManualODataDateParser.Parse(Text(item,"ValidTo")) is { } to &&
            from == CarrierManualODataDateParser.Parse(expected.ValidFrom) && to == CarrierManualODataDateParser.Parse(expected.ValidTo);
    }
    static bool TryProject(JsonElement raw, SapBpFieldR20B field, out JsonElement projected)
    {
        projected = default;
        if (field == SapBpFieldR20B.BusinessPartnerTax)
            return ProjectCollection(raw, "to_BusinessPartnerTax", ["BPTaxType","BPTaxNumber","BPTaxLongNumber"], false, out projected);
        if (field == SapBpFieldR20B.BusinessPartnerAddress)
            return ProjectCollection(raw, "to_BusinessPartnerAddress", ["AddressID","StreetName","CityName","Region","PostalCode","Country"], true, out projected);
        if (!raw.TryGetProperty(field.ToString(), out projected)) return false;
        return field switch
        {
            SapBpFieldR20B.BusinessPartnerCategory => projected.ValueKind == JsonValueKind.Number && projected.TryGetInt32(out _),
            SapBpFieldR20B.BusinessPartnerIsBlocked => projected.ValueKind is JsonValueKind.True or JsonValueKind.False ||
                projected.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(projected.GetString()),
            _ => projected.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(projected.GetString())
        };
    }
    static bool ProjectCollection(JsonElement parent, string property, string[] fields, bool address, out JsonElement projected)
    {
        projected = default;
        if (!parent.TryGetProperty(property, out var nav) || nav.ValueKind != JsonValueKind.Object ||
            !nav.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array) return false;
        var projectedRows = new List<Dictionary<string,JsonElement>>();
        var addressKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) return false;
            var values = new Dictionary<string,JsonElement>(StringComparer.Ordinal);
            foreach (var key in fields)
            {
                if (!row.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return false;
                values[key] = value.Clone();
            }
            if (address)
            {
                var id = values["AddressID"].GetString();
                if (string.IsNullOrWhiteSpace(id) || !addressKeys.Add(id)) return false;
                foreach (var (name, columns) in new[] {
                    ("to_AddressUsage", new[] {"AddressID","AddressUsage","ValidityStartDate","ValidityEndDate"}),
                    ("to_PhoneNumber", new[] {"PhoneNumber"}), ("to_EmailAddress", new[] {"EmailAddress"}) })
                {
                    if (!ProjectCollection(row, name, columns, false, out var child)) return false;
                    values[name] = child;
                }
            }
            projectedRows.Add(values);
        }
        projected = JsonSerializer.SerializeToElement(projectedRows);
        return true;
    }
    static string Canonical(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(value, writer);
        return Encoding.UTF8.GetString(stream.ToArray());
        static void Write(JsonElement v, Utf8JsonWriter w)
        {
            if (v.ValueKind == JsonValueKind.Object)
            {
                w.WriteStartObject();
                foreach (var p in v.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                { w.WritePropertyName(p.Name); Write(p.Value, w); }
                w.WriteEndObject();
            }
            else if (v.ValueKind == JsonValueKind.Array)
            { w.WriteStartArray(); foreach (var item in v.EnumerateArray()) Write(item,w); w.WriteEndArray(); }
            else v.WriteTo(w);
        }
    }
}
