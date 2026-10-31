using Microsoft.Extensions.Configuration;

namespace FugaTES.Integracao.Guardian.Transportadoras;

// Evidence-bound exception for this inbound callback URL only. Never a transport bypass.
public sealed record MasterdataPrivateHttpBindingR159B
{
    public string? Environment { get; init; }
    public bool Approved { get; init; }
    public bool PrivateTransportVerified { get; init; }
    public bool GuardianSourceVerified { get; init; }
    public string? GuardianSourceAddress { get; init; }
    public string? ApprovalReference { get; init; }

    public static MasterdataPrivateHttpBindingR159B FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Carrier:SingleCreate:PrivateHttpCallback");
        static bool True(string? value) => bool.TryParse(value, out var result) && result;
        return new()
        {
            Environment = configuration["Carrier:Environment"],
            Approved = True(section["Approved"]),
            PrivateTransportVerified = True(section["PrivateTransportVerified"]),
            GuardianSourceVerified = True(section["GuardianSourceVerified"]),
            GuardianSourceAddress = section["GuardianSourceAddress"],
            ApprovalReference = section["ApprovalReference"]
        };
    }
}

public static class MasterdataCallbackUrlPolicyR159B
{
    public const string PrivateHmlUrl = "http://192.168.17.40/api/guardian/masterdata/transportadoras/callback";

    public static bool IsAllowed(string? value, MasterdataPrivateHttpBindingR159B? binding = null)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true; // Existing HTTPS contract unchanged.
        return string.Equals(value, PrivateHmlUrl, StringComparison.Ordinal)
            && binding is { Environment: "HML", Approved: true, PrivateTransportVerified: true,
                GuardianSourceVerified: true, GuardianSourceAddress: "192.168.17.91" }
            && !string.IsNullOrWhiteSpace(binding.ApprovalReference);
    }
}
