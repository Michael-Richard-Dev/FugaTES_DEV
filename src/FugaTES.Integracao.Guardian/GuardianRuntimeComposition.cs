using System.Globalization;
using FugaTES.Aplicacao.Contratos.Recebimento;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FugaTES.Integracao.Guardian;

public static class GuardianRuntimeComposition
{
    public static IServiceCollection AddGuardianRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var baseUrlText = configuration["Guardian:BaseUrl"];
        if (!Uri.TryCreate(baseUrlText, UriKind.Absolute, out var baseUrl) ||
            baseUrl.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Guardian:BaseUrl is invalid.");
        if (!TimeSpan.TryParse(configuration["Guardian:Timeout"], CultureInfo.InvariantCulture, out var timeout) ||
            timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Guardian:Timeout is invalid.");
        var endpoint = Require(configuration, "Guardian:PreCadastroEndpoint");
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            throw new InvalidOperationException("Guardian:PreCadastroEndpoint must be relative.");

        var options = new GuardianHttpOptions
        {
            BaseUrl = baseUrl,
            Timeout = timeout,
            PreCadastroEndpoint = endpoint,
            TokenUserField = Require(configuration, "Guardian:TokenUserField"),
            TokenPasswordField = Require(configuration, "Guardian:TokenPasswordField"),
            TokenResponseField = Require(configuration, "Guardian:TokenResponseField")
        };
        Require(configuration, "Guardian:Username");
        Require(configuration, "Guardian:Password");

        services.AddSingleton(options);
        services.AddSingleton<ICredenciaisGuardianSet26Provider, GuardianRuntimeCredenciaisProvider>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<GuardianTokenClient>();
        services.AddScoped<GuardianSet26HttpClient>();
        services.AddScoped<IPreCadastroGuardianSet26Gateway>(sp => sp.GetRequiredService<GuardianSet26HttpClient>());
        return services;
    }

    private static string Require(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Required runtime configuration {key} is absent.");
        return value;
    }
}
