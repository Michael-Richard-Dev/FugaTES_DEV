namespace FugaTES.Aplicacao.Modelos.MotorIntegracao;

// Resultado da validacao estrutural pre-claim (S1 aditivo).
// Deliberadamente separado de ResultadoProcessamentoExterno: validacao nao e processamento.
public sealed record ResultadoValidacaoIntegracao
{
    private ResultadoValidacaoIntegracao(bool aprovada, string? diagnosticoSanitizado)
    {
        Aprovada = aprovada;
        DiagnosticoSanitizado = diagnosticoSanitizado;
    }

    public bool Aprovada { get; }

    public string? DiagnosticoSanitizado { get; }

    public static ResultadoValidacaoIntegracao Aprovar()
    {
        return new ResultadoValidacaoIntegracao(true, null);
    }

    public static ResultadoValidacaoIntegracao Reprovar(string diagnosticoSanitizado)
    {
        if (string.IsNullOrWhiteSpace(diagnosticoSanitizado))
        {
            throw new ArgumentException("Diagnostico sanitizado e obrigatorio para reprovacao.", nameof(diagnosticoSanitizado));
        }

        return new ResultadoValidacaoIntegracao(false, diagnosticoSanitizado);
    }
}
