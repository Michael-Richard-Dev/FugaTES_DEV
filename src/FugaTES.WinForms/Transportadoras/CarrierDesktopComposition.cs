using System.Text.Json;
using FugaTES.WinForms.Transportadoras.MasterdataR3;
using FugaTES.Persistencia.Transportadoras;
using FugaTES.Aplicacao.CasosDeUso.SAP;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.Guardian;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using FugaTES.WinForms.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
namespace FugaTES.WinForms.Transportadoras;

public static class CarrierDesktopProfile
{
    public static IConfiguration Load(string path)
    {
        if(!File.Exists(path))throw new DesktopStartupException("CARRIER_DESKTOP_HML_PROFILE_MISSING");
        try
        {
            var bytes=File.ReadAllBytes(path);using var json=JsonDocument.Parse(bytes);RejectSecrets(json.RootElement);
            using var input=new MemoryStream(bytes);return new ConfigurationBuilder().AddJsonStream(input).Build();
        }
        catch(DesktopStartupException){throw;}
        catch{throw new DesktopStartupException("CARRIER_DESKTOP_HML_PROFILE_INVALID");}
    }
    static void RejectSecrets(JsonElement node)
    {
        if(node.ValueKind==JsonValueKind.Object)foreach(var property in node.EnumerateObject())
        {
            if(new[]{"password","pwd","token","signature","assinatura","connectionstrings","connectionstring"}.Contains(property.Name,StringComparer.OrdinalIgnoreCase))throw new DesktopStartupException("CARRIER_DESKTOP_PLAINTEXT_SECRET_FORBIDDEN");
            RejectSecrets(property.Value);
        }
        else if(node.ValueKind==JsonValueKind.Array)foreach(var item in node.EnumerateArray())RejectSecrets(item);
    }
    public static bool True(IConfiguration config,string key)=>bool.TryParse(config[key],out var flag)&&flag;
    public static void Validate(IConfiguration config)
    {
        if(config["Carrier:Environment"]!="HML"||!True(config,"Carrier:DesktopProfileApproved")||string.IsNullOrWhiteSpace(config["Carrier:ApprovalReference"]))throw new DesktopStartupException("CARRIER_DESKTOP_HML_NOT_PROVEN");
        if(new[]{"FUGATES_INTEGRATION_WORKER_ENABLED","INC005_WORKER_ENABLED","Carrier:IntegrationWorkerEnabled"}.Any(key=>bool.TryParse(config[key],out var flag)&&flag))throw new DesktopStartupException("CARRIER_INC005_FORBIDDEN");
        foreach(var system in new[]{"Sap","Guardian"})
            if(!Uri.TryCreate(config[system+":BaseUrl"],UriKind.Absolute,out var url)||url.Scheme!="https"||url.UserInfo.Length!=0||url.Query.Length!=0||url.Fragment.Length!=0)throw new DesktopStartupException("CARRIER_DESKTOP_HML_ORIGIN_INVALID");
        if(CarrierProtocolBinding.BuildGuardianContract(config) is null)throw new DesktopStartupException("CARRIER_DESKTOP_GUARDIAN_HML_NOT_PROVEN");
        if(CarrierProtocolBinding.BuildSapContract(config) is not {} sap||sap.Origin.GetLeftPart(UriPartial.Authority)!=new Uri(config["Sap:BaseUrl"]!).GetLeftPart(UriPartial.Authority))throw new DesktopStartupException("CARRIER_DESKTOP_SAP_DELTA_NOT_PROVEN");
        foreach(var key in new[]{"SapUser","SapCredentialTarget","GuardianUser","GuardianCredentialTarget","GuardianSignatureTarget","GuardianSignatureUser"})
            if(string.IsNullOrWhiteSpace(config["Carrier:Secrets:"+key]))throw new DesktopStartupException("CARRIER_DESKTOP_CREDENTIAL_PROFILE_MISSING");
    }
}
public static class CarrierDesktopComposition
{
    public static CarrierDesktopLifecycle? Current{get;private set;}
    public static CarrierDesktopLifecycle Create()=>Current=new(StartSessionAsync,TimeProvider.System,reconciliationFactory:shared=>MasterdataDesktopReconciliationR28.Blocked(shared,TimeProvider.System));
    public static async Task<CarrierDesktopSession> StartSessionAsync(CancellationToken ct)
    {
        NpgsqlDataSource? source=null;ServiceProvider? services=null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var config=CarrierDesktopProfile.Load(Path.Combine(AppContext.BaseDirectory,"FugaTES.carrier.json"));CarrierDesktopProfile.Validate(config);
            var runtime=DesktopRuntime.Current??throw new DesktopStartupException("CARRIER_DESKTOP_DATABASE_NOT_READY");
            var store=runtime.CreateCarrierStore(out source);
            await ValidateDatabaseAsync(source,ct).ConfigureAwait(false);
            // Resolve only the credential targets in this proven HML profile; no enumeration/fallback.
            var credentials=new CarrierServiceGuardianCredentials(config);_ = await credentials.ObterAsync(ct).ConfigureAwait(false);
            var sapAuth=new CarrierServiceSapAuthentication(config);
            using(var proof=new HttpRequestMessage(HttpMethod.Get,new Uri(config["Sap:BaseUrl"]!)))await sapAuth.AplicarAsync(proof,ct).ConfigureAwait(false);
            var authorization=new CarrierSyncAuthorization("HML",true,true,
                DateTimeOffset.TryParse(config["Carrier:Authorization:UntilUtc"],out var until)?until:DateTimeOffset.MinValue,
                Values(config,"Carrier:Authorization:AllowedBps"),Values(config,"Carrier:Authorization:AllowedSids"),CarrierDesktopProfile.True(config,"Carrier:Authorization:IncrementalReleased"))
                {WritesEnabled=CarrierDesktopProfile.True(config,"Carrier:DesktopGuardianWritesEnabled")};
            var policy=new CarrierSyncPolicy(authorization,TimeProvider.System);
            var registrations=new ServiceCollection();
            registrations.AddSingleton(TimeProvider.System);registrations.AddSingleton<ICarrierSyncStore>(store);
            registrations.AddSingleton<HttpClient>(_=>new(new HttpClientHandler{AllowAutoRedirect=false}));
            registrations.AddSingleton<IAutenticacaoSap>(sapAuth);
            registrations.AddSingleton(new OpcoesSap{BaseUrl=new Uri(config["Sap:BaseUrl"]!),SapClient=config["Sap:SapClient"],Timeout=TimeSpan.FromSeconds(30)});
            registrations.AddSingleton<ClienteHttpParceiroNegocioSap>();registrations.AddSingleton<ConsultaTransportadoraSapAdapter>();registrations.AddSingleton<ConsultarTransportadoraSapCasoDeUso>();
            registrations.AddSingleton(new GuardianHttpOptions{BaseUrl=new Uri(config["Guardian:BaseUrl"]!),Timeout=TimeSpan.FromSeconds(30)});
            registrations.AddSingleton<ICredenciaisGuardianSet26Provider>(credentials);registrations.AddSingleton<GuardianTokenClient>();
            CarrierProtocolBinding.Register(registrations,config,policy);services=registrations.BuildServiceProvider();
            var consulta=services.GetRequiredService<ConsultarTransportadoraSapCasoDeUso>();
            var motor=new SincronizarTransportadoras(services.GetRequiredService<ISapCarrierDiscovery>(),services.GetRequiredService<IGuardianCarrierGateway>(),store,policy,TimeProvider.System,async(bp,token)=>
            {
                var result=await consulta.ExecutarAsync(bp,token).ConfigureAwait(false);
                if(result.Status!=StatusConsultaSap.Sucesso||result.Dados is null||result.Dados.BusinessPartner!=bp||result.Dados.CategoriaValidada!=CategoriaParceiroNegocio.Transportadora||result.Dados.BloqueioSap!=SituacaoBloqueioSap.NaoBloqueado)throw new CarrierGatewayException("SAP_CARRIER_INDIVIDUAL_READ_FAILED");
                var data=result.Dados;return new(bp,data.Nome??"",data.NomeCompletoParceiroNegocio??"");
            });
            var manual=new CarrierManualRequestHandler(policy,(r,token)=>store.EnfileirarPedidoAsync(r.Id,r.Bp,r.AuthenticatedSid,r.RequestedUtc,token),TimeProvider.System);
            var ownedSource=source;var ownedServices=services;
            CarrierStoredCycle? lastCycle=null;
            var readRepository=new CarrierSyncReadRepository(ownedSource);
            return new(async token=>{lastCycle=await motor.ExecutarCicloAsync(token).ConfigureAwait(false);},manual.HandleAsync,
                async()=>{try{await ownedServices.DisposeAsync().ConfigureAwait(false);}finally{await ownedSource.DisposeAsync().ConfigureAwait(false);}},
                async token=>{
                    var cycle=lastCycle;
                    if(cycle is null)return new(EstadoMasterdataR3.Bloqueado,0,0,0,0,null,false);
                    var checkpoint=await store.CarregarCheckpointAsync(token).ConfigureAwait(false);
                    var durable=checkpoint.Watermark.HasValue;
                    var status=cycle.Pending>0?EstadoMasterdataR3.Indeterminado:cycle.Failed>0||!durable?EstadoMasterdataR3.Parcial:EstadoMasterdataR3.Sincronizado;
                    return new(status,cycle.Consulted,cycle.Created+cycle.Updated+cycle.Unchanged,cycle.Pending,cycle.Failed,
                        durable?JsonSerializer.Serialize(checkpoint):null,durable);
                },token=>readRepository.ConsultarCiclosAsync(new CarrierSyncFiltro(Limit:100),token));
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){if(services is not null)await services.DisposeAsync();if(source is not null)await source.DisposeAsync();throw;}
        catch(Exception ex)
        {
            if(services is not null)await services.DisposeAsync();if(source is not null)await source.DisposeAsync();
            if(ex is DesktopStartupException)throw;
            if(ex is CarrierServiceStartupException known)throw new DesktopStartupException(known.Code);
            throw new DesktopStartupException("CARRIER_DESKTOP_PREFLIGHT_FAILED");
        }
    }
    static string[] Values(IConfiguration config,string path)=>config.GetSection(path).GetChildren().Select(c=>c.Value).Where(v=>!string.IsNullOrWhiteSpace(v)).Cast<string>().ToArray();
    private static async Task ValidateDatabaseAsync(NpgsqlDataSource source,CancellationToken ct)
    {
        await using var connection=await source.OpenConnectionAsync(ct).ConfigureAwait(false);await using var transaction=await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using(var command=new NpgsqlCommand("set transaction read only",connection,transaction))await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using(var identity=new NpgsqlCommand("select current_database()='fugates_dev' and current_user='fugates_dev_app' and not has_schema_privilege(current_user,'fugates','CREATE') and not (select rolsuper or rolcreatedb or rolcreaterole from pg_roles where rolname=current_user)",connection,transaction))
            if(await identity.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true)throw new DesktopStartupException("CARRIER_DESKTOP_DATABASE_IDENTITY_INVALID");
        foreach(var table in new[]{"cycle","state","attempt","step","checkpoint","request"})
        {
            await using var command=new NpgsqlCommand("select case when to_regclass(@name) is null then false else has_table_privilege(current_user,@name,'SELECT') and has_table_privilege(current_user,@name,'INSERT') and has_table_privilege(current_user,@name,'UPDATE') and not has_table_privilege(current_user,@name,'DELETE,TRUNCATE') end",connection,transaction);
            command.Parameters.AddWithValue("name","fugates.carrier_sync_"+table);
            if(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true)throw new DesktopStartupException("CARRIER_DESKTOP_MIGRATION_PENDING_GAIA");
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}