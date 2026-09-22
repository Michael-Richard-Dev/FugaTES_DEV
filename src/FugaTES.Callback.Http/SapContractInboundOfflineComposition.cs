using FugaTES.Aplicacao.CasosDeUso.Toledo;
using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Integracao.Toledo.Fakes;

namespace FugaTES.Callback.Http;

public static class SapContractInboundOfflineComposition
{
    private const string EnabledKey = "SapContractInbound:OfflineEnabled";

    public static bool ConfigurarServicos(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[EnabledKey];
        if (string.IsNullOrWhiteSpace(configured)) return false;
        if (!bool.TryParse(configured, out var enabled))
            throw new InvalidOperationException($"{EnabledKey} must be true or false.");
        if (!enabled) return false;

        services.AddSingleton<ContratoToledoGatewayFake>();
        services.AddSingleton<IContratoToledoGateway>(provider =>
            provider.GetRequiredService<ContratoToledoGatewayFake>());
        services.AddScoped<EnviarContratoToledoCasoDeUso>();
        return true;
    }
}
