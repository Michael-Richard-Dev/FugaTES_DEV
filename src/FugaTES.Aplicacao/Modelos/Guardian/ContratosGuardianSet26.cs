using System.Text.Json.Serialization;

namespace FugaTES.Aplicacao.Modelos.Guardian;

public sealed record EmissorGuardianSet26(string? Codigo, string Descricao, string RazaoSocial, string Estado, string TipoDocumento);
public sealed record TransportadoraGuardianSet26(string? Codigo, string Descricao, string RazaoSocial);

// Guardian Item (POST /api/v0/Guardian/Item). Minimum wire comprovado: codigo, tipo, nome, fatorConversao.
// estado e OPCIONAL no POST comprovado (omitido quando nulo).
// Tipo e FatorConversao sao transportados verbatim: nenhum valor e inventado, defaultado ou normalizado aqui.
public sealed record ItemGuardianSet26(string Codigo, string Tipo, string Nome, decimal FatorConversao)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Estado { get; init; }
}
public sealed record CampoAdicionalGuardianSet26(int Numero, string Valor);
public sealed record PreCadastroRecebimentoGuardianSet26(int Id, string CodigoFluxo, string CodigoItem, string CodigoEmissor, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CodigoTransportadora, string CodigoContrato, int IdAgenda, string NumeroReferencia, [property: JsonIgnore] string UrlParaRetorno, IReadOnlyCollection<CampoAdicionalGuardianSet26> CamposAdicionais)
{
    // Functional completion mode only, never an authorization or a wire property.
    // Default preserves the existing carrier requirement for direct DTO callers.
    [JsonIgnore]
    public bool TransportadoraPreenchidaNoGuardian { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlacaVeiculo { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlacaCarreta { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Identificador { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CodigoMotorista { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CodigoVeiculo { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? PrevisaoCargaContrato { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? DataAgendamento { get; init; }
    public IReadOnlyCollection<IReadOnlyDictionary<string, string>> Documentos { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CodigoTipoVeiculo { get; init; }
}
public sealed record GuardianSet26Options(string EndpointEspecifico, string UrlParaRetorno);
public sealed record ProtocoloGuardianSet26(string Valor, DateTimeOffset? DataCadastro = null, string? NumeroTicket = null);
