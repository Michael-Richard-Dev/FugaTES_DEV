using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Integracao.Guardian;
using FugaTES.Integracao.SAP.Http;
using FugaTES.Persistencia.MotorIntegracao;
using FugaTES.Persistencia.Recebimento;
using Microsoft.Extensions.Options;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Persistencia.ContratosSapToledo;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FugaTES.HostIntegracao;

public static class HostIntegracaoComposition
{
    public static void Configurar(HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.Equals(builder.Configuration["HostIntegracao:Mode"], "CarrierSync", StringComparison.Ordinal))
        {
            CarrierSyncComposition.ConfigurarServicos(builder.Services, builder.Configuration);
            return;
        }
        if (string.Equals(builder.Configuration["HostIntegracao:Mode"], "MmPurchaseOrders", StringComparison.Ordinal))
        {
            Mm.MmLinuxComposition.Configure(builder.Services,builder.Configuration);
            return;
        }
        // Motor do fluxo SD na VM: varre ordens de venda e cria protocolo no Guardian sozinho.
        // Modo proprio, e nao mais um worker do motor geral, porque o recorte e as credenciais
        // sao dele e nada mais deve subir junto neste processo.
        if (string.Equals(builder.Configuration["HostIntegracao:Mode"], "SdProtocolo", StringComparison.Ordinal))
        {
            SdProtocoloComposition.ConfigurarServicos(builder.Services, builder.Configuration);
            return;
        }
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

        var connectionString = configuration.GetConnectionString(NpgsqlDataSourceFactory.ConnectionStringName);
        if (!string.IsNullOrWhiteSpace(connectionString) &&
            new[]{AmbientesBanco.Dev110,AmbientesBanco.Qas120}.Any(a=>new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Username==a.PapelAcm) &&
            !(bool.TryParse(configuration["AcmR57:Enabled"], out var acmEnabled) && acmEnabled))
            throw new InvalidOperationException("R165R_ACM_ROLE_REQUIRES_R57_ENABLED");

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
            var requireIdentity=RequiresAcmPhysicalIdentity(configuration)||NpgsqlDataSourceFactory.EhRoleGuardada(new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Username,AmbientesBanco.Dev110,AmbientesBanco.Qas120);
            services.AddRepositorioMotorIntegracaoPostgreSql(connectionString,
                requireAcmPhysicalIdentity: requireIdentity,
                ambientesAceitos: requireIdentity?[AcmEnvironmentBinding.Banco(configuration)]:[]);
            services.TryAddScoped<IContratoSapToledoVersaoStore, ContratoSapToledoVersaoStorePostgreSql>();
            services.TryAddScoped<IAcmGuardianVinculoStore, AcmGuardianVinculoPostgreSql>();
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
        if (bool.TryParse(configuration["FUGATES_INTEGRATION_WORKER_ENABLED"], out var workerEnabled) && workerEnabled)
            services.AddHostedService<HospedagemIntegracaoWorker>();
        services.AddMvpOrchestration(configuration);

        return services;
    }

    // R161L binding: Guardian/ACM repositories share the physical identity initializer.
    // CarrierSync retains its separate legacy composition and guards.
    public static bool RequiresAcmPhysicalIdentity(IConfiguration configuration) =>
        (bool.TryParse(configuration["MotorIntegracao:AcmInboundWindow:Enabled"], out var window) && window) ||
        string.Equals(configuration["HostIntegracao:ExternalMode"], "Guardian", StringComparison.OrdinalIgnoreCase) ||
        (bool.TryParse(configuration["AcmR57:Enabled"], out var enabled) && enabled);

    private static void ConfigurarGuardian(IServiceCollection services, IConfiguration configuration, string? persistenceMode)
    {
        if (!string.Equals(persistenceMode, "PostgreSql", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(configuration.GetConnectionString(NpgsqlDataSourceFactory.ConnectionStringName)))
            throw new InvalidOperationException("Guardian mode requires PostgreSql persistence and ConnectionStrings:FugaTES.");

        services.AddGuardianRuntime(configuration);
        services.AddSapPesagemRuntime(configuration);
        services.AddScoped<IRepositorioCorrelacaoRecebimento, RepositorioCorrelacaoGuardianSapPostgreSql>();
        services.TryAddScoped<IAcmSapTicketEvidenceStore>(sp =>
            (IAcmSapTicketEvidenceStore)sp.GetRequiredService<IRepositorioCorrelacaoRecebimento>());
        services.AddScoped<ProcessadorPreCadastroGuardianMotor>();
        services.TryAddSingleton(FugaTES.Aplicacao.Servicos.Mvp.AcmR106SapReturnScope.R104);
        services.TryAddScoped<FugaTES.Aplicacao.Servicos.Mvp.IAcmR106SapReturnPreflight,
            FugaTES.Aplicacao.Servicos.Mvp.AcmR106SapReturnPreflight>();
        // The real SOAP binding and business provenance remain unverified.
        // Offline tests may inject the concrete reader; shipped composition stays inert.
        services.TryAddScoped<IAcmGuardianVerifiedWeighingReaderR164A, AcmGuardianTicketReaderDisabledR164E>();
        services.TryAddScoped<IAcmSapTicketEvidenceSource, AcmGuardianTicketEvidenceAdapterR164A>();
        services.AddScoped(sp => new ProcessadorPesagemSapMotor(
            sp.GetRequiredService<IRepositorioCorrelacaoRecebimento>(),
            sp.GetRequiredService<IPesagemSapMvpGateway>(),
            sp.GetService<IAcmR104EffectiveCarrierSource>(),
            sp.GetRequiredService<FugaTES.Aplicacao.Servicos.Mvp.IAcmR106SapReturnPreflight>(),
            sp.GetRequiredService<IAcmSapTicketEvidenceSource>(),
            reason => sp.GetRequiredService<ILogger<ProcessadorPesagemSapMotor>>()
                .LogWarning("SAP weighing blocked: {Reason}", reason),
            sp.GetRequiredService<IAcmSapTicketEvidenceStore>()));
        services.AddScoped<IProcessadorExternoIntegracao, ProcessadorExternoIntegracaoRoteado>();
    }
}
