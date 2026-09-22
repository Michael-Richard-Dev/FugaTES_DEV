using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FugaTES.Persistencia.MotorIntegracao;

public static class ServiceCollectionPersistenciaPostgreSqlExtensions
{
    public static IServiceCollection AddRepositorioMotorIntegracaoPostgreSql(this IServiceCollection services, string? connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSourceFactory.Criar(connectionString));
        services.AddTransient<RepositorioMotorIntegracaoPostgreSql>();
        return services;
    }
}
