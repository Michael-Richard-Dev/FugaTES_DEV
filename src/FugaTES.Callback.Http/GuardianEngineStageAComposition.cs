using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Infraestrutura.Recebimento;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FugaTES.Callback.Http;

public static class GuardianEngineStageAComposition
{
    public static IServiceCollection ConfigurarOffline(
        IServiceCollection services,
        IPreCadastroGuardianSet26Gateway guardian,
        GuardianSet26Options options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(guardian);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(guardian);
        services.AddSingleton(options);
        services.TryAddSingleton<IRepositorioCorrelacaoRecebimento, RepositorioCorrelacaoRecebimentoMemoria>();
        services.AddSingleton<RepositorioMotorIntegracaoFake>();
        services.AddSingleton<IMotorIntegracaoRepository>(sp => sp.GetRequiredService<RepositorioMotorIntegracaoFake>());
        services.AddSingleton<IIdentidadeWorkerProvider, WorkerStageA>();
        services.AddSingleton<IDetectorCandidatosIntegracao, DetectorVazioStageA>();
        services.AddSingleton<IValidadorIntegracao, ValidadorGenericoIntegracao>();
        services.AddSingleton<IPoliticaTimeoutProcessamentoIntegracao, TimeoutStageA>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new MotorIntegracaoOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(1),
            LeaseRenewInterval = TimeSpan.FromSeconds(20),
            RetryBackoff = TimeSpan.FromMinutes(5)
        });
        services.AddScoped<IProcessadorExternoIntegracao, ProcessadorPreCadastroGuardianMotor>();
        services.AddScoped<MotorIntegracaoServico>();
        services.AddScoped<RegistrarPreCadastroGuardianMotorCasoDeUso>();
        return services;
    }

    private sealed class WorkerStageA : IIdentidadeWorkerProvider
    {
        public IdentidadeWorkerIntegracao Obter() => new("GUARDIAN-STAGE-A");
    }

    private sealed class DetectorVazioStageA : IDetectorCandidatosIntegracao
    {
        public Task<IReadOnlyList<CandidatoIntegracao>> DetectarAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CandidatoIntegracao>>([]);
    }

    private sealed class TimeoutStageA : IPoliticaTimeoutProcessamentoIntegracao
    {
        public TimeSpan ObterTimeout(ClaimIntegracaoMotor claim) => TimeSpan.FromMinutes(1);
    }
}
