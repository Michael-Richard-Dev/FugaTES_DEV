namespace FugaTES.Aplicacao.Consultas.ProtocolosGuardian;

/// <summary>
/// Resultado da avaliacao de elegibilidade do reenvio da etapa REST.
/// <para>
/// <see cref="Recuperavel"/> diz que a evidencia duravel aponta uma falha recuperavel.
/// <see cref="PodeExecutar"/> e sempre falso neste incremento: a execucao pertence ao
/// mecanismo de comandos autorizado, e esta tela nao realiza POST.
/// </para>
/// </summary>
public sealed record ReenvioProtocoloAvaliacao(bool Recuperavel, string Codigo, string Motivo)
{
    public bool PodeExecutar => false;

    public string Rotulo => Recuperavel ? "ELEGIVEL_SEM_EXECUCAO" : "BLOQUEADO";
}

/// <summary>
/// Elegibilidade do reenvio da etapa REST ProtocoloPreCadastro, decidida apenas por
/// evidencia duravel e com o mesmo vocabulario de bloqueio do mecanismo de comandos
/// autorizado (<c>GUARDIAN_COMMAND_*</c>). Nenhuma etapa SOAP e reiniciada e nenhum
/// fluxo SAP e repetido: a avaliacao cobre somente a chamada REST.
/// </summary>
public static class ReenvioProtocolo
{
    public const string ProtocoloJaCriado = "GUARDIAN_COMMAND_EXISTING_EXTERNAL_RESULT";
    public const string Rejeitado = "GUARDIAN_COMMAND_REJECTED";
    public const string Indeterminado = "GUARDIAN_COMMAND_OUTCOME_UNVERIFIED";
    public const string ProcessamentoAtivo = "GUARDIAN_COMMAND_INTEGRATION_NOT_READY";
    public const string Sintetico = "GUARDIAN_COMMAND_SCOPE_MISMATCH";
    public const string CorrelacaoInvalida = "GUARDIAN_COMMAND_H4_IDENTITY_INVALID";
    public const string ContratoNaoValidado = "GUARDIAN_COMMAND_CONFIRMED_CONTRACT_REQUIRED";
    public const string ExistenciaDesconhecida = "GUARDIAN_COMMAND_EXTERNAL_EXISTENCE_UNKNOWN";
    public const string PayloadIndisponivel = "GUARDIAN_COMMAND_H4_PAYLOAD_UNAVAILABLE";
    public const string FalhaNaoComprovada = "GUARDIAN_COMMAND_RECOVERABLE_FAILURE_REQUIRED";
    public const string ExecucaoDesabilitada = "GUARDIAN_COMMAND_EXECUTION_DISABLED";

    /// <summary>
    /// Avalia na ordem do risco: o que comprova sucesso externo bloqueia antes de
    /// qualquer outra coisa, e so entao se examina a falha.
    /// </summary>
    public static ReenvioProtocoloAvaliacao Avaliar(ProtocoloPreCadastroLinha linha)
    {
        static ReenvioProtocoloAvaliacao Bloqueio(string codigo, string motivo) => new(false, codigo, motivo);

        if (linha.ProtocoloCriado)
            return Bloqueio(ProtocoloJaCriado,
                "Ha protocolo ou ticket Guardian registrado para esta integracao. Reenviar duplicaria um pre-cadastro ja existente.");
        if (linha.Origem == ProtocoloPreCadastroLinha.Simulada)
            return Bloqueio(Sintetico, "Origem simulada. Protocolo ou contrato sintetico nao autoriza reenvio.");
        if (linha.ResultadoIndeterminado)
            return Bloqueio(Indeterminado,
                "Timeout ou resultado indeterminado: nao ha prova de que a chamada anterior falhou sem efeito externo.");
        if (linha.ProcessamentoAtivo)
            return Bloqueio(ProcessamentoAtivo, "Ha processamento ou tentativa concorrente em curso para esta integracao.");
        if (linha.FalhaNaoReprocessavel)
            return Bloqueio(Rejeitado,
                "Rejeicao definitiva registrada (por exemplo HTTP 400). Exige correcao comprovada antes de qualquer reenvio.");
        if (linha.CorrelacaoAmbigua || string.IsNullOrWhiteSpace(linha.CorrelationId))
            return Bloqueio(CorrelacaoInvalida, "Correlacao ausente ou divergente entre contrato, integracao e Guardian.");
        if (!linha.ContratoConfirmado)
            return Bloqueio(ContratoNaoValidado, "O contrato de origem nao esta confirmado; a etapa REST depende dele.");
        if (string.IsNullOrWhiteSpace(linha.PayloadEnviadoJson))
            return Bloqueio(PayloadIndisponivel, "Sem payload reconstruivel a partir do contrato H4 persistido.");
        if (!linha.FalhaReprocessavel)
            return Bloqueio(FalhaNaoComprovada,
                "Nenhuma falha recuperavel confirmada na evidencia duravel. O reenvio exige falha anterior comprovada.");
        // Ultimo portao: sem atestado de origem real, a existencia externa do pre-cadastro
        // e desconhecida e um reenvio pode duplicar o que o Guardian ja aceitou.
        if (linha.Origem != ProtocoloPreCadastroLinha.Real)
            return Bloqueio(ExistenciaDesconhecida,
                "A existencia externa do pre-cadastro nao esta atestada pela origem. Sem isso, reenviar pode duplicar.");
        return new(true, ExecucaoDesabilitada,
            "Falha recuperavel confirmada e identidade valida. A execucao pertence ao mecanismo de comandos autorizado "
            + "e permanece desabilitada neste incremento: nenhum POST e realizado por esta tela.");
    }

    /// <summary>Texto da confirmacao do operador: contrato, referencia, etapa, motivo e risco.</summary>
    public static string TextoConfirmacao(ProtocoloPreCadastroLinha linha, ReenvioProtocoloAvaliacao avaliacao)
        => $"""
            Contrato: {ProtocoloPreCadastro.Texto(linha.Contrato)}   Item: {ProtocoloPreCadastro.Texto(linha.Item)}
            Referencia OC: {ProtocoloPreCadastro.Texto(linha.Referencia)}
            Etapa: {ProtocoloPreCadastro.Etapa} ({ProtocoloPreCadastro.Rota})
            Motivo da falha: {ProtocoloPreCadastro.Texto(linha.UltimoErro)}
            Situacao: {avaliacao.Rotulo} — {avaliacao.Codigo}
            {avaliacao.Motivo}

            RISCO DE DUPLICIDADE: um pre-cadastro ja aceito pelo Guardian e invisivel aqui enquanto
            o protocolo nao for registrado. Confirmar nao reinicia a etapa SOAP nem repete o fluxo
            SAP; neste incremento nenhum POST externo e realizado.
            """;
}
