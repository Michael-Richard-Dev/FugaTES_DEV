namespace FugaTES.Aplicacao.Resultados.Toledo;

public enum StatusOperacaoToledo
{
    Sucesso,
    FalhaContrato,
    FalhaTecnica
}

public sealed class ResultadoOperacaoToledo<T>
{
    private ResultadoOperacaoToledo(StatusOperacaoToledo status, T? dados)
    {
        Status = status;
        Dados = dados;
    }

    public StatusOperacaoToledo Status { get; }

    public T? Dados { get; }

    public static ResultadoOperacaoToledo<T> Sucesso(T dados)
    {
        ArgumentNullException.ThrowIfNull(dados);
        return new ResultadoOperacaoToledo<T>(StatusOperacaoToledo.Sucesso, dados);
    }

    public static ResultadoOperacaoToledo<T> Falha(StatusOperacaoToledo status)
    {
        if (status == StatusOperacaoToledo.Sucesso)
        {
            throw new ArgumentException("Status de falha nao pode ser Sucesso.", nameof(status));
        }

        return new ResultadoOperacaoToledo<T>(status, default);
    }
}