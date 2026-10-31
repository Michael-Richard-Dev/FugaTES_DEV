using System.Globalization;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Contratos.Guardian;
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
        var cacheLifetimeText = configuration["Guardian:TokenCacheLifetime"];
        var cacheLifetime = TimeSpan.Zero;
        if (cacheLifetimeText is not null &&
            (!TimeSpan.TryParse(cacheLifetimeText, CultureInfo.InvariantCulture, out cacheLifetime) || cacheLifetime <= TimeSpan.Zero || cacheLifetime > TimeSpan.FromMinutes(30)))
            throw new InvalidOperationException("Guardian:TokenCacheLifetime is invalid.");
        var endpoint = configuration["Guardian:PreCadastroEndpoint"] ?? "/api/v1/Guardian/ProtocoloPreCadastro";
        if (endpoint != "/api/v1/Guardian/ProtocoloPreCadastro")
            throw new InvalidOperationException("Guardian:PreCadastroEndpoint is not canonical.");
        Uri? soapEndpoint = null;
        var soapEndpointText = configuration["Guardian:ContratoSoapEndpoint"];
        if (soapEndpointText is not null &&
            (!Uri.TryCreate(soapEndpointText, UriKind.Absolute, out soapEndpoint) || soapEndpoint.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("Guardian:ContratoSoapEndpoint is invalid.");

        var options = new GuardianHttpOptions
        {
            BaseUrl = baseUrl,
            Timeout = timeout,
            TokenCacheLifetime = cacheLifetimeText is null ? TimeSpan.FromMinutes(30) : cacheLifetime,
            PreCadastroEndpoint = endpoint,
            ContratoSoapEndpoint = soapEndpoint,
            ContratoSoapEnvironment=configuration["Guardian:ContratoSoapEnvironment"],
            ContratoSoapInternalHmlHttpApproved=bool.TryParse(configuration["Guardian:ContratoSoapInternalHmlHttpApproved"],out var httpApproved)&&httpApproved
        };
        // Por padrao as credenciais sao exigidas na PARTIDA: um host que vai falar com o Guardian
        // deve falhar no boot, nao na primeira chamada. O receptor SAP DEV precisa de
        // ExternalMode=Guardian para satisfazer o guard de admissao SAP, mas nao executa Guardian
        // algum (worker desligado e nenhuma execucao autorizada) - e exigir um segredo que nunca
        // sera usado obrigaria a provisionar credencial sem necessidade.
        // O diferimento NAO afrouxa o controle: GuardianRuntimeCredenciaisProvider recusa fechado
        // no ponto de uso, com "Guardian runtime credentials are absent.".
        if (!CredenciaisPodemSerDiferidas(configuration))
        {
            Require(configuration, "Guardian:Email");
            Require(configuration, "Guardian:Password");
            Require(configuration, "Guardian:Signature");
        }

        services.AddSingleton(options);
        services.AddSingleton<ICredenciaisGuardianSet26Provider, GuardianRuntimeCredenciaisProvider>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<GuardianTokenClient>();
        services.AddScoped<GuardianSet26HttpClient>();
        services.AddScoped<FugaTES.Aplicacao.Servicos.Mvp.IAcmPreCadastroMinimoGatewayR57>(sp=>sp.GetRequiredService<GuardianSet26HttpClient>());
        services.AddScoped<IPreCadastroGuardianSet26Gateway>(sp => sp.GetRequiredService<GuardianSet26HttpClient>());
        services.AddScoped<IEmissorGuardianSet26Gateway>(sp => sp.GetRequiredService<GuardianSet26HttpClient>());
        services.AddScoped<ITransportadoraGuardianSet26Gateway>(sp => sp.GetRequiredService<GuardianSet26HttpClient>());
        services.AddScoped<IItemGuardianSet26Gateway>(sp => sp.GetRequiredService<GuardianSet26HttpClient>());
        services.AddScoped<GuardianContratoSoapClient>();
        services.AddScoped<ICadastraContratoGuardianGateway>(provider => provider.GetRequiredService<GuardianContratoSoapClient>());
        services.AddScoped<IGuardianContratoSincronizacaoGateway>(provider => provider.GetRequiredService<GuardianContratoSoapClient>());
        return services;
    }

    // Chaves que, se ligadas, significam que ESTE host pode chamar o Guardian. Qualquer uma delas
    // em true volta a exigir credencial na partida, independentemente do opt-in.
    private static readonly string[] ExecucoesGuardian =
    [
        "AcmR57:Normal:ExecutionAuthorized",
        "AcmProtocolCommand:ExecutionAuthorized",
        "FUGATES_INTEGRATION_WORKER_ENABLED"
    ];

    // O diferimento exige opt-in EXPLICITO e, cumulativamente, nenhuma execucao Guardian autorizada.
    public static bool CredenciaisPodemSerDiferidas(IConfiguration configuration)
    {
        if (!bool.TryParse(configuration["Guardian:CredentialsDeferredUntilUse"], out var diferir) || !diferir)
            return false;
        foreach (var chave in ExecucoesGuardian)
            if (bool.TryParse(configuration[chave], out var ligado) && ligado)
                return false;
        return true;
    }

    private static string Require(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Required runtime configuration {key} is absent.");
        return value;
    }
}
