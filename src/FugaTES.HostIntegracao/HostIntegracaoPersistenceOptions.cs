namespace FugaTES.HostIntegracao;

public sealed class HostIntegracaoPersistenceOptions
{
    public string Mode { get; set; } = string.Empty;

    public bool EhValida()
    {
        return string.Equals(Mode, "Fake", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Mode, "PostgreSql", StringComparison.OrdinalIgnoreCase);
    }

    public bool UsaPostgreSql()
    {
        return string.Equals(Mode, "PostgreSql", StringComparison.OrdinalIgnoreCase);
    }
}
