namespace FugaTES.Aplicacao.Modelos.Toledo;

public sealed record ContratoToledo(
    string? SistemaBalanca,
    string? NumeroContrato,
    string? TipoContrato,
    string? DescricaoTipoContrato,
    string? DataEmissao,
    string? DataEntregaInicio,
    string? DataEntregaFim,
    string? DiasToler,
    string? DataLimiteEntrega,
    string? StatusContrato,
    string? QuantidadeContrato,
    string? QuantidadeSaldoDevolucao,
    string? IdParceiro,
    string? Material,
    string? TipoPesagem,
    string? TabelaQualidadeID,
    string? TabelaQualidadeVersao,
    string? TabelaQualidadeVersaoValidade,
    string? Observacao,
    string? ValorUnitario,
    string? Centro,
    string? OperacaoContrato,
    IReadOnlyCollection<CentroOpcionalContratoToledo>? CentroOpcional);

public sealed record CentroOpcionalContratoToledo(string? Centro);

public sealed record ContratoToledoEnviado(ContratoToledo Contrato);
