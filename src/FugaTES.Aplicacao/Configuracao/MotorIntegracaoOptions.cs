namespace FugaTES.Aplicacao.Configuracao;

public sealed class MotorIntegracaoOptions
{
    public TimeSpan LeaseDuration { get; set; }

    public TimeSpan LeaseRenewInterval { get; set; }

    public TimeSpan RetryBackoff { get; set; }

    public string? AcmPilotNumeroContrato { get; set; }
    public string? AcmPilotCentro { get; set; }
    public string? AcmPilotItem { get; set; }
    public AcmInboundWindowOptions AcmInboundWindow { get; set; } = new();
    public AcmAutomaticoOptions AcmAutomatico { get; set; } = new();
    public bool UsaJanelaAcm => AcmInboundWindow?.Enabled == true;
    public bool UsaAutomaticoAcm => AcmAutomatico?.Enabled == true;
    private bool UsaPilotoAcm => AcmPilotNumeroContrato is not null || AcmPilotCentro is not null || AcmPilotItem is not null;

    public bool UsaEscopoAcm => UsaJanelaAcm || UsaPilotoAcm || UsaAutomaticoAcm;

    public bool EhValida()
    {
        // Os tres recortes sao exclusivos entre si. Combinar dois deles tornaria indeterminado qual
        // regra decide o que pode ser despachado, e e justamente essa decisao que o motor precisa
        // ter explicita antes de escrever no Guardian.
        var recortes = (UsaJanelaAcm ? 1 : 0) + (UsaPilotoAcm ? 1 : 0) + (UsaAutomaticoAcm ? 1 : 0);
        return LeaseDuration > TimeSpan.Zero
            && LeaseRenewInterval > TimeSpan.Zero
            && LeaseRenewInterval < LeaseDuration
            && RetryBackoff > TimeSpan.Zero
            && AcmInboundWindow is not null
            && AcmAutomatico is not null
            && recortes <= 1
            && (!UsaJanelaAcm || AcmInboundWindow.EhValida())
            && (!UsaAutomaticoAcm || AcmAutomatico.EhValida())
            && (!UsaPilotoAcm ||
                (!string.IsNullOrWhiteSpace(AcmPilotNumeroContrato) &&
                 AcmPilotNumeroContrato.All(char.IsAsciiDigit) &&
                 !string.IsNullOrWhiteSpace(AcmPilotCentro) &&
                 AcmPilotCentro == AcmPilotCentro.Trim() &&
                 (AcmPilotItem is null || !string.IsNullOrWhiteSpace(AcmPilotItem))));
    }
}
