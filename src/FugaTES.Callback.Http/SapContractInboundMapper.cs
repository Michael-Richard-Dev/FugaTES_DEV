using System.Globalization;
using FugaTES.Aplicacao.Modelos.Toledo;

namespace FugaTES.Callback.Http;

public static class SapContractInboundMapper
{
    public static IReadOnlyList<ContratoToledo> Mapear(SapContractInboundHttpDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (dto.Contrato is null || dto.Contrato.Count == 0)
            throw new ArgumentException("Contrato collection must contain at least one item.", nameof(dto));
        if (dto.Contrato.Any(item => item is null))
            throw new ArgumentException("Contrato collection cannot contain null items.", nameof(dto));

        return dto.Contrato.Select(item => MapearItem(item!)).ToArray();
    }

    private static ContratoToledo MapearItem(SapContractInboundItemHttpDto item) =>
        new(
            item.SistemaBalanca,
            item.NumeroContrato,
            item.TipoContrato,
            item.DescricaoTipoContrato,
            item.DataEmissao,
            item.DataEntregaInicio,
            item.DataEntregaFim,
            Invariante(item.DiasToler),
            item.DataLimiteEntrega,
            item.StatusContrato,
            Invariante(item.QuantidadeContrato),
            Invariante(item.QuantidadeSaldoDevolucao),
            item.IdParceiro,
            item.Material,
            item.TipoPesagem,
            item.TabelaQualidadeID,
            item.TabelaQualidadeVersao,
            item.TabelaQualidadeVersaoValidade,
            item.Observacao,
            Invariante(item.ValorUnitario),
            item.Centro,
            item.OperacaoContrato,
            item.CentroOpcional?.Select(centro => new CentroOpcionalContratoToledo(centro.Centro)).ToArray());

    private static string? Invariante(int? valor) =>
        valor?.ToString(CultureInfo.InvariantCulture);

    private static string? Invariante(decimal? valor) =>
        valor?.ToString("G29", CultureInfo.InvariantCulture);
}
