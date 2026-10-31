using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.WinForms.Runtime;

namespace FugaTES.WinForms.Monitor;

public sealed record GuardianProtocolProfile
{
    public bool Enabled { get; init; }
    public string BackendBaseUrl { get; init; } = "";
    public string CommandCredentialTarget { get; init; } = "";
    public string CommandCredentialUsername { get; init; } = "";
    public string OperatorSid { get; init; } = "";
    public bool ApprovedSshTunnel { get; init; }
    public string SshTunnelApprovalReference { get; init; } = "";

    internal bool TryGetBackend(out Uri? backend)
    {
        backend = null;
        if (!Enabled || string.IsNullOrWhiteSpace(CommandCredentialTarget) || string.IsNullOrWhiteSpace(OperatorSid)
            || !OperatorSid.StartsWith("S-1-", StringComparison.Ordinal) || OperatorSid.Any(char.IsControl)
            || CommandCredentialTarget.Contains("guardian", StringComparison.OrdinalIgnoreCase)
            || CommandCredentialTarget.Contains("postgres", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(BackendBaseUrl, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/") return false;
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback
            && ApprovedSshTunnel && !string.IsNullOrWhiteSpace(SshTunnelApprovalReference))) return false;
        backend = uri;
        return true;
    }
}

public sealed class GuardianProtocolSession : IDisposable
{
    public IGuardianProtocolClient Client { get; }
    private GuardianProtocolSession(IGuardianProtocolClient client) => Client = client;

    public static GuardianProtocolSession Create(GuardianProtocolProfile profile,
        Func<string, string, string>? readCredential = null, HttpMessageHandler? handler = null,
        Func<string?>? getOperatorSid = null, string? previewReadbackFile = null)
    {
        if (!string.IsNullOrWhiteSpace(previewReadbackFile))
        {
            handler?.Dispose();
            var blocked = !profile.Enabled ? "BACKEND_COMMAND_DISABLED" : "BACKEND_SEND_BLOCKED";
            if (string.IsNullOrWhiteSpace(profile.CommandCredentialTarget)) blocked += " | COMMAND_CREDENTIAL_NOT_PROVISIONED";
            return new(new GuardianProtocolReadbackClient(previewReadbackFile, blocked));
        }
        if (!profile.Enabled) { handler?.Dispose(); return new(new Disabled("BACKEND_COMMAND_DISABLED")); }
        if (!profile.TryGetBackend(out _)) { handler?.Dispose(); return new(new Disabled("BACKEND_COMMAND_CONFIG_REQUIRED")); }
        try
        {
            var actualSid = (getOperatorSid ?? GuardianProtocolClient.CurrentOperatorSid)();
            if (string.IsNullOrWhiteSpace(actualSid) || actualSid != profile.OperatorSid)
            {
                handler?.Dispose();
                return new(new Disabled("BACKEND_OPERATOR_SID_MISMATCH"));
            }
            var expectedUser = string.IsNullOrWhiteSpace(profile.CommandCredentialUsername) ? actualSid : profile.CommandCredentialUsername;
            var credential = (readCredential ?? WindowsCredentialProvider.ReadPassword)(profile.CommandCredentialTarget, expectedUser);
            return new(new GuardianProtocolClient(profile, credential, handler ?? GuardianProtocolClient.CreateTransport(), () => actualSid));
        }
        catch
        {
            handler?.Dispose();
            return new(new Disabled("BACKEND_COMMAND_CREDENTIAL_UNAVAILABLE"));
        }
    }

    public static GuardianProtocolSession Load(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "AcmMonitor.R165M.json");
            if (!File.Exists(path)) return Create(new());
            var profile = JsonSerializer.Deserialize<AcmMonitorProfile>(File.ReadAllText(path),
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
            var readback = string.IsNullOrWhiteSpace(profile?.ProtocolPreviewReadbackFile) ? null : Path.GetFullPath(profile.ProtocolPreviewReadbackFile, directory);
            return Create(profile?.GuardianProtocol ?? new(), previewReadbackFile: readback);
        }
        catch { return new(new Disabled("BACKEND_COMMAND_CONFIG_REQUIRED")); }
    }

    public void Dispose() => (Client as IDisposable)?.Dispose();

    private sealed class Disabled(string reason) : IGuardianProtocolClient
    {
        public bool Configured => false;
        public bool PreviewAvailable => false;
        public string PreviewSource => "BACKEND_COMMAND";
        public string LockReason => reason;
        public Task<GuardianProtocolSnapshot> PreviewAsync(Guid integrationId, CancellationToken ct) => throw new GuardianProtocolException(reason);
        public Task<GuardianProtocolSnapshot> SendAsync(Guid integrationId, string payloadSha256, string previewId, bool confirm, CancellationToken ct) => throw new GuardianProtocolException(reason);
    }
}
