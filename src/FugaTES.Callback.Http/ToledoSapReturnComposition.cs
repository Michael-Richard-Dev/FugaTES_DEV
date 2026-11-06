using System.Globalization;
using System.Net;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using FugaTES.Persistencia.MotorIntegracao;
using FugaTES.Persistencia.Recebimento;
using Npgsql;
using FugaTES.HostIntegracao;
namespace FugaTES.Callback.Http;

/// <summary>
/// Os ambientes em que o envio Toledo-&gt;SAP esta autorizado, e o SAP a que cada um corresponde.
///
/// O par e INDIVISIVEL, e e a mesma forma do ingresso da admissao e do par (banco, papel): o nome
/// DECLARADO em ToledoSapReturn:Environment tem de corresponder ao SAP que o processo realmente
/// fala. Um receptor de DEV declarando QAS120, ou o contrario, nao e ambiente nenhum.
///
/// POR QUE O NOME DECLARADO NAO E O NOME DO AMBIENTE. O DEV declara "DEV" desde antes de existir
/// um segundo ambiente, e esse valor esta no EnvironmentFile em producao. Renomear para "DEV110"
/// faria o receptor do DEV parar de subir na proxima publicacao, para ganhar simetria de nome -
/// troca ruim. O mapeamento resolve sem tocar no que roda.
///
/// A AuthorizationReference continua sendo o REGISTRO da autorizacao, escrito pelo operador, e
/// nao e conferida contra lista: fixar as referencias aceitas aqui exigiria que eu soubesse a do
/// DEV em producao, e supor errado faria o receptor do DEV recusar subir.
/// </summary>
internal sealed record ToledoSapReturnAmbiente(string Declarado, string Sap)
{
    private static readonly ToledoSapReturnAmbiente[] Aprovados =
    [
        new("DEV", "DEV110"),
        new("QAS120", "QAS120"),
    ];

    /// <summary>O ambiente deste par, ou nulo quando o declarado nao corresponde ao SAP real.</summary>
    internal static ToledoSapReturnAmbiente? Por(string? declarado, string? sap)
        => Array.Find(Aprovados, a => a.Declarado == declarado && a.Sap == sap);
}

public static class ToledoSapReturnComposition
{
    public const string Key = "TOLEDO_SAP_RETURN";
    public static ToledoSapAdmissionWindow? Configure(IServiceCollection services, IConfiguration config)
    {
        var ackMode=config["ToledoSapReturn:GuardianAckMode"];
        if(ackMode is not(null or "Receipt" or "SapConfirmed"))throw new InvalidOperationException("TOLEDO_SAP_ACK_MODE_INVALID");
        var flag=config["ToledoSapReturn:Enabled"];
        if(flag is null){if(ackMode=="SapConfirmed")throw new InvalidOperationException("TOLEDO_SAP_ACK_REQUIRES_EXISTING_WORKER");return null;}
        if(!bool.TryParse(flag,out var enabled))throw new InvalidOperationException("TOLEDO_SAP_EXPLICIT_ACTIVATION_REQUIRED");
        if(!enabled){if(ackMode=="SapConfirmed")throw new InvalidOperationException("TOLEDO_SAP_ACK_REQUIRES_EXISTING_WORKER");return null;}
        static bool True(string? v)=>bool.TryParse(v,out var value)&&value;
        // O ambiente DECLARADO tem de corresponder ao SAP que este processo fala. Ate 06/11/2026
        // isto era "Environment != DEV", o que impedia o QAS de enviar - e, no dia em que o QAS
        // passasse a declarar o que quisesse, nao impediria nada: so o nome era conferido, nunca
        // contra quem o processo realmente conversa.
        var sapDeclarado=Uri.TryCreate(config["Sap:BaseUrl"],UriKind.Absolute,out var baseUrl)
            ? AmbientesSap.Por(baseUrl.GetLeftPart(UriPartial.Authority),config["Sap:SapClient"])?.Nome
            : null;
        if(ToledoSapReturnAmbiente.Por(config["ToledoSapReturn:Environment"],sapDeclarado) is null
           || !True(config["ToledoSapReturn:ExecutionAuthorized"]) ||
           !True(config["ToledoSapReturn:AutomaticProcessingEnabled"]) || !True(config["ToledoSapReturn:OriginalDocumentApproved"]))
            throw new InvalidOperationException("TOLEDO_SAP_EXPLICIT_ACTIVATION_REQUIRED");
        if(bool.TryParse(config["FUGATES_INTEGRATION_WORKER_ENABLED"],out var global)&&global)
            throw new InvalidOperationException("TOLEDO_SAP_GLOBAL_WORKER_FORBIDDEN");
        ConfigureAuthentication(services,config);
        var continuousFlag=config["ToledoSapReturn:ContinuousEnabled"];
        if(continuousFlag is not null&&!bool.TryParse(continuousFlag,out _))throw new InvalidOperationException("TOLEDO_SAP_CONTINUOUS_MODE_MUST_BE_EXPLICIT");
        var continuous=bool.TryParse(continuousFlag,out var continuousValue)&&continuousValue;
        if(!DateTimeOffset.TryParse(config["ToledoSapReturn:NotBeforeUtc"],CultureInfo.InvariantCulture,DateTimeStyles.None,out var start)||start.Offset!=TimeSpan.Zero)
            throw new InvalidOperationException("TOLEDO_SAP_UTC_WINDOW_REQUIRED");
        DateTimeOffset end=default;
        if(continuous)
        {
            if(!string.IsNullOrWhiteSpace(config["ToledoSapReturn:UntilUtc"]))throw new InvalidOperationException("TOLEDO_SAP_ACTIVATION_MODE_AMBIGUOUS");
        }
        else if(!DateTimeOffset.TryParse(config["ToledoSapReturn:UntilUtc"],CultureInfo.InvariantCulture,DateTimeStyles.None,out end)||end.Offset!=TimeSpan.Zero)
            throw new InvalidOperationException("TOLEDO_SAP_UTC_WINDOW_REQUIRED");
        var window=new ToledoSapAdmissionWindow(start,end,config["ToledoSapReturn:ApprovedSourceIdentity"]??"",config["ToledoSapReturn:AuthorizationReference"]??"",True(config["ToledoSapReturn:CompleteDocumentAuthorized"])){ContinuoAutorizado=continuous};
        if(!window.Valida)throw new InvalidOperationException("TOLEDO_SAP_BOUNDED_AUTHORIZATION_REQUIRED");
        if(!Uri.TryCreate(config["Sap:BaseUrl"],UriKind.Absolute,out var sap)||sap.Scheme!="https"||sap.UserInfo.Length!=0||sap.Query.Length!=0||sap.Fragment.Length!=0||
           !FugaTES.Aplicacao.Configuracao.AmbientesSap.EhAprovado(sap.GetLeftPart(UriPartial.Authority),config["Sap:SapClient"]))throw new InvalidOperationException("TOLEDO_SAP_AUTHORIZED_DEV_ENDPOINT_REQUIRED");
        services.AddSingleton(window);
        if(ackMode=="SapConfirmed")
        {
            services.AddSingleton(ToledoSapCallbackAckOptions.Approved);
            services.AddScoped<IToledoSapCallbackOutcomeRead>(sp=>new ToledoSapCallbackOutcomeRead(sp.GetRequiredKeyedService<NpgsqlDataSource>(ToledoCallbackComposition.DataSourceKey),AmbientesBanco.PorNome(AmbientesSap.Por(sap,config["Sap:SapClient"])!.Nome)));
            services.AddScoped<ToledoSapCallbackAcknowledgement>();
        }
        services.AddKeyedScoped<DevolutivaToledoSapPostgreSql>(Key,(sp,_)=>new(sp.GetRequiredKeyedService<NpgsqlDataSource>(ToledoCallbackComposition.DataSourceKey),window));
        services.AddKeyedScoped<MotorIntegracaoServico>(Key,(sp,_)=>
        {
            var db=sp.GetRequiredKeyedService<NpgsqlDataSource>(ToledoCallbackComposition.DataSourceKey);
            var options=new OpcoesSap{BaseUrl=sap,SapClient=config["Sap:SapClient"]!,Timeout=TimeSpan.FromSeconds(25)};
            // Dispose the client when its DI scope is disposed, rather than create one per receipt.
            var transport=sp.GetRequiredKeyedService<ToledoSapHttpTransport>(Key);
            var gateway=new DocumentoToledoSapGateway(new(transport.Client,options,sp.GetRequiredService<IAutenticacaoSap>(),new CookieContainer()),new(config["ToledoSapReturn:WeighingTimeZone"]??"America/Sao_Paulo"));
            var processor=new ProcessadorDocumentoToledoSap(sp.GetRequiredKeyedService<DevolutivaToledoSapPostgreSql>(Key),gateway);
            return new(new RepositorioMotorIntegracaoPostgreSql(db),processor,new NoDetection(),new ValidadorGenericoIntegracao(),new WorkerIdentity(),new Timeout(),TimeProvider.System,
                new(){LeaseDuration=TimeSpan.FromSeconds(90),LeaseRenewInterval=TimeSpan.FromSeconds(10),RetryBackoff=TimeSpan.FromMinutes(1)});
        });
        services.AddKeyedScoped<ToledoSapHttpTransport>(Key,(_,_)=>new());
        services.AddHostedService<ToledoSapReturnWorker>();
        return window;
    }
    internal static void ConfigureAuthentication(IServiceCollection services,IConfiguration config)
    {
        if(!services.Any(d=>d.ServiceType==typeof(IAutenticacaoSap)))
        {
            if(config["ToledoSapReturn:SapAuthenticationProvider"]!=nameof(CarrierLinuxSapAuthentication)||
                config["ToledoSapReturn:ExpectedSapUser"]!="FUGATES")
                throw new InvalidOperationException("TOLEDO_SAP_EXISTING_AUTHENTICATION_PROVIDER_REQUIRED");
            services.AddScoped<IAutenticacaoSap>(_=>
            {
                if(!OperatingSystem.IsLinux())throw new InvalidOperationException("TOLEDO_SAP_SYSTEMD_CREDENTIALS_REQUIRED");
                var directory=Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY")??throw new InvalidOperationException("TOLEDO_SAP_SYSTEMD_CREDENTIALS_REQUIRED");
                if(!Uri.TryCreate(config["Sap:BaseUrl"],UriKind.Absolute,out var origin))throw new InvalidOperationException("TOLEDO_SAP_AUTHORIZED_DEV_ENDPOINT_REQUIRED");
                return new CarrierLinuxSapAuthentication(origin,new CarrierSystemdCredentials(directory),"FUGATES");
            });
        }
    }
    sealed class NoDetection:IDetectorCandidatosIntegracao{public Task<IReadOnlyList<CandidatoIntegracao>> DetectarAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<CandidatoIntegracao>>([]);}
    sealed class WorkerIdentity:IIdentidadeWorkerProvider{public IdentidadeWorkerIntegracao Obter()=>new("toledo-sap-return");}
    sealed class Timeout:IPoliticaTimeoutProcessamentoIntegracao{public TimeSpan ObterTimeout(ClaimIntegracaoMotor c)=>TimeSpan.FromSeconds(60);}
}
public sealed class ToledoSapHttpTransport:IDisposable
{
    public HttpClient Client{get;}=new(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false}){Timeout=TimeSpan.FromSeconds(30)};
    public void Dispose()=>Client.Dispose();
}

// This is an adapter around MotorIntegracaoServico.ExecutarPorIdAsync, never its global polling cycle.
public sealed class ToledoSapReturnWorker(IServiceScopeFactory factory,ToledoSapAdmissionWindow window):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested&&DateTimeOffset.UtcNow<window.InicioUtc)await Task.Delay(500,stoppingToken);
        await using var scope=factory.CreateAsyncScope();
        var db=scope.ServiceProvider.GetRequiredKeyedService<NpgsqlDataSource>(ToledoCallbackComposition.DataSourceKey);
        await using var exclusive=await db.OpenConnectionAsync(stoppingToken);
        await using(var gate=new NpgsqlCommand("select pg_try_advisory_lock(165165,2801);",exclusive))
            if(await gate.ExecuteScalarAsync(stoppingToken) is not true)return; // No competing initiator.
        while(!stoppingToken.IsCancellationRequested)
        {
            await ToledoSapLeaseRecovery.RecuperarAsync(db,window,stoppingToken);
            if(!window.ContinuoAutorizado&&DateTimeOffset.UtcNow>=window.FimUtc)
            {
                if(DateTimeOffset.UtcNow>=window.FimUtc.AddSeconds(100))return;
                await Task.Delay(1000,stoppingToken);continue; // Metadata recovery only; NO business dispatch after the window.
            }
            await ProcessOneAsync();
        }
        async Task ProcessOneAsync()
        {
            const string sql="""
            select i.id from fugates.integracao i
            where i.origem='GUARDIAN' and i.destino='SAP' and i.operacao=@operation
              and ((i.estado='PENDENTE' and i.tipo_disparo_pendente='AUTOMATICO'
                and i.criado_em>=@start and (@continuous or i.criado_em<@end)
                and exists(select 1 from fugates.auditoria_integracao a where a.integracao_id=i.id
                  and a.evento='toledo_sap_admitido' and a.diagnostico=@reference))
              or (i.estado='AGUARDANDO_REPROCESSAMENTO' and i.tipo_disparo_pendente='REPROCESSAMENTO'
                and exists(select 1 from fugates.auditoria_integracao a where a.integracao_id=i.id
                  and a.evento='toledo_sap_reenvio_solicitado' and a.criada_em>=@start and (@continuous or a.criada_em<@end)
                  and (case when a.evento='toledo_sap_reenvio_solicitado' then a.diagnostico::jsonb->>'authorizationReference' end)=@reference)))
            order by i.criado_em limit 1;
            """;
            Guid? id;
            await using(var cmd=new NpgsqlCommand(sql,exclusive))
            {cmd.CommandTimeout=5;cmd.Parameters.AddWithValue("operation",ToledoSapWork.Operacao);cmd.Parameters.AddWithValue("start",window.InicioUtc);cmd.Parameters.AddWithValue("end",window.ContinuoAutorizado?window.InicioUtc:window.FimUtc);cmd.Parameters.AddWithValue("continuous",window.ContinuoAutorizado);cmd.Parameters.AddWithValue("reference",window.ReferenciaAutorizacao);id=await cmd.ExecuteScalarAsync(stoppingToken) as Guid?;}
            if(id is{} target)
            {
                await using var work=factory.CreateAsyncScope();
                using var execution=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                if(!window.ContinuoAutorizado)
                {
                    var remaining=window.FimUtc-DateTimeOffset.UtcNow;if(remaining<=TimeSpan.Zero)return;
                    execution.CancelAfter(remaining);
                }
                await work.ServiceProvider.GetRequiredKeyedService<MotorIntegracaoServico>(ToledoSapReturnComposition.Key).ExecutarPorIdAsync(target,execution.Token);
            }
            await Task.Delay(1000,stoppingToken);
        }
    }
}
