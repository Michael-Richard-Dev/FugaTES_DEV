namespace FugaTES.HostIntegracao;

public sealed class TimeoutProcessamentoIntegracaoOptions
{
    public TimeSpan Timeout { get; set; }

    public bool EhValida()
    {
        return Timeout > TimeSpan.Zero;
    }
}
