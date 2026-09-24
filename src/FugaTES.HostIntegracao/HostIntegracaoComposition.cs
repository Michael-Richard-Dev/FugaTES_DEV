using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Integracao.Guardian;
using FugaTES.Persistencia.MotorIntegracao;
using FugaTES.Persistencia.Recebimento;
using Microsoft.Extensions.Options;

namespace FugaTES.HostIntegracao;

public static class HostIntegracaoComposition
{
    public static void Configurar(HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ConfigurarServicos(builder.Services, builder.Configuration);
        builder.Services.Configure<HostOptions>(options =>
        {
            var hostOptions = builder.Configuration.GetRequiredSection("HostIntegracao").Get<HostIntegracaoOptions>()
                ?? throw new InvalidOperationException("Configuracao HostIntegracao obrigatoria ausente.");
            if (!hostOptions.EhValida())
            {
                throw new InvalidOperationException("Configuracao HostIntegracao invalida.");
            }

            options.ShutdownTimeout = hostOptions.ShutdownGracePeriod;
        });
    }

    public static IServiceCollection ConfigurarServicos(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddLogging();
        services.AddOptions<MotorIntegracaoOptions>()
            .Bind(configuration.GetRequiredSection("MotorIntegracao"))
            .Validate(options => options.EhValida(), "MotorIntegracao exige LeaseDuration, LeaseRenewInterval < LeaseDuration e RetryBackoff validos.")
            .ValidateOnStart();
        services.AddOptions<HostIntegracaoOptions>()
            .Bind(configuration.GetRequiredSection("HostIntegracao"))
            .Validate(options => options.EhValida(), "HostIntegracao exige PollInterval, ShutdownGracePeriod e ExternalMode=Fake ou Guardian.")
            .ValidateOnStart();
        services.AddOptions<HostIntegracaoPersistenceOptions>()
            .Configure(options => options.Mode = configuration["HostIntegracao:PersistenceMode"] ?? string.Empty)
            .Validate(options => options.EhValida(), "HostIntegracao exige PersistenceMode=Fake ou PersistenceMode=PostgreSql.")
            .ValidateOnStart();
        services.AddOptions<TimeoutProcessamentoIntegracaoOptions>()
            .Bind(configuration.GetRequiredSection("TimeoutProcessamentoIntegracao"))
            .Validate(options => options.EhValida(), "TimeoutProcessamentoIntegracao exige Timeout positivo.")
            .ValidateOnStart();

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MotorIntegracaoOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<HostIntegracaoOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<HostIntegracaoPersistenceOptions>>().Value);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IIdentidadeWorkerProvider, IdentidadeWorkerRuntimeProvider>();
        services.AddSingleton<RepositorioMotorIntegracaoFake>();
        var persistenceMode = configuration["HostIntegracao:PersistenceMode"];
        if (string.Equals(persistenceMode, "PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            services.AddRepositorioMotorIntegracaoPostgreSql(configuration.GetConnectionString(NpgsqlDataSourceFactory.ConnectionStringName));
        }

        services.AddSingleton<IMotorIntegracaoRepository>(sp =>
        {
            var persistence = sp.GetRequiredService<HostIntegracaoPersistenceOptions>();
            return persistence.UsaPostgreSql()
                ? sp.GetRequiredService<RepositorioMotorIntegracaoPostgreSql>()
                : sp.GetRequiredService<RepositorioMotorIntegracaoFake>();
        });
        if (string.Equals(configuration["HostIntegracao:ExternalMode"], "Guardian", StringComparison.OrdinalIgnoreCase))
            ConfigurarGuardian(services, configuration, persistenceMode);
        else
            services.AddSingleton<IProcessadorExternoIntegracao>(sp => new ProcessadorExternoIntegracaoFake(() => sp.GetRequiredService<RepositorioMotorIntegracaoFake>().TransacaoAtiva));
        services.AddSingleton<IDetectorCandidatosIntegracao, DetectorCandidatosIntegracaoFake>();
        services.AddSingleton<IValidadorIntegracao, ValidadorGenericoIntegracao>();
        services.AddSingleton<IPoliticaTimeoutProcessamentoIntegracao, PoliticaTimeoutProcessamentoIntegracaoConfigurada>();
        services.AddScoped<MotorIntegracaoServico>();
        services.AddHostedService<HospedagemIntegracaoWorker>();

        return services;
    }

    private static void ConfigurarGuardian(IServiceCollection services, IConfiguration configuration, string? persistenceMode)
    {
        if (!string.Equals(persistenceMode, "PostgreSql", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(configuration.GetConnectionString(NpgsqlDataSourceFactory.ConnectionStringName)))
            throw new InvalidOperationException("Guardian mode requires PostgreSql persistence and ConnectionStrings:FugaTES.");

        services.AddGuardianRuntime(configuration);
        services.AddScoped<IRepositorioCorrelacaoRecebimento, RepositorioCorrelacaoGuardianSapPostgreSql>();
        services.AddScoped<ProcessadorPreCadastroGuardianMotor>();
        services.AddScoped<IProcessadorExternoIntegracao, ProcessadorExternoIntegracaoRoteado>();
    }
}
