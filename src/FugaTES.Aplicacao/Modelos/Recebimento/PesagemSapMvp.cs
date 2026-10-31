namespace FugaTES.Aplicacao.Modelos.Recebimento;

public sealed record PesagemSapMvp(string SistemaBalanca, string Centro, string TipoPesagem, string IdPesagem, int? IdAgendamento, string? IdTransportadora, string Material, string IdParceiro, decimal PesoBruto, decimal PesoTara, decimal PesoAntesDesconto, string? TipoDocumento, string? PedidoCompras, string DocumentoSap, short TipoOperacao, string ContratoACM, string? NumeroProtocolo = null, DateTimeOffset? ProtocoloCriadoEm = null, AcmSapTicketEvidence? EvidenciaTicket = null);
public enum StatusCallbackGuardianMvp { Aceito, Idempotente }
public sealed record ResultadoCallbackGuardianMvp(StatusCallbackGuardianMvp Status, PesagemSapMvp? Pesagem);
public sealed record ResultadoRecebimentoMvp(string DocumentoSap, string? ProtocoloGuardian, bool TrabalhoRegistrado);


// R161B — only a proven Guardian ticket can supply operational SAP fields.
// This record is not populated from the SAP example JSON or the H4 contract.
public sealed record AcmSapTicketQuality(string Tipo, string? Grupo, decimal ValorAferido);
public sealed record AcmSapTicketEvidence(
    string EvidenceReference,
    string ProtocoloGuardian,
    string TicketGuardian,
    string CallbackFingerprint,
    string ContratoACM,
    string Centro,
    string Material,
    string BusinessPartnerContrato,
    string TipoPessoa, // "PF" or "PJ", attested by the ticket source
    string? BusinessPartnerPropriedade,
    string TipoPesagem, // "1"/"E" or "2"/"S", attested by the operation
    DateTimeOffset DataFinalPesagem,
    IReadOnlyCollection<AcmSapTicketQuality> Qualidades,
    string? ChaveAcessoNFe = null,
    string? NomeMotorista = null,
    string? CpfMotorista = null,
    string? PlacaTrator = null,
    short? TipoOperacaoSap = null, // Proven operational value, never OperacaoContrato.
    string? IdAgendamentoSap = null, // Optional scheduling identity, never inferred from ticket/protocol.
    string? TipoOperacaoSapProveniencia = null,
    string? TipoOperacaoSapEvidenceReference = null);

public static class AcmSapWeighingDirection
{
    // Recebimento SAP.xlsx, row 5, and meeting 21/10/2026, 1:33:20.
    public static string? ToSap(string? value) => value switch
    {
        "1" or "E" => "E",
        "2" or "S" => "S",
        _ => null
    };
}

// Intentionally without production registration: the Guardian ticket data and its
// provenance must be attested before wiring this source to the runtime.
public interface IAcmSapTicketEvidenceSource
{
    Task<AcmSapTicketEvidence?> ObterAsync(CorrelacaoRecebimentoMvp correlacao, CancellationToken ct);
}

// An acknowledgement here means committed durable evidence, not a SAP business success.
public interface IAcmSapTicketEvidenceStore
{
    Task PreservarAsync(CorrelacaoRecebimentoMvp correlacao, PesagemSapMvp pesagem, CancellationToken ct);
}
