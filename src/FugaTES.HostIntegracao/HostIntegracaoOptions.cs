namespace FugaTES.HostIntegracao;

public sealed class HostIntegracaoOptions
{
    public TimeSpan PollInterval { get; set; }

    public TimeSpan ShutdownGracePeriod { get; set; }

    public string ExternalMode { get; set; } = string.Empty;

    public bool EhValida()
    {
        return PollInterval > TimeSpan.Zero
            && ShutdownGracePeriod > TimeSpan.Zero
            && (string.Equals(ExternalMode, "Fake", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ExternalMode, "Guardian", StringComparison.OrdinalIgnoreCase));
    }
}
