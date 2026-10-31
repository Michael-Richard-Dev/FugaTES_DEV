using System.Text.Json.Serialization;

namespace FugaTES.Integracao.Guardian;

public sealed record GuardianPreCadastroResponseDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("dataCadastro")] DateTimeOffset? DataCadastro,
    [property: JsonPropertyName("numeroTicket")] string? NumeroTicket = null);
