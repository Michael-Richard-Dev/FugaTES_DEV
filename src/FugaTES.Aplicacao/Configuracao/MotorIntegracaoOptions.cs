namespace FugaTES.Aplicacao.Configuracao;

public sealed class MotorIntegracaoOptions
{
    public TimeSpan LeaseDuration { get; set; }

    public TimeSpan LeaseRenewInterval { get; set; }

    public TimeSpan RetryBackoff { get; set; }

    public bool EhValida()
    {
        return LeaseDuration > TimeSpan.Zero
            && LeaseRenewInterval > TimeSpan.Zero
            && LeaseRenewInterval < LeaseDuration
            && RetryBackoff > TimeSpan.Zero;
    }
}
