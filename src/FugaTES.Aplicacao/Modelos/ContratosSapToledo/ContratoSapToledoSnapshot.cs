namespace FugaTES.Aplicacao.Modelos.ContratosSapToledo;

public sealed record ContratoSapToledoSnapshot(
    string? SistemaBalanca, string NumeroContrato, string? TipoContrato, string? DescricaoTipoContrato,
    string? DataEmissao, string? DataEntregaInicio, string? DataEntregaFim, int? DiasToler,
    string? DataLimiteEntrega, string? StatusContrato, decimal? QuantidadeContrato,
    decimal? QuantidadeSaldoDevolucao, string? IdParceiro, string? Material, string? TipoPesagem,
    string? TabelaQualidadeID, string? TabelaQualidadeVersao, string? TabelaQualidadeVersaoValidade,
    string? Observacao, decimal? ValorUnitario, string? Centro, string? OperacaoContrato,
    IReadOnlyCollection<string?> CentroOpcional);

public sealed record ContratoSapToledoCanonico(ContratoSapToledoSnapshot Snapshot, string PayloadCanonicoJson, string FingerprintSha256);
