using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Modelos.Guardian;

namespace FugaTES.Integracao.Guardian;

public sealed record GuardianPreCadastroRequestDto(
    [property: JsonPropertyName("configuracao")] GuardianPreCadastroConfiguracaoDto Configuracao,
    [property: JsonPropertyName("dadosProtocoloPreCadastro")] PreCadastroRecebimentoGuardianSet26 DadosProtocoloPreCadastro);

public sealed record GuardianPreCadastroConfiguracaoDto(
    [property: JsonPropertyName("urlParaRetorno")] string UrlParaRetorno);

// Partial protocol attested by the user. This DTO is a draft contract, not a write authorization.
// It deliberately does not declare codigoContrato/codigoFluxo/codigoItem/idAgenda.
public sealed record GuardianProtocoloParcialRequestDto(
    [property: JsonPropertyName("configuracao")] GuardianPreCadastroConfiguracaoDto Configuracao,
    [property: JsonPropertyName("dadosProtocoloPreCadastro")] GuardianProtocoloParcialDadosDto DadosProtocoloPreCadastro);

public sealed record GuardianProtocoloParcialDadosDto(int Id, string NumeroReferencia,
    string CodigoEmissor, string CodigoTransportadora, IReadOnlyCollection<CampoAdicionalGuardianSet26> CamposAdicionais)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PlacaVeiculo { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PlacaCarreta { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Identificador { get; init; }
}
