using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Modelos.Toledo;

namespace FugaTES.Integracao.Toledo.Dtos.Contrato;

internal sealed record ContratoToledoRequestDto(
    [property: JsonPropertyName("Contrato")] IReadOnlyCollection<ItemContratoToledoDto> Contrato)
{
    private static readonly JsonSerializerOptions SerializacaoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ContratoToledoRequestDto Criar(ContratoToledo contrato)
    {
        ArgumentNullException.ThrowIfNull(contrato);
        return Criar([contrato]);
    }

    public static ContratoToledoRequestDto Criar(IReadOnlyCollection<ContratoToledo> contratos)
    {
        ArgumentNullException.ThrowIfNull(contratos);
        return new ContratoToledoRequestDto(contratos.Select(ItemContratoToledoDto.Criar).ToArray());
    }

    public string Serializar()
    {
        return JsonSerializer.Serialize(this, SerializacaoJsonOptions);
    }
}

internal sealed record ItemContratoToledoDto(
    [property: JsonPropertyName("SistemaBalanca")] string? SistemaBalanca,
    [property: JsonPropertyName("NumeroContrato")] JsonElement? NumeroContrato,
    [property: JsonPropertyName("TipoContrato")] string? TipoContrato,
    [property: JsonPropertyName("DescricaoTipoContrato")] string? DescricaoTipoContrato,
    [property: JsonPropertyName("DataEmissao")] string? DataEmissao,
    [property: JsonPropertyName("DataEntregaInicio")] string? DataEntregaInicio,
    [property: JsonPropertyName("DataEntregaFim")] string? DataEntregaFim,
    [property: JsonPropertyName("DiasToler")] JsonElement? DiasToler,
    [property: JsonPropertyName("DataLimiteEntrega")] string? DataLimiteEntrega,
    [property: JsonPropertyName("StatusContrato")] string? StatusContrato,
    [property: JsonPropertyName("QuantidadeContrato")] string? QuantidadeContrato,
    [property: JsonPropertyName("QuantidadeSaldoDevolucao")] string? QuantidadeSaldoDevolucao,
    [property: JsonPropertyName("IdParceiro")] JsonElement? IdParceiro,
    [property: JsonPropertyName("Material")] JsonElement? Material,
    [property: JsonPropertyName("TipoPesagem")] string? TipoPesagem,
    [property: JsonPropertyName("TabelaQualidadeID")] JsonElement? TabelaQualidadeID,
    [property: JsonPropertyName("TabelaQualidadeVersao")] JsonElement? TabelaQualidadeVersao,
    [property: JsonPropertyName("TabelaQualidadeVersaoValidade")] JsonElement? TabelaQualidadeVersaoValidade,
    [property: JsonPropertyName("Observacao")] string? Observacao,
    [property: JsonPropertyName("ValorUnitario")] string? ValorUnitario,
    [property: JsonPropertyName("Centro")] string? Centro,
    [property: JsonPropertyName("OperacaoContrato")] JsonElement? OperacaoContrato,
    [property: JsonPropertyName("CentroOpcional")] IReadOnlyCollection<CentroOpcionalContratoToledoDto>? CentroOpcional)
{
    public static ItemContratoToledoDto Criar(ContratoToledo contrato)
    {
        var centroOpcional = contrato.CentroOpcional is null
            ? null
            : contrato.CentroOpcional.Select(centro => new CentroOpcionalContratoToledoDto(centro.Centro)).ToArray();

        return new ItemContratoToledoDto(
            contrato.SistemaBalanca,
            Numero(contrato.NumeroContrato),
            contrato.TipoContrato,
            contrato.DescricaoTipoContrato,
            contrato.DataEmissao,
            contrato.DataEntregaInicio,
            contrato.DataEntregaFim,
            Numero(contrato.DiasToler),
            contrato.DataLimiteEntrega,
            contrato.StatusContrato,
            contrato.QuantidadeContrato,
            contrato.QuantidadeSaldoDevolucao,
            Numero(contrato.IdParceiro),
            Numero(contrato.Material),
            contrato.TipoPesagem,
            Numero(contrato.TabelaQualidadeID),
            Numero(contrato.TabelaQualidadeVersao),
            Numero(contrato.TabelaQualidadeVersaoValidade),
            contrato.Observacao,
            contrato.ValorUnitario,
            contrato.Centro,
            Numero(contrato.OperacaoContrato),
            centroOpcional);
    }

    private static JsonElement? Numero(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return null;
        }

        using var documento = JsonDocument.Parse(valor);
        return documento.RootElement.Clone();
    }
}

internal sealed record CentroOpcionalContratoToledoDto(
    [property: JsonPropertyName("Centro")] string? Centro);
