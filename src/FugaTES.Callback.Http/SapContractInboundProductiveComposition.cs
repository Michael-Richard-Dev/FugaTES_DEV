using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Persistencia.ContratosSapToledo;
using FugaTES.Persistencia.MotorIntegracao;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FugaTES.Callback.Http;

public static class SapContractInboundProductiveComposition
{
    public static bool ConfigurarServicos(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var enabledText = configuration["SapContractInbound:ProductiveEnabled"];
        if (enabledText is null) return false;
        if (!bool.TryParse(enabledText, out var enabled))
            throw new InvalidOperationException("SapContractInbound:ProductiveEnabled must be true or false.");
        if (!enabled) return false;
        if (bool.TryParse(configuration["SapContractInbound:OfflineEnabled"], out var offline) && offline)
            throw new InvalidOperationException("Productive and offline SAP inbound modes cannot coexist.");
        if (configuration["SapContractInbound:SecurityMode"] != "LoopbackOnly")
            throw new InvalidOperationException("SAP inbound productive exposure requires explicit LoopbackOnly mode until auth is approved.");

        var connectionString = configuration.GetConnectionString(NpgsqlDataSourceFactory.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:FugaTES is required for durable SAP inbound.");
        NpgsqlConnectionStringBuilder parsed;
        try { parsed = new NpgsqlConnectionStringBuilder(connectionString); }
        catch (ArgumentException exception) { throw new InvalidOperationException("SAP inbound connection configuration is invalid.", exception); }
        if (string.IsNullOrWhiteSpace(parsed.Host) || string.IsNullOrWhiteSpace(parsed.Database) ||
            string.IsNullOrWhiteSpace(parsed.Username))
            throw new InvalidOperationException("SAP inbound requires Host, Database and Username.");

        if (services.Any(service => service.ServiceType == typeof(IContratoSapToledoVersaoStore) ||
            service.ServiceType == typeof(IMotorIntegracaoRepository) ||
            service.ServiceType == typeof(IContratoToledoGateway)))
            throw new InvalidOperationException("SAP inbound productive composition cannot reuse preselected repositories or Toledo gateways.");

        services.TryAddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSourceFactory.Criar(connectionString));
        services.AddScoped<IContratoSapToledoVersaoStore, ContratoSapToledoVersaoStorePostgreSql>();
        services.AddScoped<IMotorIntegracaoRepository, RepositorioMotorIntegracaoPostgreSql>();
        services.AddSingleton<ContratoSapToledoCanonicalizer>();
        services.AddScoped<AceitarContratoSapToledoDuravelCasoDeUso>();
        return true;
    }
}
