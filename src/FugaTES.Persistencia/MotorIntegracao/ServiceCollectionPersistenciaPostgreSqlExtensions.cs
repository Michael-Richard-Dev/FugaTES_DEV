using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FugaTES.Persistencia.MotorIntegracao;

public static class ServiceCollectionPersistenciaPostgreSqlExtensions
{
    public static IServiceCollection AddRepositorioMotorIntegracaoPostgreSql(this IServiceCollection services, string? connectionString, bool requireAcmPhysicalIdentity = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<NpgsqlDataSource>(_ => requireAcmPhysicalIdentity
            ? NpgsqlDataSourceFactory.CriarAcm(connectionString)
            : NpgsqlDataSourceFactory.Criar(connectionString));
        services.AddTransient<RepositorioMotorIntegracaoPostgreSql>();
        return services;
    }
}
