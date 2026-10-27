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
}

public interface IContratosAcmReadRepository
{
    Task<IReadOnlyList<ContratoAcmLeitura>> ConsultarAsync(string centro, CancellationToken ct);
}

public sealed class ContratosAcmConsulta(IContratosAcmReadRepository repository)
{
    public const string CentroInicial = "3011";
    public Task<IReadOnlyList<ContratoAcmLeitura>> ConsultarAsync(string centro, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(centro)) throw new ArgumentException("Informe o centro.", nameof(centro));
        ct.ThrowIfCancellationRequested();
        return repository.ConsultarAsync(centro.Trim(), ct);
    }
}
