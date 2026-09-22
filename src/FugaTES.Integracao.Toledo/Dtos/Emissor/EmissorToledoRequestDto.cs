using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Modelos.Toledo;

namespace FugaTES.Integracao.Toledo.Dtos.Emissor;

internal sealed record EmissorToledoRequestDto(
    [property: JsonPropertyName("voConfiguracao")] ConfiguracaoEmissorToledoDto VoConfiguracao,
    [property: JsonPropertyName("entidadeEmissor")] EntidadeEmissorToledoDto EntidadeEmissor)
{
    private static readonly JsonSerializerOptions SerializacaoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static EmissorToledoRequestDto Criar(EmissorToledo emissor)
    {
        ArgumentNullException.ThrowIfNull(emissor);

        return new EmissorToledoRequestDto(
            new ConfiguracaoEmissorToledoDto(string.Empty),
            new EntidadeEmissorToledoDto(
                emissor.BusinessPartner,
                emissor.TipoEmissor,
                emissor.RazaoSocial,
                emissor.Cnpj,
                emissor.TipoDocumento,
                emissor.InscricaoEstadual,
                emissor.Municipio,
                emissor.Uf,
                emissor.Cep,
                emissor.Email));
    }

    public string Serializar()
    {
        return JsonSerializer.Serialize(this, SerializacaoJsonOptions);
    }
}

internal sealed record ConfiguracaoEmissorToledoDto(
    [property: JsonPropertyName("urlParaRetorno")] string UrlParaRetorno);

internal sealed record EntidadeEmissorToledoDto(
    [property: JsonPropertyName("codigo")] string Codigo,
    [property: JsonPropertyName("tipoEmissor")] int TipoEmissor,
    [property: JsonPropertyName("razaoSocial")] string RazaoSocial,
    [property: JsonPropertyName("cnpj")] string? Cnpj,
    [property: JsonPropertyName("tipoDocumento")] int TipoDocumento,
    [property: JsonPropertyName("inscricaoEstadual")] string? InscricaoEstadual,
    [property: JsonPropertyName("municipio")] string? Municipio,
    [property: JsonPropertyName("uf")] int? Uf,
    [property: JsonPropertyName("cep")] string? Cep,
    [property: JsonPropertyName("email")] string? Email);