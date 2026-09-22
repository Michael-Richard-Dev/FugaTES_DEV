namespace FugaTES.Aplicacao.Resultados.SAP;

public enum StatusConsultaSap
{
    Sucesso,
    NaoEncontrado,
    CategoriaNaoCorrespondente,
    FalhaConfiguracao,
    FalhaAutenticacao,
    FalhaAutorizacao,
    FalhaTecnica,
    FalhaContrato
}

public sealed class ResultadoConsultaSap<T>
{
    private ResultadoConsultaSap(StatusConsultaSap status, T? dados)
    {
        Status = status;
        Dados = dados;
    }

    public StatusConsultaSap Status { get; }

    public T? Dados { get; }

    public static ResultadoConsultaSap<T> Sucesso(T dados)
    {
        ArgumentNullException.ThrowIfNull(dados);
        return new ResultadoConsultaSap<T>(StatusConsultaSap.Sucesso, dados);
    }

    public static ResultadoConsultaSap<T> Falha(StatusConsultaSap status)
    {
        if (status == StatusConsultaSap.Sucesso)
        {
            throw new ArgumentException("Status de falha não pode ser Sucesso.", nameof(status));
        }

        return new ResultadoConsultaSap<T>(status, default);
    }
}
