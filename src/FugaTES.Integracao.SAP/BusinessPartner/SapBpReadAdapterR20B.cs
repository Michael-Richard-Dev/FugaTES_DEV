using System.Net;
using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Integracao.SAP.Http;
namespace FugaTES.Integracao.SAP.BusinessPartner;

public sealed class SapBpReadAdapterR20B(ClienteHttpParceiroNegocioSap client) : ISapBpMasterReaderR20B
{
    public async Task<SapBpReadResultR20B> ReadAsync(string businessPartner, CancellationToken cancellationToken)
    {
        CarrierPersistencePolicy.ValidateBp(businessPartner);
        SapBpReadResultR20B Failed(string reason) => new(businessPartner,
            JsonSerializer.SerializeToElement(new { BusinessPartner = businessPartner }), false, reason);
        try
        {
            // Reuse the existing SAP GET, expansions, SAP client and authentication composition.
            var response = await client.ObterAsync(businessPartner, cancellationToken, solicitarJson: true).ConfigureAwait(false);
            if (response.FalhaConfiguracao is not null || response.StatusCode != HttpStatusCode.OK || response.Conteudo is null)
                return Failed("BP_HTTP_GET_FAILED");
            using var document = JsonDocument.Parse(response.Conteudo);
            if (HasDuplicateFields(document.RootElement)) return Failed("BP_DUPLICATE_FIELD_CONFLICT");
            if (!document.RootElement.TryGetProperty("d", out var raw) || raw.ValueKind != JsonValueKind.Object)
                return Failed("BP_RAW_INVALID");
            if (RawProblem(raw) is { } problem) return Failed(problem);
            if (!raw.TryGetProperty("BusinessPartner", out var bp) || bp.ValueKind != JsonValueKind.String || bp.GetString() != businessPartner)
                return Failed("BP_IDENTITY_MISMATCH");
            return new(businessPartner, raw.Clone());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Failed("BP_READ_FAILED"); }
    }
    internal static string? RawProblem(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return "BP_RAW_INVALID";
        if (HasDuplicateFields(raw)) return "BP_DUPLICATE_FIELD_CONFLICT";
        return HasContinuation(raw) ? "BP_NAVIGATION_INCOMPLETE" : null;
    }
    static bool HasDuplicateFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
                if (!names.Add(p.Name) || HasDuplicateFields(p.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (HasDuplicateFields(item)) return true;
        return false;
    }
    static bool HasContinuation(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var p in value.EnumerateObject())
                if (p.Name == "__next" || HasContinuation(p.Value)) return true;
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (HasContinuation(item)) return true;
        return false;
    }
}
