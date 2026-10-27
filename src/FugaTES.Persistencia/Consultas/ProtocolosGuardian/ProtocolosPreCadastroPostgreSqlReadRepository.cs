using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Aplicacao.Consultas.ProtocolosGuardian;
using FugaTES.Persistencia.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.UnifiedDev;

namespace FugaTES.Persistencia.Consultas.ProtocolosGuardian;

/// <summary>
/// Monta as linhas do monitor de pre-cadastro a partir da evidencia ja persistida,
/// reutilizando o repositorio de monitor (contratos H4, journal, tentativas e
/// correlacoes). Nenhuma consulta nova de escrita e nenhuma tabela nova: so leitura,
/// dentro das transacoes read-only que o repositorio de monitor ja impoe.
/// </summary>
public sealed class ProtocolosPreCadastroPostgreSqlReadRepository(MonitorPostgreSqlReadRepository monitor)
    : IProtocolosPreCadastroReadRepository
{
    /// <summary>Operacao do contrato SAP cuja etapa REST e o ProtocoloPreCadastro.</summary>
    public const string Operacao = "SAP_CONTRATO_TOLEDO";

    public async Task<IReadOnlyList<ProtocoloPreCadastroLinha>> ConsultarAsync(CancellationToken ct)
    {
        var snapshot = await new MonitorOperacionalConsulta(monitor).ConsultarAsync(new(Operacao: Operacao), ct);
        var linhas = new List<ProtocoloPreCadastroLinha>(snapshot.Integracoes.Count);
        foreach (var integracao in snapshot.Integracoes)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(integracao.Operacao, Operacao, StringComparison.Ordinal)) continue;
            linhas.Add(Montar(integracao, await DetalharAsync(integracao.IntegracaoId, ct)));
        }
        return linhas;
    }

    private async Task<MonitorAcmDetalhe?> DetalharAsync(Guid id, CancellationToken ct)
    {
        // Uma integracao sem detalhe legivel continua listada, com os campos ausentes
        // declarados; esconder a linha seria esconder o proprio problema.
        try { return await monitor.DetalharAcmAsync(id, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    public static ProtocoloPreCadastroLinha Montar(MonitorIntegracao integracao, MonitorAcmDetalhe? detalhe)
    {
        var acm = detalhe?.Acm;
        var tentativas = detalhe?.Detalhe.Tentativas ?? [];
        var ultima = tentativas.Count == 0 ? null : tentativas[^1];
        var origem = DevReadScope.Classify(integracao.IntegracaoId, acm?.Contrato ?? integracao.Documento);
        return new ProtocoloPreCadastroLinha
        {
            IntegracaoId = integracao.IntegracaoId,
            CorrelationId = integracao.CorrelationId,
            Contrato = acm?.Contrato ?? integracao.Documento,
            // O item vive dentro do payload do contrato e nao e projetado pelo monitor.
            Item = null,
            Referencia = acm?.Referencia,
            Protocolo = integracao.ProtocoloGuardian ?? acm?.Protocolo,
            TicketGuardian = integracao.TicketGuardian ?? integracao.NumeroTicketGuardian,
            TentativaEm = ultima?.EnvioIniciadoEm ?? ultima?.CriadaEm,
            // Nenhuma coluna da origem guarda o status HTTP da chamada REST.
            StatusHttp = null,
            EstadoEmpresarial = integracao.StatusFuncional,
            EtapaFuncional = integracao.EtapaFuncional,
            Tentativas = tentativas.Count,
            UltimoErro = integracao.UltimoErro,
            StatusCorrelacao = integracao.EstadoCorrelacao,
            EstadoMotor = integracao.EstadoMotor,
            Origem = origem == "SIMULACAO_CONFIRMADA"
                ? ProtocoloPreCadastroLinha.Simulada
                : ProtocoloPreCadastroLinha.Desconhecida,
            ResultadoIndeterminado = integracao.EstadoMotor == "RESULTADO_INDETERMINADO"
                || integracao.StatusFuncional == "RESULTADO_INDETERMINADO",
            FalhaNaoReprocessavel = integracao.EstadoMotor == "FALHA_DEFINITIVA"
                || integracao.StatusFuncional == "FALHA_NAO_REPROCESSAVEL",
            FalhaReprocessavel = integracao.EstadoMotor == "AGUARDANDO_REPROCESSAMENTO"
                || integracao.StatusFuncional == "FALHA_REPROCESSAVEL",
            ProcessamentoAtivo = integracao.EstadoMotor is "EM_PROCESSAMENTO" or "AGUARDANDO_ACAO_MANUAL",
            CorrelacaoAmbigua = acm?.CorrelacaoAmbigua ?? true,
            ContratoConfirmado = !string.IsNullOrWhiteSpace(acm?.CodigoGuardian),
            // O corpo HTTP original da resposta nao e persistido em lugar nenhum.
            RespostaJson = null,
        };
    }
}
