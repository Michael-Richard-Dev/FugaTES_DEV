using FugaTES.Aplicacao.CasosDeUso.Toledo;
using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Integracao.Toledo.Fakes;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Infraestrutura.ContratosSapToledo;
using FugaTES.Infraestrutura.MotorIntegracao;

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
        services.AddSingleton<RepositorioMotorIntegracaoFake>();
        services.AddSingleton<IMotorIntegracaoRepository>(sp => sp.GetRequiredService<RepositorioMotorIntegracaoFake>());
        services.AddSingleton<IContratoSapToledoVersaoStore, ContratoSapToledoVersaoStoreFake>();
        services.AddSingleton<ContratoSapToledoCanonicalizer>();
        services.AddSingleton<IIdentidadeWorkerProvider, H4WorkerProvider>();
        services.AddSingleton<IDetectorCandidatosIntegracao, H4EmptyDetector>();
        services.AddSingleton<IValidadorIntegracao, ValidadorGenericoIntegracao>();
        services.AddSingleton<IPoliticaTimeoutProcessamentoIntegracao, H4TimeoutPolicy>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new MotorIntegracaoOptions { LeaseDuration=TimeSpan.FromMinutes(1), LeaseRenewInterval=TimeSpan.FromSeconds(20), RetryBackoff=TimeSpan.FromMinutes(5) });
        services.AddScoped<IProcessadorExternoIntegracao, ProcessadorContratoSapToledo>();
        services.AddScoped<MotorIntegracaoServico>();
        services.AddScoped<ProcessarContratoSapToledoIdempotenteCasoDeUso>();
        return true;
    }

    private sealed class H4WorkerProvider : IIdentidadeWorkerProvider { public IdentidadeWorkerIntegracao Obter()=>new("H4-HTTP"); }
    private sealed class H4EmptyDetector : IDetectorCandidatosIntegracao { public Task<IReadOnlyList<CandidatoIntegracao>> DetectarAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<CandidatoIntegracao>>([]); }
    private sealed class H4TimeoutPolicy : IPoliticaTimeoutProcessamentoIntegracao { public TimeSpan ObterTimeout(ClaimIntegracaoMotor claim)=>TimeSpan.FromMinutes(1); }
}
