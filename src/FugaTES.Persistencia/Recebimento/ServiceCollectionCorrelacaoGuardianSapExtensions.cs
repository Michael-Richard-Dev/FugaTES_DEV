using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Persistencia.MotorIntegracao;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FugaTES.Persistencia.Recebimento;

public static class ServiceCollectionCorrelacaoGuardianSapExtensions
{
    public static IServiceCollection AddCorrelacaoGuardianSapPostgreSql(this IServiceCollection services, string? connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSourceFactory.Criar(connectionString));
        services.AddScoped<IRepositorioCorrelacaoRecebimento, RepositorioCorrelacaoGuardianSapPostgreSql>();
        return services;
    }
}
