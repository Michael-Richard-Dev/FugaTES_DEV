using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FugaTES.WinForms.Monitor;

internal sealed class GuardianProtocolReadbackClient(string path, string commandLockReason) : IGuardianProtocolClient
{
    public bool Configured => false;
    public bool PreviewAvailable => true;
    public string PreviewSource => "HISTORICAL_READ_ONLY - historico / somente leitura";
    public string LockReason => commandLockReason + " | HISTORICAL_REJECTION_NO_REPLAY";

    public async Task<GuardianProtocolSnapshot> PreviewAsync(Guid integrationId, CancellationToken ct)
    {
        try
        {
            var local = Path.GetFullPath(path);
            if (local.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(local)!).DriveType == DriveType.Network
                || new FileInfo(local).Length > 1048576) throw new GuardianProtocolException("HISTORICAL_PREVIEW_FILE_INVALID");
            var snapshot = JsonSerializer.Deserialize<GuardianProtocolSnapshot>(await File.ReadAllTextAsync(local, ct), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new GuardianProtocolException("HISTORICAL_PREVIEW_INVALID");
            if (integrationId == Guid.Empty || snapshot.IntegrationId != integrationId || string.IsNullOrWhiteSpace(snapshot.Contract)
                || snapshot.Version <= 0 || string.IsNullOrWhiteSpace(snapshot.SnapshotFingerprint)
                || snapshot.AuthenticationContext != "GUARDIAN_HML_BACKEND")
                throw new GuardianProtocolException("HISTORICAL_PREVIEW_IDENTITY_MISMATCH");
            if (snapshot.PayloadJson is null || !GuardianProtocolClient.ValidHash(snapshot.PayloadSha256)
                || !Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.PayloadJson))).Equals(snapshot.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                throw new GuardianProtocolException("HISTORICAL_PREVIEW_HASH_MISMATCH");
            return snapshot with
            {
                CanSend = false, BlockCode = "HISTORICAL_REJECTION_NO_REPLAY", State = "HISTORICAL_BACKEND_EXPORT",
                SafeBusinessMessage = GuardianProtocolText.Safe(snapshot.SafeBusinessMessage),
                SafeResponseBody = GuardianProtocolText.Safe(snapshot.SafeResponseBody)
            };
        }
        catch (GuardianProtocolException) { throw; }
        catch { throw new GuardianProtocolException("HISTORICAL_PREVIEW_UNAVAILABLE"); }
    }

    public Task<GuardianProtocolSnapshot> SendAsync(Guid integrationId, string payloadSha256, string previewId, bool confirm, CancellationToken ct)
        => throw new GuardianProtocolException("HISTORICAL_REJECTION_NO_REPLAY");
}
