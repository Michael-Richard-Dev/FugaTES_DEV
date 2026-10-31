using System.Net;
using System.Text.RegularExpressions;

namespace FugaTES.Callback.Http;

public sealed record MasterdataCallbackOptions(bool Enabled, string StoreDirectory,
    IReadOnlyList<IPAddress> AllowedRemoteAddresses, string AuthenticationHeaderName,
    string SharedSecretEnvironmentVariable, bool AllowLoopbackHttp, bool Configured)
{
    public const int MaximumBodyBytes = 64 * 1024;
    // Production private HTTP is separate from the existing isolated-loopback smoke switch.
    public bool AllowPrivateHmlProxyHttp { get; init; }

    public static MasterdataCallbackOptions Parse(IConfiguration configuration)
    {
        var section = configuration.GetSection("MasterdataCallback");
        var enabled = bool.TryParse(section["Enabled"], out var value) && value;
        var directory = section["StoreDirectory"] ?? "";
        var header = section["AuthenticationHeaderName"] ?? "";
        var variable = section["SharedSecretEnvironmentVariable"] ?? "";
        var peers = section.GetSection("AllowedRemoteAddresses").GetChildren().Select(x => x.Value).ToArray();
        var addresses = new List<IPAddress>();
        var validPeers = peers.Length > 0;
        foreach (var peer in peers)
        {
            if (!IPAddress.TryParse(peer, out var address)) { validPeers = false; continue; }
            addresses.Add(Normalize(address));
        }
        var configured = validPeers && Path.IsPathFullyQualified(directory)
            && Regex.IsMatch(header, "\\A[A-Za-z0-9-]{1,64}\\z")
            && Regex.IsMatch(variable, "\\A[A-Za-z_][A-Za-z0-9_]{0,127}\\z");
        static bool True(string? value) => bool.TryParse(value, out var result) && result;
        return new(enabled, directory, addresses.ToArray(), header, variable,
            True(section["AllowLoopbackHttp"]), configured)
        {
            AllowPrivateHmlProxyHttp = True(section["PrivateHmlHttpApproved"])
                && section["Environment"] == "HML" && True(section["PrivateTransportVerified"])
                && True(section["GuardianSourceVerified"]) && section["GuardianSourceAddress"] == "192.168.17.91"
                && section["TrustedProxyAddress"] == "127.0.0.1"
                && !string.IsNullOrWhiteSpace(section["ApprovalReference"])
                && addresses.Count == 1 && addresses[0].Equals(IPAddress.Loopback)
        };
    }

    internal static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
