using System.Security.Cryptography;
using System.Text;

namespace FugaTES.WinForms.Monitor;

public sealed class GuardianProtocolController(IGuardianProtocolClient? client)
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, string> attempted = new();
    private Guid? selectedId;
    private string? selectedContract;
    private long revision;
    private bool pending;
    private GuardianProtocolSnapshot? snapshot;
    private string? error;
    public GuardianProtocolSnapshot? Snapshot { get { lock (sync) return snapshot; } }
    public bool OperationPending { get { lock (sync) return pending; } }
    public bool CanGenerate { get { lock (sync) return client?.PreviewAvailable == true && selectedId is not null && !pending; } }
    public string PreviewSource => client?.PreviewSource ?? "BACKEND_COMMAND";
    public bool CanSend { get { lock (sync) return Reason() == ""; } }
    public string LockReason { get { lock (sync) return Reason(); } }

    public void Select(Guid? id, string? contract)
    {
        lock (sync)
        {
            if (id == selectedId && contract == selectedContract) return;
            selectedId = id == Guid.Empty ? null : id;
            selectedContract = contract;
            revision++;
            snapshot = null;
            error = null;
        }
    }

    public async Task GeneratePreviewAsync(CancellationToken ct)
    {
        Guid id;
        string? contract;
        long captured;
        lock (sync)
        {
            if (!CanGenerate) return;
            id = selectedId!.Value;
            contract = selectedContract;
            captured = ++revision;
            snapshot = null;
            error = null;
            pending = true;
        }
        try
        {
            var result = await client!.PreviewAsync(id, ct);
            if (result.IntegrationId != id || result.Contract != contract)
                throw new GuardianProtocolException("PREVIEW_IDENTITY_MISMATCH");
            lock (sync) { if (revision == captured) snapshot = result; }
        }
        catch (Exception ex)
        {
            lock (sync) { if (revision == captured) error = ex is GuardianProtocolException ? ex.Message : "BACKEND_PREVIEW_UNAVAILABLE"; }
        }
        finally { lock (sync) pending = false; }
    }

    public async Task SendAsync(bool confirmed, CancellationToken ct)
    {
        Guid id;
        long captured;
        string hash;
        string previewId;
        lock (sync)
        {
            if (!confirmed || !CanSend || ct.IsCancellationRequested) return;
            id = selectedId!.Value;
            captured = revision;
            hash = snapshot!.PayloadSha256!;
            previewId = snapshot.PreviewId!;
            // Reserve before the await: a second click or a refresh cannot issue another command.
            attempted[id] = "SEND_ATTEMPTED";
            pending = true;
        }
        try
        {
            var result = await client!.SendAsync(id, hash, previewId, true, ct);
            lock (sync)
            {
                attempted[id] = result.ProtocolId is not null || IsSuccessful(result.State) ? "SEND_SUCCEEDED" :
                    IsUnknown(result.State) ? "SEND_RESULT_UNKNOWN" : "SEND_ATTEMPTED";
                if (revision == captured) snapshot = result;
            }
        }
        catch
        {
            lock (sync) attempted[id] = "SEND_RESULT_UNKNOWN";
        }
        finally { lock (sync) pending = false; }
    }

    private string Reason()
    {
        if (pending) return "OPERATION_PENDING";
        if (error is not null) return error;
        if (client?.Configured != true) return client?.LockReason ?? "BACKEND_COMMAND_DISABLED";
        if (selectedId is null || string.IsNullOrWhiteSpace(selectedContract)) return "SELECTED_IDENTITY_REQUIRED";
        if (attempted.TryGetValue(selectedId.Value, out var reason)) return reason;
        if (snapshot is null) return "PREVIEW_REQUIRED";
        if (snapshot.IntegrationId != selectedId || snapshot.Contract != selectedContract || snapshot.Version <= 0
            || string.IsNullOrWhiteSpace(snapshot.SnapshotFingerprint)) return "PREVIEW_IDENTITY_MISMATCH";
        if (snapshot.AuthenticationContext != "GUARDIAN_HML_BACKEND") return "BACKEND_AUTH_CONTEXT_INVALID";
        if (!snapshot.CanSend || !string.IsNullOrWhiteSpace(snapshot.BlockCode)) return GuardianProtocolText.Safe(snapshot.BlockCode) is { Length: > 0 } code ? code : "BACKEND_SEND_BLOCKED";
        if (snapshot.State != "READY" || snapshot.ProtocolId is not null) return "BACKEND_STATE_LOCKED";
        if (!GuardianProtocolClient.ValidHash(snapshot.PreviewId)) return "PREVIEW_ID_INVALID";
        if (snapshot.PayloadJson is null || !GuardianProtocolClient.ValidHash(snapshot.PayloadSha256)
            || !Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.PayloadJson))).Equals(snapshot.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            return "PREVIEW_HASH_INVALID";
        return "";
    }

    private static bool IsSuccessful(string? state) => state is not null &&
        (state.Contains("SUCC", StringComparison.OrdinalIgnoreCase) || state.Contains("SENT", StringComparison.OrdinalIgnoreCase)
        || state.Contains("CONCLUI", StringComparison.OrdinalIgnoreCase) || state.Contains("COMPLETED", StringComparison.OrdinalIgnoreCase)
        || state.Contains("SUCESSO", StringComparison.OrdinalIgnoreCase) || state.Contains("ENVIADO", StringComparison.OrdinalIgnoreCase));
    private static bool IsUnknown(string? state) => state is not null &&
        (state.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase) || state.Contains("INDETERMIN", StringComparison.OrdinalIgnoreCase));
}
