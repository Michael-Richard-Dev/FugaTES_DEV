namespace FugaTES.Aplicacao.Consultas.ProtocolosGuardian;

/// <summary>
/// Marcadores de ausencia. A origem persistida nao guarda tudo o que a documentacao
/// descreve; o que falta aparece como ausente, nunca preenchido por suposicao.
/// </summary>
public static class ProtocoloPreCadastro
{
    public const string Desconhecido = "UNKNOWN";
    public const string NaoArmazenado = "ORIGINAL_NAO_ARMAZENADO";
    public const string Projecao = "PROJECAO_PERSISTIDA";
    public const string Etapa = "REST_PROTOCOLO_PRE_CADASTRO";
    public const string Rota = "/api/v1/Guardian/ProtocoloPreCadastro";

    public static string Texto(string? valor) => string.IsNullOrWhiteSpace(valor) ? Desconhecido : valor;
}

/// <summary>
/// Uma tentativa de pre-cadastro REST, montada so com o que esta duravelmente
/// registrado. Campos sem origem fisica ficam nulos e sao exibidos como UNKNOWN.
/// </summary>
public sealed record ProtocoloPreCadastroLinha
{
    public Guid IntegracaoId { get; init; }
    public string CorrelationId { get; init; } = "";
    public string? Contrato { get; init; }
    public string? Item { get; init; }
    public string? Referencia { get; init; }
    public string? Protocolo { get; init; }
    public string? TicketGuardian { get; init; }
    public DateTimeOffset? TentativaEm { get; init; }

    /// <summary>
    /// Status HTTP da chamada REST. Nenhuma coluna da origem atual o armazena, de modo
    /// que ele chega nulo; a tela declara UNKNOWN em vez de inferir pelo estado funcional.
    /// </summary>
    public int? StatusHttp { get; init; }

    public string? EstadoEmpresarial { get; init; }
    public string? EtapaFuncional { get; init; }
    public int Tentativas { get; init; }
    public string? UltimoErro { get; init; }
    public string? StatusCorrelacao { get; init; }
    public string EstadoMotor { get; init; } = "";
    public string Origem { get; init; } = Desconhecida;

    public const string Desconhecida = "ORIGEM_NAO_ATESTADA";
    public const string Simulada = "SIMULADA";
    public const string Real = "REAL";

    public bool ResultadoIndeterminado { get; init; }
    public bool FalhaNaoReprocessavel { get; init; }
    public bool FalhaReprocessavel { get; init; }
    public bool CorrelacaoAmbigua { get; init; }
    public bool ContratoConfirmado { get; init; }
    public bool ProcessamentoAtivo { get; init; }

    /// <summary>Projecao do payload enviado, quando reconstruivel a partir do contrato H4.</summary>
    public string? PayloadEnviadoJson { get; init; }

    /// <summary>
    /// Corpo HTTP original da resposta Guardian. A origem atual nao o persiste; quando
    /// nulo, a tela informa ORIGINAL_NAO_ARMAZENADO e nada e reconstruido no lugar.
    /// </summary>
    public string? RespostaJson { get; init; }

    public bool ProtocoloCriado => !string.IsNullOrWhiteSpace(Protocolo) || !string.IsNullOrWhiteSpace(TicketGuardian);
}

public sealed record FiltroProtocoloPreCadastro
{
    public string? Contrato { get; init; }
    public string? Referencia { get; init; }
    public string? Protocolo { get; init; }
    public string Situacao { get; init; } = Todas;

    public const string Todas = "TODAS";
    public const string Concluidos = "CONCLUIDOS";
    public const string Recuperaveis = "RECUPERAVEIS";
    public const string Bloqueados = "BLOQUEADOS";
    public const string Indeterminados = "INDETERMINADOS";

    public static readonly string[] Situacoes = [Todas, Concluidos, Recuperaveis, Bloqueados, Indeterminados];

    public FiltroProtocoloPreCadastro Normalizar()
    {
        static string? Limpo(string? valor)
        {
            var texto = valor?.Trim();
            if (string.IsNullOrEmpty(texto)) return null;
            if (texto.Length > 60 || texto.Any(char.IsControl)) throw new ArgumentException("Filtro invalido.");
            return texto;
        }
        if (!Situacoes.Contains(Situacao)) throw new ArgumentException("Filtro invalido.");
        return this with { Contrato = Limpo(Contrato), Referencia = Limpo(Referencia), Protocolo = Limpo(Protocolo) };
    }

    public bool Aceita(ProtocoloPreCadastroLinha linha)
    {
        static bool Contem(string? valor, string? filtro) => filtro is null ||
            (valor is not null && valor.Contains(filtro, StringComparison.OrdinalIgnoreCase));
        if (!Contem(linha.Contrato, Contrato) || !Contem(linha.Referencia, Referencia) ||
            !Contem(linha.Protocolo, Protocolo)) return false;
        return Situacao switch
        {
            Concluidos => linha.ProtocoloCriado,
            Indeterminados => linha.ResultadoIndeterminado,
            Recuperaveis => ReenvioProtocolo.Avaliar(linha).Recuperavel,
            Bloqueados => !linha.ProtocoloCriado && !ReenvioProtocolo.Avaliar(linha).Recuperavel,
            _ => true,
        };
    }
}

public interface IProtocolosPreCadastroReadRepository
{
    Task<IReadOnlyList<ProtocoloPreCadastroLinha>> ConsultarAsync(CancellationToken ct);
}

public sealed class ProtocolosPreCadastroConsulta(IProtocolosPreCadastroReadRepository repository)
{
    public async Task<IReadOnlyList<ProtocoloPreCadastroLinha>> ConsultarAsync(
        FiltroProtocoloPreCadastro filtro, CancellationToken ct)
    {
        var normalizado = filtro.Normalizar();
        ct.ThrowIfCancellationRequested();
        var linhas = await repository.ConsultarAsync(ct);
        return linhas.Where(normalizado.Aceita).ToArray();
    }
}
