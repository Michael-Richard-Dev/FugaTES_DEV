using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Servicos.Mvp;

using FugaTES.Aplicacao.Configuracao;
namespace FugaTES.Aplicacao.Servicos.Recebimento;

// R164A: INTERNAL normalized evidence contract, NOT the Guardian HTTP/SOAP wire schema.
// The transport-specific reader must be backed by an attested R161G ticket-read source.
// No endpoint, authentication scheme, or JSON/XML property names are inferred here.
public sealed record AcmGuardianVerifiedWeighingR164A(
    string EvidenceReference,
    string ProtocoloGuardian,
    string TicketGuardian,
    string CallbackFingerprint,
    string ContratoACM,
    string Centro,
    string Material,
    string NumeroReferencia,
    decimal PesoBruto,
    decimal Tara,
    decimal PesoLiquido,
    string? EmissorSelecionadoGuardian,
    string TipoPessoa,
    string? BusinessPartnerPropriedade,
    bool PropriedadeComprovada,
    string TipoPesagem,
    DateTimeOffset? DataFinalPesagem,
    IReadOnlyCollection<AcmSapTicketQuality>? Qualidades,
    string? ChaveAcessoNFe = null,
    string? NomeMotorista = null,
    string? CpfMotorista = null,
    string? PlacaTrator = null,
    // These fields are normalized BUSINESS evidence, never SOAP wire fields.
    // 3 (TipoOperacaoCodigo) means pesagem final and is NOT a SAP operation mapping.
    short TipoOperacaoSap = 0,
    string? TipoOperacaoSapProveniencia = null,
    string? TipoOperacaoSapEvidenceReference = null);

// Boundary for an independently authenticated, ticket-scoped Guardian read.
// A concrete implementation and its effective route/method/auth/wire MUST be reviewed
// against the R161G source and provider documentation before registration.
public interface IAcmGuardianVerifiedWeighingReaderR164A
{
    Task<AcmGuardianVerifiedWeighingR164A?> ObterAsync(
        CorrelacaoRecebimentoMvp correlacao, CancellationToken ct);
}

// Adapts trusted, already-read Guardian ticket fields to R161B without changing SAP POST.
// Missing or inconsistent evidence is rejected BEFORE invoking the downstream gateway.
public sealed class AcmGuardianTicketEvidenceAdapterR164A(
    IAcmGuardianVerifiedWeighingReaderR164A reader) : IAcmSapTicketEvidenceSource
{
    public async Task<AcmSapTicketEvidence?> ObterAsync(
        CorrelacaoRecebimentoMvp correlacao, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(correlacao);
        ct.ThrowIfCancellationRequested();

        // R161F durable correlation and terminal weighing are prerequisites.
        if (correlacao.ProtocoloGuardian is null || correlacao.TicketGuardian is null ||
            correlacao.CallbackFingerprint is null || correlacao.Pesos is null ||
            !AcmR106SapReturnPreflight.EventoFinalComprovado(correlacao) ||
            correlacao.Entrada.TipoContrato != "AC3P" || !CentrosAcm.EhPermitido(correlacao.Entrada.Centro))
            return null;
        if (correlacao.Callback is { } callback &&
            (callback.Estado != "5" || callback.ProtocoloGuardian != correlacao.ProtocoloGuardian ||
             callback.Sequencial != correlacao.TicketGuardian ||
             callback.PesoBruto != correlacao.Pesos.PesoBruto || callback.Tara != correlacao.Pesos.Tara ||
             callback.PesoLiquido != correlacao.Pesos.PesoLiquido))
            return null;
        if (string.IsNullOrWhiteSpace(correlacao.Entrada.ContratoAcmSap) ||
            string.IsNullOrWhiteSpace(correlacao.Entrada.Material) ||
            string.IsNullOrWhiteSpace(correlacao.Entrada.BusinessPartnerEmissor) ||
            string.IsNullOrWhiteSpace(correlacao.Entrada.DocumentoLogisticoReferencia))
            return null;
        var x = await reader.ObterAsync(correlacao, ct).ConfigureAwait(false);
        if (x is null || string.IsNullOrWhiteSpace(x.EvidenceReference) ||
            x.ProtocoloGuardian != correlacao.ProtocoloGuardian ||
            x.TicketGuardian != correlacao.TicketGuardian ||
            x.CallbackFingerprint != correlacao.CallbackFingerprint ||
            x.ContratoACM != correlacao.Entrada.ContratoAcmSap ||
            x.Centro != correlacao.Entrada.Centro || x.Material != correlacao.Entrada.Material ||
            x.NumeroReferencia != correlacao.Entrada.DocumentoLogisticoReferencia ||
            x.PesoBruto != correlacao.Pesos.PesoBruto || x.Tara != correlacao.Pesos.Tara ||
            x.PesoLiquido != correlacao.Pesos.PesoLiquido ||
            x.DataFinalPesagem is null || x.DataFinalPesagem.Value == default(DateTimeOffset) ||
            x.TipoPessoa is not ("PF" or "PJ") || x.TipoPesagem is not ("E" or "S") ||
            // R164D: never project OperacaoContrato=10, TipoOperacaoCodigo=3 or
            // an unproven persisted field into TipoOperacaoSap. The reader must
            // provide independently attested, ticket-correlated business provenance.
            x.TipoOperacaoSap <= 0 ||
            x.TipoOperacaoSapProveniencia is not
                ("TICKET_BUSINESS_FIELD_ATTESTED" or "SAP_BUSINESS_RULE_ATTESTED") ||
            string.IsNullOrWhiteSpace(x.TipoOperacaoSapEvidenceReference) ||
            x.Qualidades is null || x.Qualidades.Count == 0 ||
            x.Qualidades.Any(q => q is null || string.IsNullOrWhiteSpace(q.Tipo) || q.ValorAferido < 0))
            return null;
        var commodity = x.Material.TrimStart('0');
        if (commodity is not ("7000000" or "7000001" or "7000002"))
            return null;
        // Never treat Guardian's selected emitter as the property BP by inference.
        // A PF property BP must be separately attested by the upstream reader.
        if (x.TipoPessoa == "PF" &&
            (!x.PropriedadeComprovada || string.IsNullOrWhiteSpace(x.BusinessPartnerPropriedade)))
            return null;
        if (x.TipoPesagem == "E" &&
            (x.ChaveAcessoNFe is null || x.ChaveAcessoNFe.Length != 44 ||
             !x.ChaveAcessoNFe.All(char.IsDigit)))
            return null;
        // SAP IdParceiro ALWAYS derives from the H4 contract, never from the
        // effective emitter selected at Guardian weighing.
        return new AcmSapTicketEvidence(
            x.EvidenceReference, x.ProtocoloGuardian, x.TicketGuardian,
            x.CallbackFingerprint, x.ContratoACM, x.Centro, x.Material,
            correlacao.Entrada.BusinessPartnerEmissor, x.TipoPessoa,
            x.TipoPessoa == "PF" ? x.BusinessPartnerPropriedade : null,
            x.TipoPesagem, x.DataFinalPesagem.Value, x.Qualidades,
            x.ChaveAcessoNFe, x.NomeMotorista, x.CpfMotorista, x.PlacaTrator,
            TipoOperacaoSap: x.TipoOperacaoSap,
            TipoOperacaoSapProveniencia: x.TipoOperacaoSapProveniencia,
            TipoOperacaoSapEvidenceReference: x.TipoOperacaoSapEvidenceReference);
    }
}
