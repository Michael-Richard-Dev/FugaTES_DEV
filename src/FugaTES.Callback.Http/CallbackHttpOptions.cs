namespace FugaTES.Callback.Http;

public sealed record CallbackHttpOptions(bool Enabled, string Route, string CorrelationProvider)
{
    public const string SectionName = "Callback";
    public const string InMemoryImplementation = "InMemory";
    public const string PostgreSqlImplementation = "PostgreSql";

    public static CallbackHttpOptions Parse(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var enabledText = Require(configuration, $"{SectionName}:Enabled");
        if (!bool.TryParse(enabledText, out var enabled))
            throw new InvalidOperationException("Callback:Enabled must be true or false.");

        var route = Require(configuration, $"{SectionName}:Route");
        if (!route.StartsWith("/", StringComparison.Ordinal) || route.Contains('?'))
            throw new InvalidOperationException("Callback:Route must be an absolute path without query string.");

        var provider = Require(configuration, "Correlation:Provider");
        return new CallbackHttpOptions(enabled, route, provider);
    }

    private static string Require(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Required configuration {key} is absent.");
        return value;
    }
}
