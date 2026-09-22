using Npgsql;

namespace FugaTES.Persistencia.MotorIntegracao;

public static class NpgsqlDataSourceFactory
{
    public const string ConnectionStringName = "FugaTES";
    public const string ConnectionStringKey = "ConnectionStrings:FugaTES";

    public static NpgsqlDataSource Criar(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"{ConnectionStringKey} obrigatoria para PersistenceMode=PostgreSql.");
        }

        return NpgsqlDataSource.Create(connectionString);
    }
}
