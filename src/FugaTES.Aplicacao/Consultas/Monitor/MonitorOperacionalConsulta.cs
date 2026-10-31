using System.Text.RegularExpressions;

namespace FugaTES.Aplicacao.Consultas.Monitor;

public sealed record MonitorFiltro(string? Operacao = null, string? Estado = null, string? CorrelationId = null,
    string? Documento = null, DateTimeOffset? De = null, DateTimeOffset? Ate = null, string Grupo = "TODOS");

public sealed record MonitorIntegracao
{
    public Guid IntegracaoId { get; init; }
    public DateTimeOffset CriadoEm { get; init; }
    public string Operacao { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public string EstadoMotor { get; init; } = "";
    public Guid? Tentativa { get; init; }
    public string? Documento { get; init; }
    public string? ProtocoloGuardian { get; init; }
    public string? TicketGuardian { get; init; }
    public string? EstadoCorrelacao { get; init; }
    public string? UltimoResultado { get; init; }
    public string? UltimoErro { get; init; }
    public DateTimeOffset AtualizadoEm { get; init; }
    public string? Fluxo { get; init; }
    public string? StatusFuncional { get; init; }
    public string? EtapaFuncional { get; init; }
    public string? DecisaoPendente { get; init; }
    public string? SalesOrder { get; init; }
    public string? DeliveryDocument { get; init; }
    public string? PurchaseOrder { get; init; }
    public string? MaterialDocument { get; init; }
    public string? MaterialDocumentYear { get; init; }
    public string? NumeroTicketGuardian { get; init; }
}

public sealed record MonitorTentativa(Guid Id, long FencingToken, string TipoDisparo, string FaseEnvio,
    string Desfecho, DateTimeOffset CriadaEm, DateTimeOffset? EnvioIniciadoEm, DateTimeOffset? ResultadoRecebidoEm);
public sealed record MonitorAuditoria(string Evento, DateTimeOffset CriadaEm, long? FencingToken, Guid? TentativaId);
public sealed record MonitorEventoGuardian(int EstadoCodigo, long Sequencial, decimal? PesoBruto,
    decimal? Tara, decimal? PesoLiquido, DateTimeOffset RecebidoEm);
public sealed record MonitorDetalhe(IReadOnlyList<MonitorTentativa> Tentativas,
    IReadOnlyList<MonitorAuditoria> Auditoria, IReadOnlyList<MonitorEventoGuardian> Guardian);
public sealed record MonitorSnapshot(bool ProjecaoDisponivel, bool EventosDisponiveis,
    IReadOnlyList<MonitorIntegracao> Integracoes);

public interface IMonitorReadRepository
{
    Task<MonitorSnapshot> ConsultarAsync(MonitorFiltro filtro, CancellationToken ct);
    Task<MonitorDetalhe> DetalharAsync(Guid integracaoId, CancellationToken ct);
}

public sealed class MonitorOperacionalConsulta(IMonitorReadRepository repository)
{
    public static string OperacaoCorrelacionadaPorDocumento => FugaTES.Aplicacao.CasosDeUso.Recebimento.GuardianPreCadastroMotorWork.Operacao;

    public async Task<MonitorSnapshot> ConsultarAsync(MonitorFiltro filtro, CancellationToken ct)
    {
        if (filtro.De > filtro.Ate || filtro.Grupo is not ("TODOS" or "PENDENTES" or "FALHAS" or "INDETERMINADOS"))
            throw new ArgumentException("Filtro invalido.");
        var snapshot = await repository.ConsultarAsync(filtro, ct);
        return snapshot with { Integracoes = snapshot.Integracoes.Select(Sanitizar).ToArray() };
    }

    public async Task<MonitorDetalhe> DetalharAsync(Guid id, CancellationToken ct)
    {
        var detalhe = await repository.DetalharAsync(id, ct);
        return detalhe with { Auditoria = detalhe.Auditoria.Select(a => a with { Evento = TextoSeguro(a.Evento)! }).ToArray() };
    }

    public static string? TextoSeguro(string? valor)
    {
        if (valor is null) return null;
        if (valor.Length > 160 || Regex.IsMatch(valor, @"(?i)authorization|bearer|basic\s|password|senha|token|assinatura|cookie|secret|host=|username=|stacktrace|\r|\n"))
            return "[CONTEUDO OMITIDO]";
        return valor;
    }

    private static MonitorIntegracao Sanitizar(MonitorIntegracao i) => i with
    {
        Operacao = TextoSeguro(i.Operacao)!, CorrelationId = TextoSeguro(i.CorrelationId)!,
        Documento = TextoSeguro(i.Documento), ProtocoloGuardian = TextoSeguro(i.ProtocoloGuardian),
        TicketGuardian = TextoSeguro(i.TicketGuardian), UltimoResultado = TextoSeguro(i.UltimoResultado),
        UltimoErro = TextoSeguro(i.UltimoErro), Fluxo = TextoSeguro(i.Fluxo), StatusFuncional = TextoSeguro(i.StatusFuncional),
        EtapaFuncional = TextoSeguro(i.EtapaFuncional), DecisaoPendente = TextoSeguro(i.DecisaoPendente),
        SalesOrder = TextoSeguro(i.SalesOrder), DeliveryDocument = TextoSeguro(i.DeliveryDocument),
        PurchaseOrder = TextoSeguro(i.PurchaseOrder), MaterialDocument = TextoSeguro(i.MaterialDocument),
        MaterialDocumentYear = TextoSeguro(i.MaterialDocumentYear), NumeroTicketGuardian = TextoSeguro(i.NumeroTicketGuardian)
    };

    public static IReadOnlyDictionary<string, int> Contadores(IEnumerable<MonitorIntegracao> linhas)
    {
        var rows = linhas.ToArray();
        return new Dictionary<string, int>
        {
            ["Em processamento"] = rows.Count(i => i.EstadoMotor == "EM_PROCESSAMENTO"),
            ["Aguardando acao"] = rows.Count(i => i.EstadoMotor == "AGUARDANDO_ACAO_MANUAL" || i.StatusFuncional == "AGUARDANDO_DECISAO_FUNCIONAL"),
            ["Aguardando callback"] = rows.Count(i => i.StatusFuncional == "AGUARDANDO_CALLBACK" || i.EstadoMotor == "CONCLUIDA" && i.EstadoCorrelacao == "AGUARDANDO_TICKET"),
            ["Concluidos"] = rows.Count(i => i.EstadoMotor == "CONCLUIDA" && i.StatusFuncional != "AGUARDANDO_CALLBACK" && i.EstadoCorrelacao != "AGUARDANDO_TICKET"),
            ["Falhas reprocessaveis"] = rows.Count(i => i.EstadoMotor == "AGUARDANDO_REPROCESSAMENTO"),
            ["Falhas definitivas"] = rows.Count(i => i.EstadoMotor == "FALHA_DEFINITIVA"),
            ["Indeterminados"] = rows.Count(i => i.EstadoMotor == "RESULTADO_INDETERMINADO")
        };
    }
}
