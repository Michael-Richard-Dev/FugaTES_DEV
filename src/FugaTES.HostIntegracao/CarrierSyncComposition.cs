using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Aplicacao.CasosDeUso.SAP;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using FugaTES.Integracao.Guardian;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace FugaTES.HostIntegracao;
public static class CarrierSyncComposition
{
    public static void ConfigurarServicos(IServiceCollection services,IConfiguration config)
    {
        if(bool.TryParse(config["FUGATES_INTEGRATION_WORKER_ENABLED"],out var general)&&general)throw new InvalidOperationException("CARRIER_INC005_FORBIDDEN");
        var enabled=bool.TryParse(config["FUGATES_CARRIER_SYNC_ENABLED"],out var flag)&&flag;
        if(OperatingSystem.IsLinux())CarrierLinuxHosting.Configure(services,config,enabled);
        if(!enabled)return;
        if(config["Carrier:Environment"]!="HML")throw new InvalidOperationException("CARRIER_ENVIRONMENT_INVALID");
        if(!OperatingSystem.IsWindows()&&!OperatingSystem.IsLinux())throw new InvalidOperationException("CARRIER_HOST_PLATFORM_UNSUPPORTED");
        if(OperatingSystem.IsWindows())services.AddWindowsService(options=>options.ServiceName="FugaTES.CarrierSync");
        services.AddLogging();services.TryAddSingleton(TimeProvider.System);
        var guardianContract=CarrierProtocolBinding.BuildGuardianContract(config);
        var authorization=new CarrierSyncAuthorization("HML",guardianContract?.ProfileVerified==true,guardianContract?.SignatureVerified==true,
            DateTimeOffset.TryParse(config["Carrier:Authorization:UntilUtc"],out var until)?until:DateTimeOffset.MinValue,
            Values(config,"Carrier:Authorization:AllowedBps"),Values(config,"Carrier:Authorization:AllowedSids"),
            config["Carrier:Authorization:IncrementalReleased"]=="true");
        services.AddSingleton(sp=>new CarrierSyncPolicy(authorization,sp.GetRequiredService<TimeProvider>()));
        if(OperatingSystem.IsWindows())services.AddSingleton(sp=>CarrierServiceRuntimeBootstrap.StartAsync(config["Carrier:ServiceProfile"]??throw new InvalidOperationException("CARRIER_SERVICE_PROFILE_REQUIRED"),CancellationToken.None).GetAwaiter().GetResult());
        services.AddSingleton<ICarrierSyncStore>(sp=>OperatingSystem.IsLinux()?sp.GetRequiredService<CarrierLinuxRuntimeBootstrap>().Store:sp.GetRequiredService<CarrierServiceRuntimeBootstrap>().Store);
        services.AddSingleton<HttpClient>(_=>new(new HttpClientHandler {AllowAutoRedirect=false}));
        services.AddSingleton<IAutenticacaoSap>(sp=>OperatingSystem.IsLinux()?new CarrierLinuxSapAuthentication(sp.GetRequiredService<CarrierLinuxRuntimeBootstrap>()):new CarrierServiceSapAuthentication(config));
        services.AddSingleton(sp=>new OpcoesSap {BaseUrl=OperatingSystem.IsLinux()?new Uri(sp.GetRequiredService<CarrierLinuxRuntimeBootstrap>().Profile.Sap.BaseUrl):Uri.TryCreate(config["Sap:BaseUrl"],UriKind.Absolute,out var sap)?sap:null,SapClient=config["Sap:SapClient"],Timeout=TimeSpan.FromSeconds(30)});
        services.AddSingleton<ClienteHttpParceiroNegocioSap>();
        services.AddSingleton<ConsultaTransportadoraSapAdapter>();
        services.AddSingleton(sp=>new ConsultarTransportadoraSapCasoDeUso(sp.GetRequiredService<ConsultaTransportadoraSapAdapter>()));
        services.AddSingleton(sp=>new GuardianHttpOptions {BaseUrl=OperatingSystem.IsLinux()?new Uri(sp.GetRequiredService<CarrierLinuxRuntimeBootstrap>().Profile.Guardian.BaseUrl):Uri.TryCreate(config["Guardian:BaseUrl"],UriKind.Absolute,out var uri)?uri:null,Timeout=TimeSpan.FromSeconds(30)});
        services.AddSingleton<ICredenciaisGuardianSet26Provider>(sp=>OperatingSystem.IsLinux()?new CarrierLinuxGuardianCredentials(sp.GetRequiredService<CarrierLinuxRuntimeBootstrap>()):new CarrierServiceGuardianCredentials(config));
        services.AddSingleton<GuardianTokenClient>();
        // The binding resolves only proven protocol metadata; missing contract never invents routes.
        CarrierProtocolBinding.Register(services,config,new CarrierSyncPolicy(authorization,TimeProvider.System));
        services.AddSingleton(sp=>new SincronizarTransportadoras(sp.GetRequiredService<ISapCarrierDiscovery>(),sp.GetRequiredService<IGuardianCarrierGateway>(),
            sp.GetRequiredService<ICarrierSyncStore>(),sp.GetRequiredService<CarrierSyncPolicy>(),sp.GetRequiredService<TimeProvider>(),
            async (bp,ct)=>
            {
                var result=await sp.GetRequiredService<ConsultarTransportadoraSapCasoDeUso>().ExecutarAsync(bp,ct);
                if(result.Status!=StatusConsultaSap.Sucesso||result.Dados is null||result.Dados.BusinessPartner!=bp||result.Dados.CategoriaValidada!=CategoriaParceiroNegocio.Transportadora||result.Dados.BloqueioSap!=SituacaoBloqueioSap.NaoBloqueado)
                    throw new CarrierGatewayException("SAP_CARRIER_INDIVIDUAL_READ_FAILED");
                var data=result.Dados;return new CarrierPayload(bp,data.Nome??"",data.NomeCompletoParceiroNegocio??"");
            }));
        services.AddSingleton(sp=>new CarrierManualRequestHandler(sp.GetRequiredService<CarrierSyncPolicy>(),
            (r,ct)=>sp.GetRequiredService<ICarrierSyncStore>().EnfileirarPedidoAsync(r.Id,r.Bp,r.AuthenticatedSid,r.RequestedUtc,ct),sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService<CarrierSyncWorker>();
        if(OperatingSystem.IsWindows())services.AddHostedService<CarrierSyncManualCommandServer>();
    }
    static string[] Values(IConfiguration config,string key)=>config.GetSection(key).GetChildren().Select(c=>c.Value).Where(v=>!string.IsNullOrWhiteSpace(v)).Cast<string>().ToArray();
}