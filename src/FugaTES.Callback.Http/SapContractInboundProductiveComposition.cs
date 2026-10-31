using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Persistencia.ContratosSapToledo;
using FugaTES.Persistencia.MotorIntegracao;
using FugaTES.HostIntegracao;
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
        var securityMode = configuration["SapContractInbound:SecurityMode"];
        if (securityMode is not ("LoopbackOnly" or "LoopbackBasicHttps"))
            throw new InvalidOperationException("SAP_INBOUND_SECURITY_MODE_REQUIRED");
        if (string.IsNullOrWhiteSpace(configuration["SapInbound:BasicAuth:Username"]) ||
            string.IsNullOrWhiteSpace(configuration["SapInbound:BasicAuth:Password"]))
            throw new InvalidOperationException("SAP_INBOUND_BASIC_CONFIGURATION_REQUIRED");

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

        var automaticText = configuration["SapContractInbound:AutomaticProcessingEnabled"];
        if (automaticText is not null && !bool.TryParse(automaticText, out _))
            throw new InvalidOperationException("SapContractInbound:AutomaticProcessingEnabled must be true or false.");
        var windowText = configuration["MotorIntegracao:AcmInboundWindow:Enabled"];
        if (windowText is not null && !bool.TryParse(windowText, out _))
            throw new InvalidOperationException("SAP_INBOUND_SCOPE_UNAVAILABLE");
        if (bool.TryParse(windowText, out var windowEnabled) && windowEnabled &&
            (securityMode != "LoopbackBasicHttps" || !bool.TryParse(automaticText, out var windowAutomatic) || !windowAutomatic))
            throw new InvalidOperationException("SAP_INBOUND_SECURE_AUTOMATIC_WINDOW_REQUIRED");
        if (bool.TryParse(automaticText, out var automatic) && automatic)
        {
            var motor = configuration.GetSection("MotorIntegracao").Get<MotorIntegracaoOptions>();
            if (motor is null || !motor.EhValida() || (!motor.UsaEscopoAcm && !motor.AcmInboundWindow.Enabled) ||
                configuration["HostIntegracao:PersistenceMode"] != "PostgreSql" ||
                configuration["HostIntegracao:ExternalMode"] != "Guardian" ||
                !bool.TryParse(configuration["AcmR57:Enabled"], out var r57) || !r57 ||
                !bool.TryParse(configuration["AcmR57:Normal:Enabled"], out var normal) || !normal)
                throw new InvalidOperationException("SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED");

            // The existing worker polls committed H4 entries; HTTP acceptance never sends to Guardian.
            HostIntegracaoComposition.ConfigurarServicos(services, configuration);
        }
        else
        {
            services.TryAddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSourceFactory.Criar(connectionString));
            services.AddScoped<IContratoSapToledoVersaoStore, ContratoSapToledoVersaoStorePostgreSql>();
            services.AddScoped<IMotorIntegracaoRepository, RepositorioMotorIntegracaoPostgreSql>();
        }
        services.AddSingleton<ContratoSapToledoCanonicalizer>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<AceitarContratoSapToledoDuravelCasoDeUso>();
        return true;
    }
}
