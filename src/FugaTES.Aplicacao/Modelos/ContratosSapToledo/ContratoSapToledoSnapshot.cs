namespace FugaTES.Aplicacao.Modelos.ContratosSapToledo;

public sealed record ContratoSapToledoSnapshot(
    string? SistemaBalanca, string NumeroContrato, string? TipoContrato, string? DescricaoTipoContrato,
    string? DataEmissao, string? DataEntregaInicio, string? DataEntregaFim, int? DiasToler,
    string? DataLimiteEntrega, string? StatusContrato, decimal? QuantidadeContrato,
    decimal? QuantidadeSaldoDevolucao, string? IdParceiro, string? Material, string? TipoPesagem,
    string? TabelaQualidadeID, string? TabelaQualidadeVersao, string? TabelaQualidadeVersaoValidade,
    string? Observacao, decimal? ValorUnitario, string? Centro, string? OperacaoContrato,
    IReadOnlyCollection<string?> CentroOpcional)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? Item { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? UnidCont { get; init; }
}

public sealed record ContratoSapToledoCanonico(ContratoSapToledoSnapshot Snapshot, string PayloadCanonicoJson, string FingerprintSha256);
