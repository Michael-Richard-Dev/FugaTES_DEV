using Npgsql;

namespace FugaTES.Persistencia.MotorIntegracao;

public static class NpgsqlDataSourceFactory
{
    public const string ConnectionStringName = "FugaTES";
    public const string ConnectionStringKey = "ConnectionStrings:FugaTES";
    public const string AcmApplicationRole = "fugates_acm_hml_app";

    /// <summary>
    /// Identidade exclusiva proposta para o receptor SAP. Fica aqui, junto da role leitora, para
    /// que ambas passem exatamente pelas MESMAS guardas de conexao: destino 5433/fugates_dev,
    /// schema fugates, host loopback, Passfile explicito e verificacao de identidade fisica a
    /// cada conexao. Sem isto a role nova cairia no caminho generico, sem guarda alguma.
    /// A role ainda NAO existe no servidor; sua criacao depende de autorizacao da Hera.
    /// </summary>
    public const string SapInboundApplicationRole = "fugates_sap_inbound_app";

    /// <summary>
    /// Identidade GET-only exclusiva da API privada de leitura (18762). Existe porque
    /// fugates_acm_hml_app e compartilhada com o servico de comandos 18763, que precisa de
    /// escrita: separar a leitura em uma role sem nenhum INSERT/UPDATE/DELETE e a unica forma
    /// de dar isolamento a API sem quebrar os demais consumidores.
    /// A role ainda NAO existe no servidor; sua criacao depende de autorizacao da Hera.
    /// </summary>
    public const string DevReadOnlyApplicationRole = "fugates_dev_read_app";

    /// <summary>Identidades submetidas ao caminho reforcado de conexao.</summary>
    public static bool EhRoleGuardada(string? username)
        => username == AcmApplicationRole
        || username == SapInboundApplicationRole
        || username == DevReadOnlyApplicationRole;

    public static NpgsqlDataSource CriarAcm(string? connectionString)
    {
        NpgsqlConnectionStringBuilder metadata;
        try
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException();
            metadata = new NpgsqlConnectionStringBuilder(connectionString);
            if (metadata.Port != 5433 || !EhRoleGuardada(metadata.Username) || metadata.Database != "fugates_dev" ||
                metadata.SearchPath != "fugates" ||
                (metadata.Host != "127.0.0.1" && metadata.Host != "localhost"))
                throw new InvalidOperationException();
        }
        catch
        {
            throw new InvalidOperationException("R165R_ACM_CONNECTION_METADATA_REQUIRED");
        }

        // Explicit Passfile overrides PGPASSFILE/default paths. PGPASSWORD must not override it.
        if (!HasExplicitAcmPassfile(metadata) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PGPASSWORD")))
            throw new InvalidOperationException("R165R_EXPLICIT_ACM_PASSFILE_REQUIRED");

        // A identidade esperada acompanha a role da propria connection string: cada conexao fisica
        // precisa confirmar que current_user e session_user sao exatamente essa role.
        var roleEsperada = metadata.Username!;
        var builder = new NpgsqlDataSourceBuilder(metadata.ConnectionString);
        builder.UsePhysicalConnectionInitializer(
            connection => AcmPostgreSqlIdentityGuard.VerifyAsync(connection, expectedRole: roleEsperada).GetAwaiter().GetResult(),
            connection => AcmPostgreSqlIdentityGuard.VerifyAsync(connection, expectedRole: roleEsperada));
        return builder.Build();
    }

    public static bool HasExplicitAcmPassfile(NpgsqlConnectionStringBuilder metadata) =>
        metadata.Password is null && !string.IsNullOrWhiteSpace(metadata.Passfile) &&
        metadata.Passfile.StartsWith("/", StringComparison.Ordinal) &&
        !metadata.Passfile.StartsWith("//", StringComparison.Ordinal) &&
        !metadata.Passfile.Contains('\\') && !metadata.Passfile.Contains(';') &&
        !metadata.Passfile.Any(char.IsControl) &&
        !metadata.Passfile.Contains(".fugates-r116-dev", StringComparison.OrdinalIgnoreCase);

    public static NpgsqlDataSource Criar(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"{ConnectionStringKey} obrigatoria para PersistenceMode=PostgreSql.");
        }

        // Callers outside HostIntegracao (including callback TryAddSingleton registrations)
        // cannot bypass physical identity by using the ordinary factory com uma role guardada.
        if (EhRoleGuardada(new NpgsqlConnectionStringBuilder(connectionString).Username))
            return CriarAcm(connectionString);

        return NpgsqlDataSource.Create(connectionString);
    }
}
