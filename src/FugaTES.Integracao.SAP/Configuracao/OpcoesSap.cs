namespace FugaTES.Integracao.SAP.Configuracao;

public sealed class OpcoesSap
{
    public Uri? BaseUrl { get; init; }

    public string? SapClient { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
