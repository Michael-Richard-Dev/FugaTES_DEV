using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using System.Text.Json.Serialization;

namespace FugaTES.Aplicacao.Configuracao;

/// <summary>Explicit one-new-contract authorization, disabled unless fully configured.</summary>
public sealed class AcmInboundWindowOptions
{
    public bool Enabled { get; set; }
    public string? WindowId { get; set; }
    public string? AuthorizationReference { get; set; }
    public DateTimeOffset? NotBeforeUtc { get; set; }
    public DateTimeOffset? UntilUtc { get; set; }
    public string? Centro { get; set; }
    public string? Item { get; set; }
    public string[] AllowedSapPartners { get; set; } = [];
    public string[] AllowedSapMaterials { get; set; } = [];
    public string[] ExcludedContractNumbers { get; set; } = [];

    // Known previously exercised contracts must never become this window's new target.
    private static readonly string[] HistoricalContracts =
        ["1031", "1106", "1138", "1139", "1140", "1144", "1147", "1155"];

    // Mesma lista, exposta para o recorte continuo (AcmAutomaticoOptions) nao duplica-la.
    public static bool ContratoHistoricoOuExcluido(string? numero, IEnumerable<string>? excluidos = null) =>
        numero is not null &&
        HistoricalContracts.Concat(excluidos ?? []).Any(n => n.TrimStart('0') == numero.TrimStart('0'));

    private static bool Token(string? value, int max) => !string.IsNullOrWhiteSpace(value) &&
        value == value.Trim() && value.Length <= max && !value.Any(char.IsControl);
    private static bool Identifier(string? value, int max) => Token(value, max) && value!.All(char.IsAsciiDigit);

    public bool EhValida() => Enabled && Guid.TryParseExact(WindowId, "N", out var id) && id != Guid.Empty &&
        Token(AuthorizationReference, 160) &&
        !new[] { "PENDENTE", "NOT_APPROVED", "NAO VALIDADO", "NÃO VALIDADO" }.Contains(AuthorizationReference, StringComparer.OrdinalIgnoreCase) &&
        NotBeforeUtc is { Offset: var fromOffset } from && fromOffset == TimeSpan.Zero &&
        UntilUtc is { Offset: var toOffset } until && toOffset == TimeSpan.Zero &&
        until > from && until - from <= TimeSpan.FromDays(1) &&
        CentrosAcm.EhPermitido(Centro) && (Item is null || Identifier(Item, 10)) &&
        AllowedSapPartners is { Length: > 0 and <= 100 } && AllowedSapPartners.All(v => Token(v, 20)) &&
        AllowedSapMaterials is { Length: > 0 and <= 100 } && AllowedSapMaterials.All(v => Token(v, 30)) &&
        ExcludedContractNumbers is not null && ExcludedContractNumbers.All(v => Identifier(v, 20));

    public bool EstaAberta(DateTimeOffset now) => EhValida() && now >= NotBeforeUtc!.Value && now < UntilUtc!.Value;
    [JsonIgnore]
    public string CorrelationPrefix => EhValida()
        ? new AcmInboundAdmission(Guid.ParseExact(WindowId!, "N"), NotBeforeUtc!.Value, UntilUtc!.Value).CorrelationPrefix
        : throw new InvalidOperationException("R165AG_ADMISSION_WINDOW_INVALID");
    public string CriarCorrelationId() => CorrelationPrefix + Guid.NewGuid().ToString("N");
    public bool CorrelationConfere(string? value) => EhValida() && value is not null &&
        value.StartsWith(CorrelationPrefix, StringComparison.Ordinal) &&
        value.Length == CorrelationPrefix.Length + 32 &&
        Guid.TryParseExact(value[CorrelationPrefix.Length..], "N", out var id) && id != Guid.Empty;

    // R170: o cadastro automatico aceita exatamente DUAS combinacoes de negocio, como pares
    // fechados. A decisao e do tipo de contrato COM o tipo de pesagem - a operacao nao participa,
    // porque o SAP usa varios codigos de operacao para o mesmo sentido de negocio (10 e 30 sao
    // ambos compra), e exigir um valor fixo travava contrato legitimo sem motivo.
    //   compra: AC3P + E  ->  TipoLancamento 309837 (credito)
    //   venda : AC3S + S  ->  TipoLancamento 309838 (debito)
    // Misturar o par continua recusado: AC3P com pesagem de saida nunca passa.
    // O mapeamento do TipoLancamento vive em MapeadorContratoAcmGuardian e usa esta mesma regra.
    public static bool EscopoContratoAprovado(string? tipoContrato, string? tipoPesagem)
        => (tipoContrato, tipoPesagem) is ("AC3P", "E") or ("AC3S", "S");

    public bool SnapshotPermitido(ContratoSapToledoSnapshot snapshot) => EhValida() &&
        Identifier(snapshot.NumeroContrato, 20) &&
        !ContratoHistoricoOuExcluido(snapshot.NumeroContrato, ExcludedContractNumbers) &&
        snapshot.Centro == Centro && snapshot.Item == Item &&
        EscopoContratoAprovado(snapshot.TipoContrato, snapshot.TipoPesagem) &&
        snapshot.StatusContrato == "T" &&
        AllowedSapPartners.Contains(snapshot.IdParceiro, StringComparer.Ordinal) &&
        AllowedSapMaterials.Contains(snapshot.Material, StringComparer.Ordinal);

    public AcmInboundAdmission CriarAdmissao() => EhValida()
        ? new(Guid.ParseExact(WindowId!, "N"), NotBeforeUtc!.Value, UntilUtc!.Value)
        : throw new InvalidOperationException("R165AG_ADMISSION_WINDOW_INVALID");
}
