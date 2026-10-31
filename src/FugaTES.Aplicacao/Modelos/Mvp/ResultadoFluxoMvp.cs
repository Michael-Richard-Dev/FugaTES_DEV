namespace FugaTES.Aplicacao.Modelos.Mvp;

public enum FluxoMvp { SdOut, MmInPreCadastro, MmIn101, AcmInContrato, AcmInPesagem, AcmOut }

public enum StatusFluxoMvp
{
    Recebido, Validado, AguardandoDecisaoFuncional, AguardandoDependenciaExterna,
    ProntoParaEnvio, EmProcessamento, AguardandoCallback, Concluido,
    FalhaReprocessavel, ResultadoIndeterminado, FalhaNaoReprocessavel
}

public sealed record ResultadoFluxoMvp(
    StatusFluxoMvp Status, string Correlacao, string Etapa,
    string? ReferenciaExterna = null, string? DecisaoPendente = null, string? Erro = null,
    string? ResultadoFuncional = null, string? SalesOrder = null, string? DeliveryDocument = null,
    string? PurchaseOrder = null, string? MaterialDocument = null, string? MaterialDocumentYear = null,
    string? ProtocoloGuardian = null, string? NumeroTicketGuardian = null,
    string? ErroCodigo = null, string? ErroCategoria = null, string? ErroEtapa = null,
    bool? ErroReprocessavel = null, bool ErroResultadoIndeterminado = false)
{
    public static string MapearFluxo(string operacao) => operacao switch
    {
        "MVP_SDOUT" => "SD_OUT",
        "MVP_MMINPRECADASTRO" or "MVP_MMIN101" => "MM_IN",
        "MVP_ACMINCONTRATO" or "MVP_ACMINPESAGEM" or "SAP_CONTRATO_TOLEDO" => "ACM_IN",
        "MVP_ACMOUT" => "ACM_OUT",
        _ => throw new ArgumentException("MVP_OPERATION_UNKNOWN")
    };

    public ResultadoFluxoMvp Normalizar()
    {
        if (!Enum.IsDefined(Status) || string.IsNullOrWhiteSpace(Correlacao) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Etapa, "^[A-Z][A-Z0-9_]*$"))
            throw new ArgumentException("MVP_RESULT_INVALID");
        if (Status == StatusFluxoMvp.AguardandoDecisaoFuncional && string.IsNullOrWhiteSpace(DecisaoPendente))
            throw new ArgumentException("MVP_PENDING_DECISION_MISSING");
        foreach (var value in new[] { Correlacao, Etapa, ReferenciaExterna, DecisaoPendente, Erro,
            ResultadoFuncional, SalesOrder, DeliveryDocument, PurchaseOrder, MaterialDocument,
            MaterialDocumentYear, ProtocoloGuardian, NumeroTicketGuardian, ErroCodigo, ErroCategoria, ErroEtapa })
        {
            if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 160 ||
                System.Text.RegularExpressions.Regex.IsMatch(value,
                    @"(?i)authorization|bearer|basic\s|password|senha|assinatura|cookie|secret|host\s*=|username\s*=|token\s*[:=]|stack\s*trace|\r|\n")))
                throw new ArgumentException("MVP_RESULT_UNSAFE_FIELD");
        }
        return this with
        {
            ErroCodigo = ErroCodigo ?? Erro,
            ErroEtapa = ErroEtapa ?? (ErroCodigo is not null || Erro is not null ? Etapa : null),
            ErroReprocessavel = ErroReprocessavel ?? (Status == StatusFluxoMvp.FalhaReprocessavel ? true :
                Status is StatusFluxoMvp.FalhaNaoReprocessavel or StatusFluxoMvp.ResultadoIndeterminado ? false : null),
            ErroResultadoIndeterminado = Status == StatusFluxoMvp.ResultadoIndeterminado
        };
    }
}

public sealed class CapabilityMvpPendenteException(string capability)
    : Exception("MVP_CAPABILITY_PENDING: " + capability)
{
    public string Capability { get; } = capability;
}
