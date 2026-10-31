using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.Monitor;

namespace FugaTES.WinForms.Monitor;

public sealed record MonitorAcmEtapa(string Etapa, string Estado, string Evidencia, DateTimeOffset? DataHora = null);

public static class MonitorFluxoAcm
{
    public static IReadOnlyList<MonitorAcmEtapa> Criar(MonitorIntegracao linha, MonitorDetalhe detalhe, MonitorAcmDados? dados = null)
    {
        var a = dados ?? new();
        var callback = detalhe.Guardian.LastOrDefault();
        var motor = linha.EstadoMotor switch
        {
            "EM_PROCESSAMENTO" => "EM_PROCESSAMENTO",
            "AGUARDANDO_ACAO_MANUAL" or "RESULTADO_INDETERMINADO" or "CANCELADA" => "BLOQUEADO",
            "FALHA_DEFINITIVA" or "AGUARDANDO_REPROCESSAMENTO" => "ERRO",
            "CONCLUIDA" => "CONCLUIDO",
            _ => "PENDENTE"
        };
        if (linha.StatusFuncional is "FALHA_NAO_REPROCESSAVEL" or "FALHA_REPROCESSAVEL") motor = "ERRO";
        if (linha.StatusFuncional == "RESULTADO_INDETERMINADO") motor = "BLOQUEADO";
        if (linha.StatusFuncional == "AGUARDANDO_DECISAO_FUNCIONAL" || !string.IsNullOrWhiteSpace(linha.DecisaoPendente)) motor = "BLOQUEADO";
        MonitorAcmEtapa Etapa(string nome, bool disponivel, bool comprovado, string evidencia, DateTimeOffset? quando = null)
            => new(nome, !disponivel ? "INDISPONIVEL" : comprovado ? "CONCLUIDO" : "PENDENTE", evidencia, quando);
        var steps = new List<MonitorAcmEtapa>
        {
            Etapa("Contrato recebido SAP", a.ContratosDisponiveis, a.Contrato is not null, a.Contrato ?? "H4 sem vinculo disponivel", a.RecebidoEm),
            Etapa("Cadastro contrato Guardian", a.ContratosDisponiveis, a.CodigoGuardian is not null, a.CodigoGuardian is null ? "Codigo confirmado ainda nao persistido" : $"Codigo: {a.CodigoGuardian} | Sequencial contrato: {a.SequencialContrato ?? "-"}"),
            linha.EtapaFuncional == "R165Y_REST_REJECTED" && linha.StatusFuncional == "FALHA_NAO_REPROCESSAVEL"
                ? new("Protocolo Guardian", "ERRO", MonitorOperacionalConsulta.TextoSeguro(linha.UltimoErro) ?? "Erro detalhado nao persistido", linha.AtualizadoEm)
                : Etapa("Protocolo Guardian", a.CorrelacaoDisponivel || !string.IsNullOrWhiteSpace(a.Protocolo), !string.IsNullOrWhiteSpace(a.Protocolo), a.Protocolo is null ? "Protocolo ainda nao persistido" : (linha.Fluxo?.Contains("GUARDIAN SIMULADO", StringComparison.Ordinal)==true ? "SIMULADO: "+a.Protocolo : a.Protocolo)),
            Etapa("Callback pesagem", a.CorrelacaoDisponivel, callback is not null || a.Sequencial is not null, callback is null ? "Sem evento vinculado nesta consulta" : "Evento persistido; nao autoriza POST SAP", callback?.RecebidoEm),
            new("Identidade do tiquete", !a.IdentidadesDisponiveis ? "INDISPONIVEL" : a.CallbackIdOriginal is null && a.TicketCodigo is null ? "PENDENTE" : "BLOQUEADO", "ID / Ticket.Codigo: CONFLICT_PENDING_EVIDENCE"),
            new("Consulta SOAP", "INDISPONIVEL", "Sem recibo independente de consulta SOAP real no contrato de leitura"),
            Etapa("Evidencia enriquecida persistida", a.EvidenciaDisponivel, a.EvidenciaCompleta, a.EvidenciaCompleta ? "Snapshot + hash + data persistidos; corpo privado" : "R165A ou evidencia ainda indisponivel", a.EvidenciaRecebidaEm),
            new("Devolutiva SAP (tecnica)", linha.Operacao == "SAP_RECEBIMENTO_GUARDIAN_PESAGEM" ? motor : "INDISPONIVEL", linha.Operacao == "SAP_RECEBIMENTO_GUARDIAN_PESAGEM" ? "Estado INC005 da devolutiva; nao confirma negocio SAP" : "Selecione a integracao de devolutiva SAP", linha.AtualizadoEm),
            new("Confirmacao empresarial SAP", "PENDENTE", "Exige evidencia empresarial SAP independente; HTTP 2xx nao basta"),
            new("Processamento / intervencao", motor, MonitorOperacionalConsulta.TextoSeguro(linha.UltimoErro) ?? (motor is "ERRO" or "BLOQUEADO" ? "Erro detalhado nao persistido; nenhuma retentativa por esta tela" : "Estado tecnico INC005; somente leitura"), linha.AtualizadoEm)
        };
        if (a.CorrelacaoAmbigua)
            for (var i = 0; i < steps.Count - 1; i++) steps[i] = steps[i] with { Estado = "BLOQUEADO", Evidencia = "Vinculo ambiguo; nenhuma identidade inferida" };
        return steps;
    }
}
