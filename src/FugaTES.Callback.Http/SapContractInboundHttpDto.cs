using System.Text.Json.Serialization;

namespace FugaTES.Callback.Http;

public sealed record SapContractInboundHttpDto(
    [property: JsonPropertyName("Contrato")]
    IReadOnlyCollection<SapContractInboundItemHttpDto?>? Contrato);

public sealed record SapContractInboundItemHttpDto(
    [property: JsonPropertyName("SistemaBalanca")] string? SistemaBalanca,
    [property: JsonPropertyName("NumeroContrato")] string? NumeroContrato,
    [property: JsonPropertyName("TipoContrato")] string? TipoContrato,
    [property: JsonPropertyName("DescricaoTipoContrato")] string? DescricaoTipoContrato,
    [property: JsonPropertyName("DataEmissao")] string? DataEmissao,
    [property: JsonPropertyName("DataEntregaInicio")] string? DataEntregaInicio,
    [property: JsonPropertyName("DataEntregaFim")] string? DataEntregaFim,
    [property: JsonPropertyName("DiasToler")] int? DiasToler,
    [property: JsonPropertyName("DataLimiteEntrega")] string? DataLimiteEntrega,
    [property: JsonPropertyName("StatusContrato")] string? StatusContrato,
    [property: JsonPropertyName("QuantidadeContrato")] decimal? QuantidadeContrato,
    [property: JsonPropertyName("QuantidadeSaldoDevolucao")] decimal? QuantidadeSaldoDevolucao,
    [property: JsonPropertyName("IdParceiro")] string? IdParceiro,
    [property: JsonPropertyName("Material")] string? Material,
    [property: JsonPropertyName("TipoPesagem")] string? TipoPesagem,
    [property: JsonPropertyName("TabelaQualidadeID")] string? TabelaQualidadeID,
    [property: JsonPropertyName("TabelaQualidadeVersao")] string? TabelaQualidadeVersao,
    [property: JsonPropertyName("TabelaQualidadeVersaoValidade")] string? TabelaQualidadeVersaoValidade,
    [property: JsonPropertyName("Observacao")] string? Observacao,
    [property: JsonPropertyName("ValorUnitario")] decimal? ValorUnitario,
    [property: JsonPropertyName("Centro")] string? Centro,
    [property: JsonPropertyName("OperacaoContrato")] string? OperacaoContrato,
    [property: JsonPropertyName("CentroOpcional")]
    IReadOnlyCollection<SapContractInboundCentroOpcionalHttpDto>? CentroOpcional);

public sealed record SapContractInboundCentroOpcionalHttpDto(
    [property: JsonPropertyName("Centro")] string? Centro);
