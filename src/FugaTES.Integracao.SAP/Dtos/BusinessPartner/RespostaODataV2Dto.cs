using System.Text.Json.Serialization;

namespace FugaTES.Integracao.SAP.Dtos.BusinessPartner;

internal sealed class RespostaODataV2Dto
{
    [JsonPropertyName("d")]
    public BusinessPartnerSapDto? D { get; set; }
}

internal sealed class ODataResultsDto<T>
{
    [JsonPropertyName("results")]
    public List<T>? Results { get; set; }

    [JsonPropertyName("__next")]
    public string? Next { get; set; }
}
