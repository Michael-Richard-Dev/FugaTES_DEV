using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.Mvp;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using System.Data.Common;
using FugaTES.Persistencia.MotorIntegracao;

namespace FugaTES.HostIntegracao;

public static class AcmR104OneShotEntrypoint
{
    public const string Argument="--acm-r104-once";
    public static string? ConfigurationBlock(IConfiguration config,AcmR104OperationalProfile profile)
    {
        var block=AcmR104Scope.ApprovalBlock(profile);if(block is not null)return block;
        foreach(var key in new[]{"FUGATES_INTEGRATION_WORKER_ENABLED","INC005_WORKER_ENABLED","FUGATES_CARRIER_SYNC_ENABLED","CARRIER_WORKER_ENABLED","Sap:Enabled"})
        {
            var text=config[key];
            if(text is not null && (!bool.TryParse(text,out var enabled)||enabled))return "R104_AUTOMATIC_OR_SAP_RUNTIME_FORBIDDEN";
        }
        if(config["HostIntegracao:Mode"]=="CarrierSync" || config["HostIntegracao:PersistenceMode"]!="PostgreSql" ||
            config["HostIntegracao:ExternalMode"]!="Guardian" || config["AcmR57:Enabled"]!="true")return "R104_HOST_PROFILE_INVALID";
        if(config["Guardian:BaseUrl"]!="https://guardian.cloudprix.com.br" || config["Guardian:ContratoSoapEnvironment"]!="HML" ||
            config["Guardian:ContratoSoapInternalHmlHttpApproved"]!="true" ||
            !Uri.TryCreate(config["Guardian:ContratoSoapEndpoint"],UriKind.Absolute,out var soap) ||
            soap.AbsoluteUri!="http://192.168.17.91/WS_GUARDIAN/WS_GUARDIAN_PLUS.asmx")return "R104_GUARDIAN_PROFILE_MISMATCH";
        return null;
    }
    public static void AddUnitaryServices(IServiceCollection services,AcmR104OperationalProfile profile)
    {
        if(services.Any(s=>s.ServiceType==typeof(IHostedService)))throw new AcmR57BlockedException("R104_HOSTED_WORKER_FORBIDDEN");
        services.AddSingleton(profile);
        services.Replace(ServiceDescriptor.Scoped<IAcmR57RuntimeGate,AcmR104RuntimeGate>());
        services.Replace(ServiceDescriptor.Scoped<IAcmR57Preparacao,AcmR104Preparation>());
        services.AddScoped<IAcmR104CallbackCorrelation,AcmR104CallbackCorrelation>();
        services.AddScoped<IAcmR104TargetedDispatch,AcmR104MotorDispatch>();
        services.AddScoped<AcmR104UnitaryInvoker>();
    }
    public static async Task<int> RunAsync(string[] args,HostApplicationBuilder builder,CancellationToken ct=default)
    {
        if(args.Length!=2 || args[0]!=Argument || !Guid.TryParse(args[1],out var id) || id!=AcmR104Scope.IntegrationId)
        {Console.WriteLine("R104_SCOPE_MISMATCH");return 64;}
        try
        {
            var profile=builder.Configuration.GetSection("AcmR104").Get<AcmR104OperationalProfile>()??new();
            var block=ConfigurationBlock(builder.Configuration,profile);
            if(block is not null){Console.WriteLine(block);return 78;}
            HostIntegracaoComposition.Configurar(builder);
            AddUnitaryServices(builder.Services,profile);
            // Build resolves dependencies but never Host.Start/Run, timers or hosted services.
            using var host=builder.Build();using var scope=host.Services.CreateScope();
            var source=scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
            await VerifyDatabaseIdentityAsync(source,ct);
            var result=await scope.ServiceProvider.GetRequiredService<AcmR104UnitaryInvoker>().ExecutarAsync(id,ct);
            Console.WriteLine(result.Code); // No payload, exception detail or credentials.
            if(result.MotorResult is not null)Console.WriteLine("R104_MOTOR_RESULT="+result.MotorResult);
            return result.Code=="R104_TARGET_CONFIRMED" && result.MotorResult==CicloMotorIntegracaoResultado.Processado?0:78;
        }
        catch(AcmR57BlockedException ex){Console.WriteLine(ex.Code);return 78;}
        catch(OperationCanceledException){Console.WriteLine("R104_CANCELLED_RECONCILIATION_REQUIRED");return 70;}
        catch{Console.WriteLine("R104_OPERATION_UNCONFIRMED");return 70;}
    }
    private static async Task VerifyDatabaseIdentityAsync(NpgsqlDataSource source,CancellationToken ct)
    {
        await using var connection=await source.OpenConnectionAsync(ct);
        await VerifyDatabaseIdentityAsync((DbConnection)connection,ct);
    }

    // Same physical guard as the R165R datasource and inert diagnostic. No legacy-role fallback.
    private static Task VerifyDatabaseIdentityAsync(DbConnection connection,CancellationToken ct)
        => AcmPostgreSqlIdentityGuard.VerifyAsync(connection,ct);
}

public sealed class AcmR104MotorDispatch(MotorIntegracaoServico motor):IAcmR104TargetedDispatch
{
    public Task<CicloMotorIntegracaoResultado> ExecutarAsync(Guid id,CancellationToken ct)
        =>id==AcmR104Scope.IntegrationId?motor.ExecutarPorIdAsync(id,ct):throw new AcmR57BlockedException("R104_SCOPE_MISMATCH");
}
