using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FugaTES.Callback.Http.ManualRetry;

public sealed class AcmCommandCredential
{
    readonly byte[] expected;
    public string OperatorSid { get; }
    public AcmCommandCredential(string token, string operatorSid)
    {
        if (token.Length != 64 || !token.All(char.IsAsciiHexDigit) ||
            !System.Text.RegularExpressions.Regex.IsMatch(operatorSid, @"\AS-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+\z"))
            throw new InvalidOperationException("ACM_COMMAND_CREDENTIAL_INVALID");
        expected = Encoding.ASCII.GetBytes(token); OperatorSid = operatorSid;
    }
    public bool Matches(string? token) => token is { Length: 64 } && token.All(char.IsAsciiHexDigit) &&
        CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(token));
}

public sealed class AcmCommandAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, AcmCommandCredential credential)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AcmDedicatedCommandBearer";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer))
            return Task.FromResult(AuthenticateResult.Fail("Loopback required."));
        var values = Request.Headers.Authorization;
        if (values.Count == 0) return Task.FromResult(AuthenticateResult.NoResult());
        if (values.Count != 1 || values[0] is not { } header || !header.StartsWith("Bearer ", StringComparison.Ordinal) || !credential.Matches(header[7..]))
            return Task.FromResult(AuthenticateResult.Fail("Dedicated credential required."));
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.PrimarySid, credential.OperatorSid), new Claim("permission", "acm.retry") }, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
    public static void Configure(IServiceCollection services, AcmCommandCredential credential)
    {
        services.AddSingleton(credential);
        services.AddAuthentication(SchemeName).AddScheme<AuthenticationSchemeOptions, AcmCommandAuthenticationHandler>(SchemeName, _ => { });
        services.AddAuthorization(o => o.AddPolicy(AcmManualRetryEndpoint.Policy, p => p
            .AddAuthenticationSchemes(SchemeName).RequireAuthenticatedUser().RequireClaim("permission", "acm.retry")));
    }
}
