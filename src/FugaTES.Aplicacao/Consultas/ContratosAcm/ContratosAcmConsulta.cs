namespace FugaTES.Aplicacao.Consultas.ContratosAcm;

public sealed record ContratoAcmLeitura(string? Centro, string NumeroContrato, long Versao,
    DateTime RecebidoEm, string? StatusContrato, string EstadoIntegracao,
    Guid IntegracaoId, string CorrelationId, bool VersaoMaisRecente)
{
    /// <summary>
    /// Item SAP do contrato. A identidade empresarial e NumeroContrato + Item; as tabelas
    /// sao chaveadas apenas por numero_contrato e o item vive dentro do payload, de modo
    /// que varios itens do mesmo contrato chegam como versoes distintas, cada uma com a
    /// sua integracao. Propriedade init para preservar o construtor posicional em uso.
    /// </summary>
    public string? Item { get; init; }

    /// <summary>
    /// Projecao persistida do contrato (contrato_sap_toledo_versao.payload_contrato),
    /// carregada junto da linha para que a acao JSON nao dependa de consulta ao vivo.
    /// Nulo quando a linha nao tem payload armazenado.
    /// </summary>
    public string? PayloadContratoJson { get; init; }
    public string FonteDados {get;init;}="PROJECAO_PERSISTIDA";
}

public interface IContratosAcmReadRepository
{
    Task<IReadOnlyList<ContratoAcmLeitura>> ConsultarAsync(string centro, CancellationToken ct);
}

public sealed class ContratosAcmConsulta(IContratosAcmReadRepository repository)
{
    public const string CentroInicial = "3011";

    /// <summary>
    /// Seletor que dispensa o filtro de centro e devolve todos os centros autorizados pela
    /// fonte de leitura. Existe para que um contrato recebido do SAP nunca fique invisivel
    /// apenas por pertencer a um centro diferente do piloto. Nao amplia privilegio: a fonte
    /// continua sendo a mesma leitura autorizada, somente leitura, com o mesmo limite.
    /// </summary>
    public const string TodosOsCentros = "TODOS";

    public static bool EhTodosOsCentros(string? centro)
        => string.Equals(centro?.Trim(), TodosOsCentros, StringComparison.OrdinalIgnoreCase);

    public Task<IReadOnlyList<ContratoAcmLeitura>> ConsultarAsync(string centro, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(centro)) throw new ArgumentException("Informe o centro.", nameof(centro));
        ct.ThrowIfCancellationRequested();
        // O sentinela e normalizado aqui para que todas as implementacoes do repositorio
        // recebam exatamente o mesmo valor, sem depender da caixa digitada na tela.
        return repository.ConsultarAsync(EhTodosOsCentros(centro) ? TodosOsCentros : centro.Trim(), ct);
    }
}
