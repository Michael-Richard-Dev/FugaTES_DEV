using System.Data.Common;
using FugaTES.Aplicacao.Servicos.Mvp;
namespace FugaTES.Persistencia.MotorIntegracao;

// Executed on each newly opened physical ACM connector, before it reaches a repository.
// Server settings are authoritative; an SSH tunnel's local port is not the server port.
public static class AcmPostgreSqlIdentityGuard
{
    /// <param name="expectedRole">
    /// Role que a conexao fisica tem que confirmar em current_user e session_user. Por padrao a
    /// role leitora; o receptor SAP, quando autorizado, passa a sua propria identidade exclusiva.
    /// Nenhum outro criterio e relaxado: cluster, porta, base, schema e recovery seguem fixos.
    /// </param>
    public static async Task VerifyAsync(DbConnection connection, CancellationToken ct = default, string? expectedRole = null)
    {
        var role = string.IsNullOrWhiteSpace(expectedRole)
            ? NpgsqlDataSourceFactory.AcmApplicationRole
            : expectedRole;
        if (!NpgsqlDataSourceFactory.EhRoleGuardada(role))
            throw new AcmR57BlockedException("R146E_PHYSICAL_DATABASE_IDENTITY_MISMATCH");
        try
        {
            await using var tx = await connection.BeginTransactionAsync(ct);
            try
            {
                await using (var readOnly = connection.CreateCommand())
                {
                    readOnly.Transaction = tx;
                    readOnly.CommandTimeout = 5;
                    readOnly.CommandText = "set transaction read only";
                    await readOnly.ExecuteNonQueryAsync(ct);
                }
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = 5;
                cmd.CommandText = """
                    select current_setting('cluster_name'), current_setting('port')::integer,
                           inet_server_port(), current_database(), current_schema(),
                           current_user, session_user, pg_is_in_recovery()
                    """;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct) || reader.IsDBNull(0) || reader.GetString(0) != "18/fugatesacmdev" ||
                    reader.GetInt32(1) != 5433 || (!reader.IsDBNull(2) && reader.GetInt32(2) != 5433) ||
                    reader.GetString(3) != "fugates_dev" || reader.IsDBNull(4) || reader.GetString(4) != "fugates" ||
                    reader.GetString(5) != role || reader.GetString(6) != role || reader.GetBoolean(7))
                    throw new AcmR57BlockedException("R146E_PHYSICAL_DATABASE_IDENTITY_MISMATCH");
            }
            finally { await tx.RollbackAsync(CancellationToken.None); }
        }
        catch(OperationCanceledException) { throw; }
        catch(AcmR57BlockedException) { throw; }
        catch { throw new AcmR57BlockedException("R146E_DATABASE_IDENTITY_UNVERIFIED"); }
    }
}
