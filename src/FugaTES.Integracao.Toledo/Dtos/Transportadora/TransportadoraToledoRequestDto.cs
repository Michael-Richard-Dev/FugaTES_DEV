using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Modelos.Toledo;

namespace FugaTES.Integracao.Toledo.Dtos.Transportadora;

internal sealed record TransportadoraToledoRequestDto(
    [property: JsonPropertyName("entidadeTransportadora")] EntidadeTransportadoraToledoDto EntidadeTransportadora)
{
    private static readonly JsonSerializerOptions SerializacaoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static TransportadoraToledoRequestDto Criar(TransportadoraToledo transportadora)
    {
        ArgumentNullException.ThrowIfNull(transportadora);

        return new TransportadoraToledoRequestDto(
            new EntidadeTransportadoraToledoDto(
                transportadora.BusinessPartner,
                transportadora.Nome,
                transportadora.Uf));
    }

    public string Serializar()
    {
        return JsonSerializer.Serialize(this, SerializacaoJsonOptions);
    }
}

internal sealed record EntidadeTransportadoraToledoDto(
    [property: JsonPropertyName("codigo")] string Codigo,
    [property: JsonPropertyName("nome")] string? Nome,
    [property: JsonPropertyName("uf")] string Uf);