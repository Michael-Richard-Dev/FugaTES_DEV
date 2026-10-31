namespace FugaTES.Aplicacao.Resultados.SAP;

public enum StatusEscritaSap
{
    Sucesso,
    NaoEncontrado,
    Conflito,          // HTTP 409
    PreCondicaoFalhou, // HTTP 412 (If-Match/ETag)
    FalhaConfiguracao,
    FalhaAutenticacao,
    FalhaAutorizacao,
    FalhaTecnica,
    FalhaContrato
}

public sealed class ResultadoEscritaSap<T>
{
    private ResultadoEscritaSap(StatusEscritaSap status, T? dados)
    {
        Status = status;
        Dados = dados;
    }

    public StatusEscritaSap Status { get; }

    public T? Dados { get; }

    public static ResultadoEscritaSap<T> Sucesso(T dados)
    {
        ArgumentNullException.ThrowIfNull(dados);
        return new ResultadoEscritaSap<T>(StatusEscritaSap.Sucesso, dados);
    }

    public static ResultadoEscritaSap<T> Falha(StatusEscritaSap status)
    {
        if (status == StatusEscritaSap.Sucesso)
        {
            throw new ArgumentException("Status de falha nao pode ser Sucesso.", nameof(status));
        }

        return new ResultadoEscritaSap<T>(status, default);
    }
}
