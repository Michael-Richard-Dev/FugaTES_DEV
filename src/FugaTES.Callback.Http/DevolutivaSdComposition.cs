using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Persistencia.MotorIntegracao;
using FugaTES.Persistencia.Sd;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FugaTES.Callback.Http;

/// <summary>
/// Liga o receptor da devolutiva SD ao PostgreSQL 5433, com a mesma exigencia de vinculo
/// dedicado do receptor Toledo: host, porta, banco, schema e PAPEL conferidos, senha por
/// passfile. Um vinculo trocado nao vira gravacao no banco errado.
///
/// Desligado por padrao: sem ToledoInbound:SdVendaEnabled=true nada e registrado, e o endpoint
/// responde 503 em vez de gravar.
/// </summary>
public static class DevolutivaSdComposition
{
    public const string DataSourceKey = "SD_DEVOLUTIVA_DEV_5433";
    public const string Flag = "SdInbound:DevolutivaEnabled";

    public static bool Configure(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var flag = configuration[Flag];
        // Valor ambiguo e erro, nao "desligado por via das duvidas": um "sim" ou "1" no arquivo
        // passaria despercebido como desligado.
        if (flag is not null && !bool.TryParse(flag, out _))
            throw new InvalidOperationException($"{Flag} must be explicit true/false.");
        if (!bool.TryParse(flag, out var habilitado) || !habilitado) return false;

        var cs = ToledoCallbackComposition.ValidateDedicatedBinding(
            configuration.GetConnectionString("SdDevolutiva") ?? configuration.GetConnectionString("Toledo"));
        services.AddKeyedSingleton<NpgsqlDataSource>(DataSourceKey, (_, _) => NpgsqlDataSourceFactory.CriarAcm(cs));
        services.TryAddScoped<IDevolutivasSdRepositorio>(sp =>
            new DevolutivasSdPostgreSql(sp.GetRequiredKeyedService<NpgsqlDataSource>(DataSourceKey)));
        return true;
    }
}
