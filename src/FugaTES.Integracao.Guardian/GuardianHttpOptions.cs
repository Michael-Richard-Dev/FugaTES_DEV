namespace FugaTES.Integracao.Guardian;

public sealed record GuardianHttpOptions
{
    public Uri? BaseUrl { get; init; }
    public TimeSpan Timeout { get; init; }
    public string? TransportadoraEndpoint { get; init; }
    public string? PreCadastroEndpoint { get; init; }
    public string? TokenUserField { get; init; }
    public string? TokenPasswordField { get; init; }
    public string? TokenResponseField { get; init; }
    public string? PreCadastroProtocolResponseField { get; init; }

    internal Uri ResolveEndpoint(string? endpoint, string setting)
    {
        if (BaseUrl is null || !BaseUrl.IsAbsoluteUri || BaseUrl.Scheme is not ("http" or "https"))
            throw new GuardianHttpException("Guardian BaseUrl is required and must use HTTP or HTTPS.");
        if (Timeout <= TimeSpan.Zero)
            throw new GuardianHttpException("Guardian timeout must be positive.");
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new GuardianHttpException($"Guardian setting {setting} is required.");
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            throw new GuardianHttpException($"Guardian setting {setting} must be a relative endpoint.");

        return new Uri(BaseUrl, endpoint);
    }

    internal static string RequireField(string? value, string setting)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new GuardianHttpException($"Guardian setting {setting} is required.");
        return value;
    }
}

public sealed class GuardianHttpException(string message, Exception? innerException = null)
    : Exception(message, innerException);
