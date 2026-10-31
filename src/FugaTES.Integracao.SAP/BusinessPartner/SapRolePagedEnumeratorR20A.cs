using System.Security.Cryptography;
using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;
namespace FugaTES.Integracao.SAP.BusinessPartner;

public sealed class SapRolePagedEnumeratorR20A(ISapRolePageReaderR20A reader, TimeProvider? clock = null)
{
    public async Task<SapRoleSnapshotR20A> EnumerateAsync(SapRoleScanLimitsR20A limits,
        IProgress<SapRoleScanProgressR20A>? progress = null, Func<bool>? interrupted = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.PageSize <= 0 || limits.MaxPages <= 0 || limits.MaxRows <= 0 ||
            (long)limits.PageSize * limits.MaxPages > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(limits));
        var time = clock ?? TimeProvider.System;
        var started = time.GetUtcNow();
        var rows = new List<SapRoleRowR20A>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var pages = new HashSet<string>(StringComparer.Ordinal);
        string? previousBp = null;
        int pageNumber = 0;
        SapRoleSnapshotR20A Finish(bool complete, string reason) =>
            new(rows, complete, reason, pageNumber, started, time.GetUtcNow());
        // Every invocation owns a fresh in-memory scan. There is no persisted partial checkpoint.
        try
        {
            while (pageNumber < limits.MaxPages)
            {
                if (cancellationToken.IsCancellationRequested) return Finish(false, "CANCELED");
                if (interrupted?.Invoke() == true) return Finish(false, "INTERRUPTED");
                SapRolePageR20A page;
                try
                {
                    page = await reader.ReadAsync(new(pageNumber, limits.PageSize,
                        checked(pageNumber * limits.PageSize)), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { return Finish(false, "CANCELED"); }
                catch { return Finish(false, "PAGE_READ_FAILED"); }
                pageNumber++;
                if (cancellationToken.IsCancellationRequested) return Finish(false, "CANCELED");
                if (interrupted?.Invoke() == true) return Finish(false, "INTERRUPTED");
                if (page?.Rows is null) return Finish(false, "INVALID_PAGE");
                // Copy the supplied collection before validation; never retain a mutable page reference.
                var batch = page.Rows.ToArray();
                if (batch.Length > limits.PageSize) return Finish(false, "PAGE_SIZE_EXCEEDED");
                // Even a short page is not terminal: require an explicit empty response.
                if (batch.Length == 0)
                {
                    progress?.Report(new(pageNumber, rows.Count, checked(pageNumber * limits.PageSize)));
                    if (cancellationToken.IsCancellationRequested) return Finish(false, "CANCELED");
                    if (interrupted?.Invoke() == true) return Finish(false, "INTERRUPTED");
                    return Finish(true, "END_OF_ENUMERATION");
                }
                if (batch.Length > limits.MaxRows - rows.Count) return Finish(false, "ROW_LIMIT");
                var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(batch)));
                if (!pages.Add(fingerprint)) return Finish(false, "REPEATED_PAGE");
                var batchKeys = new HashSet<string>(StringComparer.Ordinal);
                var lastBp = previousBp;
                foreach (var row in batch)
                {
                    if (row is null || row.BusinessPartnerRole != "CRM010") return Finish(false, "INVALID_ROLE_ROW");
                    try { CarrierPersistencePolicy.ValidateBp(row.BusinessPartner); }
                    catch { return Finish(false, "INVALID_ROLE_ROW"); }
                    if (keys.Contains(row.BusinessPartner) || !batchKeys.Add(row.BusinessPartner))
                        return Finish(false, "DUPLICATE_KEY");
                    if (lastBp is not null && StringComparer.Ordinal.Compare(row.BusinessPartner, lastBp) < 0)
                        return Finish(false, "ORDERING_REGRESSION");
                    lastBp = row.BusinessPartner;
                }
                // Add an entire validated page atomically to this local result only.
                rows.AddRange(batch); keys.UnionWith(batchKeys); previousBp = lastBp;
                progress?.Report(new(pageNumber, rows.Count, checked(pageNumber * limits.PageSize)));
            }
            return Finish(false, "PAGE_LIMIT");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Finish(false, "CANCELED"); }
        catch { return Finish(false, "SCAN_INTERRUPTED"); }
    }
}
