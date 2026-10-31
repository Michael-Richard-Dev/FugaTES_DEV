using Npgsql;
namespace FugaTES.Persistencia.Transportadoras;

// R147B: datasource PROPRIO do MasterData HML, segregado do cluster compartilhado com ACM/Callback.
// Nao altera DesktopRuntime.CreateReadSource (datasource ACM) nem CreateCarrierStore (CarrierSync R3).
//
// Contrato fisico proposto em R146A (GAIA): cluster 18/fugatesmdhml, listener 5434 na VM,
// tunel Windows 127.0.0.1:15434 -> VM 127.0.0.1:5434, database fugates_masterdata_hml,
// schema fugates, app role fugates_masterdata_hml_app.
//
// Esta classe NAO cria, migra nem escreve nada: apenas valida identidade e compoe a conexao.
public sealed record MasterdataHmlTargetR147B(string Host, int Port, string Database, string Schema, string AppRole)
{
    // Valores propostos no R146A. Continuam sujeitos a atestacao fisica por Atlas.
    public static MasterdataHmlTargetR147B Proposto { get; } =
        new("127.0.0.1", 15434, "fugates_masterdata_hml", "fugates", "fugates_masterdata_hml_app");
}

public static class MasterdataHmlDataSourceR147B
{
    // Portas que NUNCA podem receber a conexao MasterData:
    // 5432/15432 = cluster original compartilhado com ACM/Callback; 5433/15433 = ACM DEV segregado.
    public static readonly int[] PortasProibidas = [5432, 5433, 15432, 15433];

    public static string? Validar(string? connectionString, MasterdataHmlTargetR147B? alvo = null)
    {
        alvo ??= MasterdataHmlTargetR147B.Proposto;
        if (string.IsNullOrWhiteSpace(connectionString)) return "MASTERDATA_HML_CONNECTION_ABSENT";
        NpgsqlConnectionStringBuilder b;
        try { b = new NpgsqlConnectionStringBuilder(connectionString); }
        catch { return "MASTERDATA_HML_CONNECTION_INVALID"; }

        // 4) rejeitar conexao acidental aos clusters ACM, antes de qualquer outra coisa.
        if (PortasProibidas.Contains(b.Port)) return "MASTERDATA_HML_FORBIDDEN_ACM_PORT";
        if (b.Port != alvo.Port) return "MASTERDATA_HML_PORT_MISMATCH";

        // 2) identidade propria do MasterData, no lugar de fugates_dev / fugates_dev_app.
        if (!string.Equals(b.Database, alvo.Database, StringComparison.Ordinal)) return "MASTERDATA_HML_DATABASE_MISMATCH";
        if (!string.Equals(b.Username, alvo.AppRole, StringComparison.Ordinal)) return "MASTERDATA_HML_APP_ROLE_MISMATCH";
        if (!string.Equals(b.Host, alvo.Host, StringComparison.Ordinal)) return "MASTERDATA_HML_HOST_MISMATCH";

        // A identidade antiga nao pode reaparecer por nenhum caminho.
        if (string.Equals(b.Database, "fugates_dev", StringComparison.Ordinal) ||
            string.Equals(b.Username, "fugates_dev_app", StringComparison.Ordinal))
            return "MASTERDATA_HML_LEGACY_IDENTITY_FORBIDDEN";

        // 5) barreira transacional: o escopo ambiente nunca pode envolver esta conexao.
        if (b.Enlist) return "MASTERDATA_HML_ENLIST_MUST_BE_FALSE";
        return null;
    }

    // Compoe o datasource do MasterData. Enlist=false e fixado aqui para que a transacao local
    // explicita do SingleCreate nunca seja promovida a distribuida.
    public static NpgsqlDataSource Criar(string connectionString, MasterdataHmlTargetR147B? alvo = null)
    {
        alvo ??= MasterdataHmlTargetR147B.Proposto;
        var normalizada = Normalizar(connectionString, alvo);
        if (Validar(normalizada, alvo) is { } codigo) throw new InvalidOperationException(codigo);
        return NpgsqlDataSource.Create(normalizada);
    }

    // Aplica apenas o que e composicao: Enlist=false, search_path do schema aprovado e um
    // ApplicationName rastreavel. Nao altera host, porta, database nem usuario informados.
    public static string Normalizar(string connectionString, MasterdataHmlTargetR147B? alvo = null)
    {
        alvo ??= MasterdataHmlTargetR147B.Proposto;
        var b = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Enlist = false,
            ApplicationName = "FugaTES.Desktop.MasterdataHml.SingleCreate"
        };
        if (string.IsNullOrWhiteSpace(b.SearchPath)) b.SearchPath = alvo.Schema;
        return b.ConnectionString;
    }
}
