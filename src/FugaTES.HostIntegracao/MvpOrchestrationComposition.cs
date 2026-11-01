using FugaTES.Aplicacao.CasosDeUso.Mvp;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Servicos.SAP;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FugaTES.Persistencia.Mvp;
using Npgsql;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Configuracao;

namespace FugaTES.HostIntegracao;

public static class MvpOrchestrationComposition
{
    public static IServiceCollection AddMvpOrchestration(this IServiceCollection services,Microsoft.Extensions.Configuration.IConfiguration? configuration=null)
    {
        services.TryAddScoped<IDecisoesFluxoMvp, DecisoesMvpConfirmadas>();
        services.TryAddScoped<IResultadoFluxoMvpStore>(provider =>
            new RepositorioResultadoFluxoMvpPostgreSql(provider.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddScoped<IPreparacaoSaidaSdMvp, PreparacaoMvpRegrasConfirmadas>();
        services.TryAddScoped<IPreparacaoEntradaMmMvp, PreparacaoMvpRegrasConfirmadas>();
        services.TryAddScoped<RegraValorProximidadeFimAcm>(provider => new(
            provider.GetService<Microsoft.Extensions.Configuration.IConfiguration>()?
                .GetValue<decimal?>("Acm:ValorProximidadeFimContrato"),
            provider.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.EnvironmentName));
        services.TryAddScoped<IMapeadorContratoAcmGuardian, MapeadorContratoAcmGuardian>();
        services.TryAddScoped<IResolverTransportadoraAcm, ResolverTransportadoraAcmPendente>();
        services.TryAddScoped<IDecisaoRemessaSap, DecisaoRemessaSapPendente>();
        services.TryAddScoped<IGatilhoMovimentoMaterialSap, GatilhoMovimentoMaterialSapPendente>();
        services.TryAddScoped<IMapeadorPesagemAcmSap, MapeadorPesagemAcmSapPendente>();
        if (!Tem(services, typeof(IMotorIntegracaoRepository), typeof(TimeProvider))) return services;

        if (Tem(services, typeof(IConsultaOrdemVendaSap), typeof(IConsultaPartnerSap), typeof(IConsultaMaterialSap), typeof(IRemessaSap), typeof(IPreCadastroGuardianSet26Gateway)))
            Adicionar<ProcessarSaidaSd>(services);
        if (Tem(services, typeof(IConsultaPedidoCompraSap), typeof(IConsultaPartnerSap), typeof(IConsultaMaterialSap), typeof(IPreCadastroGuardianSet26Gateway)))
            Adicionar<ProcessarEntradaMm>(services);
        if (Tem(services, typeof(IRepositorioCorrelacaoRecebimento), typeof(IConsultaPedidoCompraSap), typeof(IMovimentoMaterialSap)))
            Adicionar<ProcessarMovimento101Mm>(services);
        var r57Config=services.LastOrDefault(d=>d.ServiceType==typeof(Microsoft.Extensions.Configuration.IConfiguration))?.ImplementationInstance as Microsoft.Extensions.Configuration.IConfiguration;
        var enabled=(configuration ?? r57Config)?["AcmR57:Enabled"];
        if(enabled is not null && !bool.TryParse(enabled,out _))throw new InvalidOperationException("AcmR57:Enabled must be explicit true/false.");
        if(enabled is not null && bool.Parse(enabled))
        {
            var normalConfig = (configuration ?? r57Config)?.GetSection("AcmR57:Normal");
            var normalEnabled = normalConfig?["Enabled"];
            if (normalEnabled is not null && !bool.TryParse(normalEnabled, out _))
                throw new InvalidOperationException("AcmR57:Normal:Enabled must be true/false.");
            if (bool.TryParse(normalEnabled, out var useNormal) && useNormal)
            {
                var normal = normalConfig!.Get<AcmR57NormalOptions>() ?? new();
                var sharedWindow = (configuration ?? r57Config)?.GetSection("MotorIntegracao:AcmInboundWindow").Get<AcmInboundWindowOptions>();
                // O recorte continuo vem da mesma secao do motor, como a janela: um unico
                // lugar define o que pode ser despachado, e o gate R57 le dali.
                var sharedAuto = (configuration ?? r57Config)?.GetSection("MotorIntegracao:AcmAutomatico").Get<AcmAutomaticoOptions>();
                normal = normal with { AcmInboundWindow = sharedWindow ?? new(), AcmAutomatico = sharedAuto ?? new() };
                services.TryAddSingleton(normal);
                if(normal.DynamicEmitterValidationEnabled)
                    services.TryAddScoped<AcmGuardianEmitterResolver>();
                if(normal.DynamicMaterialValidationEnabled)
                {
                    if(!normal.DynamicEmitterValidationEnabled)throw new InvalidOperationException("ACM_MATERIAL_REQUIRES_DYNAMIC_EMITTER_SCOPE");
                    services.TryAddScoped<AcmGuardianMaterialResolver>();
                }
                services.TryAddScoped<IAcmR57RuntimeGate,AcmR57NormalRuntimeGate>();
                services.TryAddScoped<IAcmR57Preparacao,AcmR57NormalPreparation>();
            }
            else
            {
                services.TryAddScoped<IAcmR57RuntimeGate,AcmR57RuntimeDisabled>();
                services.TryAddScoped<IAcmR57Preparacao,AcmR57PreparacaoPendente>();
            }
            if (Tem(services, typeof(IRepositorioCorrelacaoRecebimento)))
                services.TryAddScoped<IAcmR104CallbackCorrelation, AcmR104CallbackCorrelation>();
            services.TryAddScoped<IGuardianContratoSincronizacaoGateway>(sp=>sp.GetRequiredService<ICadastraContratoGuardianGateway>() as IGuardianContratoSincronizacaoGateway ?? throw new InvalidOperationException("R57_SOAP_GATEWAY_UNAVAILABLE"));
            services.TryAddScoped<IAcmPreCadastroMinimoGatewayR57>(sp=>sp.GetRequiredService<IPreCadastroGuardianSet26Gateway>() as IAcmPreCadastroMinimoGatewayR57 ?? throw new InvalidOperationException("R57_REST_GATEWAY_UNAVAILABLE"));
            services.TryAddScoped<AcmR57Pipeline>();
        }
        if (Tem(services, typeof(IContratoSapToledoVersaoStore), typeof(ICadastraContratoGuardianGateway)))
            Adicionar<ProcessarContratoAcm>(services);
        if (Tem(services, typeof(IRepositorioCorrelacaoRecebimento), typeof(IReceberPesagemSap)))
            Adicionar<ProcessarPesagemAcm>(services);
        Adicionar<ProcessarAcmSaida>(services);
        services.TryAddScoped<ProcessadorFluxosMvp>();
        return services;
    }

    private static bool Tem(IServiceCollection services, params Type[] tipos)
        => tipos.All(tipo => services.Any(descriptor => descriptor.ServiceType == tipo));

    private static void Adicionar<T>(IServiceCollection services) where T : FluxoMvpCasoDeUso
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(T))) return;
        services.AddScoped<T>();
        services.AddScoped<FluxoMvpCasoDeUso>(provider => provider.GetRequiredService<T>());
    }

}

public sealed class ProcessadorFluxosMvp(IEnumerable<FluxoMvpCasoDeUso> casos) : IProcessadorExternoIntegracao
{
    public Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim, CancellationToken cancellationToken)
    {
        var caso = casos.SingleOrDefault(c => claim.Operacao == ProcessarContratoAcm.OperacaoInboundH4
            ? c is ProcessarContratoAcm : c.Operacao == claim.Operacao);
        return caso is null ? Task.FromResult(new ResultadoProcessamentoExterno(ClassificacaoFalhaIntegracao.FalhaDefinitiva))
            : caso.ProcessarAsync(claim, cancellationToken);
    }
}
