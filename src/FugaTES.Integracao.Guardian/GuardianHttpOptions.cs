namespace FugaTES.Integracao.Guardian;

public sealed record GuardianHttpOptions
{
    public Uri? BaseUrl { get; init; }
    public TimeSpan Timeout { get; init; }
    public TimeSpan TokenCacheLifetime { get; init; } = TimeSpan.FromMinutes(30);
    public string? TransportadoraEndpoint { get; init; }
    public string? PreCadastroEndpoint { get; init; }
    public Uri? ContratoSoapEndpoint { get; init; }
    public bool ContratoSoapInternalHmlHttpApproved { get; init; }
    public string? ContratoSoapEnvironment { get; init; }
    internal void ValidateContratoSoapEndpoint()
    {
        var uri=ContratoSoapEndpoint;
        if(uri is null || !uri.IsAbsoluteUri || Timeout<=TimeSpan.Zero || !string.IsNullOrEmpty(uri.UserInfo) || uri.Scheme is not ("https" or "http"))
            throw new GuardianHttpException("Guardian SOAP profile is invalid.");
        if(uri.Scheme=="http" && (!ContratoSoapInternalHmlHttpApproved || ContratoSoapEnvironment!="HML" || uri.Host!="192.168.17.91" || uri.Port!=80))
            throw new GuardianHttpException("Guardian internal HTTP SOAP profile is not approved.");
    }
    public string? PreCadastroProtocolResponseField { get; init; }

    internal Uri ResolveEndpoint(string? endpoint, string setting)
    {
        if (BaseUrl is null || !BaseUrl.IsAbsoluteUri || BaseUrl.Scheme is not ("http" or "https"))
            throw new GuardianHttpException("Guardian BaseUrl is required and must use HTTP or HTTPS.");
        if (Timeout <= TimeSpan.Zero)
            throw new GuardianHttpException("Guardian timeout must be positive.");
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new GuardianHttpException($"Guardian setting {setting} is required.");
        // Root-relative paths must not be inferred as absolute file URIs on Linux.
        if (endpoint.TrimStart().StartsWith("//", StringComparison.Ordinal) || endpoint.Contains('\\') ||
            !Uri.TryCreate(endpoint, UriKind.Relative, out var relativeEndpoint))
            throw new GuardianHttpException($"Guardian setting {setting} must be a relative endpoint.");

        if (!Uri.TryCreate(BaseUrl, relativeEndpoint, out var resolved) ||
            !string.Equals(resolved.Scheme, BaseUrl.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolved.Host, BaseUrl.Host, StringComparison.OrdinalIgnoreCase) ||
            resolved.Port != BaseUrl.Port || resolved.UserInfo != BaseUrl.UserInfo)
            throw new GuardianHttpException($"Guardian setting {setting} must be a relative endpoint.");

        return resolved;
    }

    internal static string RequireField(string? value, string setting)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new GuardianHttpException($"Guardian setting {setting} is required.");
        return value;
    }
}

public sealed class GuardianHttpException(string message, Exception? innerException = null)
    : Exception(message, innerException), FugaTES.Aplicacao.Servicos.Mvp.IAcmGuardianBusinessFailure
{
    public int? BusinessErrorCode { get; init; }
    public int? HttpStatusCode { get; init; }
}
