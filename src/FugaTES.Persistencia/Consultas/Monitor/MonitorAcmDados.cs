using FugaTES.Aplicacao.Consultas.Monitor;
using System.Globalization;

namespace FugaTES.Persistencia.Consultas.Monitor;

public sealed record MonitorAcmDetalhe(MonitorDetalhe Detalhe, MonitorAcmDados Acm);

public sealed record MonitorAcmDados
{
    public bool ContratosDisponiveis { get; init; }
    public bool CorrelacaoDisponivel { get; init; }
    public bool IdentidadesDisponiveis { get; init; }
    public bool EvidenciaDisponivel { get; init; }
    public bool CorrelacaoAmbigua { get; init; }
    public string? Contrato { get; init; }
    public long? Versao { get; init; }
    public string? Centro { get; init; }
    public DateTimeOffset? RecebidoEm { get; init; }
    public string? CodigoGuardian { get; init; }
    public string? Referencia { get; init; }
    public string? Protocolo { get; init; }
    public string? Sequencial { get; init; }
    public string? SequencialContrato { get; init; }
    public string? HashContratoMetadata { get; init; }
    public string? HashProtocoloMetadata { get; init; }
    public string EstadoMetadataU { get; init; } = "AUSENTE";
    public string? CallbackIdOriginal { get; init; }
    public string? TicketCodigo { get; init; }
    public bool EvidenciaCompleta { get; init; }
    public DateTimeOffset? EvidenciaRecebidaEm { get; init; }

    // These hashes identify prepared requests, not the canonical H4 inbound fingerprint.
    public MonitorAcmDados ComMetadataU(MonitorIntegracao linha)
    {
        var result = this with { SequencialContrato = null, HashContratoMetadata = null, HashProtocoloMetadata = null, EstadoMetadataU = "AUSENTE" };
        if (linha.UltimoResultado is not { } metadata || !metadata.StartsWith("U:", StringComparison.Ordinal)) return result;
        result = result with { EstadoMetadataU = "FORMATO_OU_ESTADO_INVALIDO" };
        var fields = metadata.Split(':');
        static bool Hash(string value) => value.Length == 64 && value.All(c => char.IsAsciiHexDigit(c) && !char.IsLower(c));
        static bool Positive(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0;
        if (metadata.Length > 160 || fields.Length != 4 || fields[0] != "U" || !Hash(fields[1]) ||
            linha.Operacao != "SAP_CONTRATO_TOLEDO" || linha.Fluxo != "ACM_IN" ||
            (fields[3].Length > 0 && !Positive(fields[3]))) return result;
        var protocolStage = linha.EtapaFuncional is "R165U_REST_ARMED" or "R165U_CONFIRMED" or "R165Y_REST_REJECTED";
        var stateMatches = (linha.EtapaFuncional, linha.StatusFuncional) switch
        {
            ("R165U_SOAP_CONFIRMED", "EM_PROCESSAMENTO") => true,
            ("R165U_PROTOCOL_BLOCKED", "AGUARDANDO_DEPENDENCIA_EXTERNA") => true,
            ("R165U_REST_ARMED", "RESULTADO_INDETERMINADO") => true,
            ("R165U_CONFIRMED", "AGUARDANDO_CALLBACK") => true,
            ("R165Y_REST_REJECTED", "FALHA_NAO_REPROCESSAVEL") => true,
            _ => false
        };
        if (!stateMatches || (protocolStage ? !Hash(fields[2]) : fields[2].Length != 0)) return result;
        if (CorrelacaoAmbigua || string.IsNullOrWhiteSpace(CodigoGuardian) ||
            MonitorOperacionalConsulta.TextoSeguro(CodigoGuardian) != CodigoGuardian)
            return result with { EstadoMetadataU = "VINCULO_H4_NAO_COMPROVADO" };
        if (linha.EtapaFuncional == "R165U_CONFIRMED" &&
            (linha.ProtocoloGuardian is not { } protocol || !Positive(protocol) || protocol != Protocolo))
            return result with { EstadoMetadataU = "PROTOCOLO_DIVERGENTE" };
        return result with
        {
            SequencialContrato = fields[3].Length == 0 ? null : fields[3], HashContratoMetadata = fields[1],
            HashProtocoloMetadata = fields[2].Length == 0 ? null : fields[2], EstadoMetadataU = "FORMATO_E_ESTADO_VALIDOS"
        };
    }

    public MonitorAcmDados Sanitizar() => this with
    {
        Contrato = MonitorOperacionalConsulta.TextoSeguro(Contrato), Centro = MonitorOperacionalConsulta.TextoSeguro(Centro),
        CodigoGuardian = MonitorOperacionalConsulta.TextoSeguro(CodigoGuardian), Referencia = MonitorOperacionalConsulta.TextoSeguro(Referencia),
        Protocolo = MonitorOperacionalConsulta.TextoSeguro(Protocolo), Sequencial = MonitorOperacionalConsulta.TextoSeguro(Sequencial),
        SequencialContrato = MonitorOperacionalConsulta.TextoSeguro(SequencialContrato),
        CallbackIdOriginal = MonitorOperacionalConsulta.TextoSeguro(CallbackIdOriginal), TicketCodigo = MonitorOperacionalConsulta.TextoSeguro(TicketCodigo)
    };
}
