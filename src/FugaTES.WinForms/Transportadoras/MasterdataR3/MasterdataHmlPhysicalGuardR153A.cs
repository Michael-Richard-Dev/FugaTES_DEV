using FugaTES.Persistencia.Transportadoras;
using FugaTES.WinForms.Runtime;
using Npgsql;
namespace FugaTES.WinForms.Transportadoras.MasterdataR3;

// R153A: preserva, DENTRO do caminho canonico R139, a segregacao fisica do cluster MasterData HML
// que havia sido construida em R147B/R150C. Nao e um caminho de cadastro alternativo: nao abre
// transacao, nao reserva nada, nao despacha. Apenas recusa a sessao quando o servidor nao e o
// cluster MasterData aprovado.
//
// Contrato fisico (R146A/GAIA): cluster 18/fugatesmdhml, listener 5434 na VM,
// tunel Windows 127.0.0.1:15434 -> VM 127.0.0.1:5434, database fugates_masterdata_hml,
// schema fugates, app role fugates_masterdata_hml_app.
//
// SingleCreateDatabaseProfileR139.Options ja recusa 5432/5433 e a identidade fugates_dev.
// Aqui a recusa e estendida a 15432/15433 (tuneis dos clusters ACM) e a divergencia de
// database/role/porta frente ao alvo aprovado, antes e depois de abrir a conexao.
public static class MasterdataHmlPhysicalGuardR153A
{
    // Porta REAL do listener no servidor, distinta da porta local do tunel.
    public const int PortaFisicaEsperada = 5434;
    public const string ClusterEsperado = "18/fugatesmdhml";

    // Identidade declarada no perfil, verificada ANTES de qualquer conexao.
    public static string? ValidarPerfil(SingleCreateDatabaseProfileR139 perfil, MasterdataHmlTargetR147B? alvo = null)
    {
        ArgumentNullException.ThrowIfNull(perfil);
        alvo ??= MasterdataHmlTargetR147B.Proposto;
        if (MasterdataHmlDataSourceR147B.PortasProibidas.Contains(perfil.Port)) return "MASTERDATA_HML_FORBIDDEN_ACM_PORT";
        if (perfil.Port != alvo.Port) return "MASTERDATA_HML_PORT_MISMATCH";
        if (!string.Equals(perfil.Host, alvo.Host, StringComparison.Ordinal)) return "MASTERDATA_HML_HOST_MISMATCH";
        if (!string.Equals(perfil.Database, alvo.Database, StringComparison.Ordinal)) return "MASTERDATA_HML_DATABASE_MISMATCH";
        if (!string.Equals(perfil.Role, alvo.AppRole, StringComparison.Ordinal)) return "MASTERDATA_HML_APP_ROLE_MISMATCH";
        if (!string.Equals(perfil.Schema, alvo.Schema, StringComparison.Ordinal)) return "MASTERDATA_HML_SCHEMA_MISMATCH";
        return null;
    }

    // Guarda de identidade FISICA: a connection string prometer 15434 nao basta, o servidor
    // precisa confirmar que e o cluster MasterData segregado. SELECT puro, sem efeito colateral.
    public const string IdentidadeSql = """
        select current_database()
             , coalesce(current_setting('cluster_name',true),'')
             , current_setting('port')::int
             , current_user
             , current_setting('server_version_num')::int
             , pg_is_in_recovery()
        """;

    public static async Task<string?> VerificarIdentidadeFisicaAsync(
        NpgsqlDataSource source, MasterdataHmlTargetR147B? alvo = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        alvo ??= MasterdataHmlTargetR147B.Proposto;
        try
        {
            await using var command = source.CreateCommand(IdentidadeSql);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return "MASTERDATA_HML_IDENTITY_UNREADABLE";
            if (!string.Equals(reader.GetString(0), alvo.Database, StringComparison.Ordinal)) return "MASTERDATA_HML_PHYSICAL_DATABASE_MISMATCH";
            if (!string.Equals(reader.GetString(1), ClusterEsperado, StringComparison.Ordinal)) return "MASTERDATA_HML_PHYSICAL_CLUSTER_MISMATCH";
            if (reader.GetInt32(2) != PortaFisicaEsperada) return "MASTERDATA_HML_PHYSICAL_PORT_MISMATCH";
            if (!string.Equals(reader.GetString(3), alvo.AppRole, StringComparison.Ordinal)) return "MASTERDATA_HML_PHYSICAL_ROLE_MISMATCH";
            if (reader.GetInt32(4) is < 180000 or > 189999) return "MASTERDATA_HML_PHYSICAL_VERSION_MISMATCH";
            if (reader.GetBoolean(5)) return "MASTERDATA_HML_SERVER_IN_RECOVERY";
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return "MASTERDATA_HML_CANCELLED"; }
        catch { return "MASTERDATA_HML_IDENTITY_PROBE_FAILED"; }
    }

    // Invocada do caminho canonico R139. Falha fechada: lanca, e o PrepareAsync do R139
    // descarta o datasource dedicado. Sem fallback para o pool ACM compartilhado.
    public static async Task VerifyAsync(
        NpgsqlDataSource source, SingleCreateDatabaseProfileR139 perfil, CancellationToken ct)
    {
        if (ValidarPerfil(perfil) is { } perfilInvalido) throw new DesktopStartupException(perfilInvalido);
        if (await VerificarIdentidadeFisicaAsync(source, ct: ct).ConfigureAwait(false) is { } divergencia)
            throw new DesktopStartupException(divergencia);
    }
}
