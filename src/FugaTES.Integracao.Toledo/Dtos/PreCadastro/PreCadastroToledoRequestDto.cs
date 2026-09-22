using System.Text.Json;
using System.Text.Json.Serialization;

namespace FugaTES.Integracao.Toledo.Dtos.PreCadastro;

public sealed record PreCadastroToledoRequestDto(
    [property: JsonPropertyName("configuracao")] ConfiguracaoPreCadastroToledoDto Configuracao,
    [property: JsonPropertyName("dadosProtocoloPreCadastro")] DadosProtocoloPreCadastroToledoDto DadosProtocoloPreCadastro)
{
    private static readonly JsonSerializerOptions SerializacaoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Serializar()
    {
        return JsonSerializer.Serialize(this, SerializacaoJsonOptions);
    }
}

public sealed record ConfiguracaoPreCadastroToledoDto(
    [property: JsonPropertyName("urlParaRetorno")] string? UrlParaRetorno);

public sealed record DadosProtocoloPreCadastroToledoDto(
    [property: JsonPropertyName("id")] int? Id,
    [property: JsonPropertyName("placaVeiculo")] string? PlacaVeiculo,
    [property: JsonPropertyName("placaCarreta")] string? PlacaCarreta,
    [property: JsonPropertyName("identificador")] string? Identificador,
    [property: JsonPropertyName("codigoFluxo")] string? CodigoFluxo,
    [property: JsonPropertyName("codigoItem")] string? CodigoItem,
    [property: JsonPropertyName("codigoEmissor")] string? CodigoEmissor,
    [property: JsonPropertyName("codigoTransportadora")] string? CodigoTransportadora,
    [property: JsonPropertyName("codigoMotorista")] string? CodigoMotorista,
    [property: JsonPropertyName("codigoContrato")] string CodigoContrato,
    [property: JsonPropertyName("codigoVeiculo")] string? CodigoVeiculo,
    [property: JsonPropertyName("previsaoCargaContrato")] int? PrevisaoCargaContrato,
    [property: JsonPropertyName("idAgenda")] int IdAgenda,
    [property: JsonPropertyName("dataAgendamento")] string? DataAgendamento,
    [property: JsonPropertyName("numeroReferencia")] string? NumeroReferencia,
    [property: JsonPropertyName("camposAdicionais")] IReadOnlyCollection<CampoAdicionalPreCadastroToledoDto> CamposAdicionais);

public sealed record CampoAdicionalPreCadastroToledoDto(
    [property: JsonPropertyName("numero")] int Numero,
    [property: JsonPropertyName("valor")] string Valor);
