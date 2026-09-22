using FugaTES.Aplicacao.Modelos.Toledo;

namespace FugaTES.Aplicacao.Resultados.Toledo;

public enum StatusPreCadastroToledo
{
    Sucesso,
    FalhaValidacao,
    FalhaContrato,
    FalhaTecnica
}

public sealed class ResultadoPreCadastroToledo
{
    private ResultadoPreCadastroToledo(StatusPreCadastroToledo status, PreCadastroToledo? dados)
    {
        Status = status;
        Dados = dados;
    }

    public StatusPreCadastroToledo Status { get; }

    public PreCadastroToledo? Dados { get; }

    public static ResultadoPreCadastroToledo Sucesso(PreCadastroToledo dados)
    {
        ArgumentNullException.ThrowIfNull(dados);
        return new ResultadoPreCadastroToledo(StatusPreCadastroToledo.Sucesso, dados);
    }

    public static ResultadoPreCadastroToledo Falha(StatusPreCadastroToledo status)
    {
        if (status == StatusPreCadastroToledo.Sucesso)
        {
            throw new ArgumentException("Status de falha nao pode ser Sucesso.", nameof(status));
        }

        return new ResultadoPreCadastroToledo(status, default);
    }
}
