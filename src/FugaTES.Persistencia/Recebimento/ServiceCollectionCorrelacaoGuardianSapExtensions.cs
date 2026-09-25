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
        services.AddScoped<RepositorioCorrelacaoGuardianSapPostgreSql>();
        services.AddScoped<IRepositorioCorrelacaoRecebimento>(sp => sp.GetRequiredService<RepositorioCorrelacaoGuardianSapPostgreSql>());
        services.AddScoped<IRegistroCallbackPesagemSapStore>(sp => sp.GetRequiredService<RepositorioCorrelacaoGuardianSapPostgreSql>());
        return services;
    }
}
