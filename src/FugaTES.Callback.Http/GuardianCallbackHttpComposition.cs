using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Infraestrutura.Recebimento;
using FugaTES.Persistencia.Recebimento;

namespace FugaTES.Callback.Http;

public static class GuardianCallbackHttpComposition
{
    public static CallbackHttpOptions ConfigurarServicos(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = CallbackHttpOptions.Parse(configuration);
        ConfigurarBase(services);
        if (string.Equals(options.CorrelationProvider, CallbackHttpOptions.InMemoryImplementation, StringComparison.Ordinal))
            ConfigurarCorrelacaoEmMemoria(services);
        else if (string.Equals(options.CorrelationProvider, CallbackHttpOptions.PostgreSqlImplementation, StringComparison.Ordinal))
            ConfigurarCorrelacaoPostgreSql(services, configuration);
        else
            throw new InvalidOperationException("Correlation:Provider must be InMemory or PostgreSql.");
        ToledoCallbackComposition.Configure(services, configuration, options.CorrelationProvider);
        return options;
    }

    private static void ConfigurarBase(IServiceCollection services)
    {
        services.AddSingleton<PesagemSapMvpGatewayFake>();
        services.AddSingleton<IPesagemSapMvpGateway>(provider =>
            provider.GetRequiredService<PesagemSapMvpGatewayFake>());
        services.AddScoped<ProcessarCallbackGuardianMvpCasoDeUso>();
    }

    private static void ConfigurarCorrelacaoEmMemoria(IServiceCollection services)
    {
        services.AddSingleton<RepositorioCorrelacaoRecebimentoMemoria>();
        services.AddSingleton<IRepositorioCorrelacaoRecebimento>(provider =>
            provider.GetRequiredService<RepositorioCorrelacaoRecebimentoMemoria>());
        services.AddSingleton<RepositorioMotorIntegracaoFake>();
        services.AddSingleton<IRegistroCallbackPesagemSapStore, RegistroCallbackPesagemSapMemoria>();
    }

    private static void ConfigurarCorrelacaoPostgreSql(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("FugaTES");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Required configuration ConnectionStrings:FugaTES is absent.");
        services.AddCorrelacaoGuardianSapPostgreSql(connectionString);
        services.AddScoped<IRegistroEventoGuardianStore, RepositorioEventoGuardianPostgreSql>();
        services.AddScoped<ProcessarEventoGuardianCasoDeUso>();
    }

    public static IServiceCollection ConfigurarFluxoLocal(
        IServiceCollection services,
        IPreCadastroGuardianSet26Gateway guardian,
        GuardianSet26Options options)
    {
        ArgumentNullException.ThrowIfNull(guardian);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        ConfigurarBase(services);
        ConfigurarCorrelacaoEmMemoria(services);
        GuardianEngineStageAComposition.ConfigurarOffline(services, guardian, options);
        services.AddScoped<ProcessarRecebimentoMvpCasoDeUso>();
        return services;
    }
}
