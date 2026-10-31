using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;

namespace FugaTES.Aplicacao.Servicos.Mvp;

// A read-only scope, not a write authorization. The shipped host selects the existing R104 scope.
public sealed record AcmR106SapReturnScope(Guid IntegrationId, string Contract, long H4Version, string Fingerprint)
{
    public static AcmR106SapReturnScope R104 => new(AcmR104Scope.IntegrationId, AcmR104Scope.Contract,
        AcmR104Scope.H4Version, AcmR104Scope.Fingerprint);
}

public interface IAcmR106SapReturnPreflight
{
    Task<bool> ValidarAsync(CorrelacaoRecebimentoMvp correlation, CancellationToken ct);
}

// Reuses H4, the existing protocol link, integration metadata and sender journal.
// No reservation, mutation, callback dispatch, or fabricated claim ownership occurs here.
public sealed class AcmR106SapReturnPreflight(
    IContratoSapToledoVersaoStore versions, IAcmGuardianVinculoStore links,
    IMotorIntegracaoRepository integrations, IResultadoFluxoMvpStore journal,
    AcmR106SapReturnScope scope) : IAcmR106SapReturnPreflight
{
    public static bool RequiresAcmGuards(RecebimentoSapCpiMvp input) =>
        (input.Centro == "3011" && input.TipoContrato == "AC3P") ||
        input.Contrato.TrimStart('0') == AcmR104Scope.Contract.TrimStart('0') ||
        input.ContratoAcmSap?.TrimStart('0') == AcmR104Scope.Contract.TrimStart('0');

    public async Task<bool> ValidarAsync(CorrelacaoRecebimentoMvp c, CancellationToken ct)
    {
        var v = await versions.ObterPorIntegracaoIdAsync(scope.IntegrationId, ct);
        var link = await links.ObterPorIntegracaoAsync(scope.IntegrationId, ct);
        var source = await integrations.ObterPorIdAsync(scope.IntegrationId, ct);
        if (v is null || link is null || source is null || scope.H4Version <= 0 ||
            string.IsNullOrWhiteSpace(scope.Fingerprint) || v.IntegracaoId != scope.IntegrationId ||
            v.Versao != scope.H4Version || v.NumeroContrato != scope.Contract ||
            v.FingerprintSha256 != scope.Fingerprint ||
            new ContratoSapToledoCanonicalizer().Canonicalizar(v.Snapshot).FingerprintSha256 != scope.Fingerprint ||
            source.Id != v.IntegracaoId || source.Origem != "SAP" || source.Destino != "TOLEDO" ||
            source.Operacao != "SAP_CONTRATO_TOLEDO" || source.ChaveNegocio != v.NumeroContrato ||
            source.FingerprintSha256 != v.FingerprintSha256 || string.IsNullOrWhiteSpace(source.CorrelationId))
            return false;

        var s = v.Snapshot;
        var x = c.Entrada;
        if (link.IntegracaoIdOrigem != v.IntegracaoId || link.NumeroContratoSap != v.NumeroContrato ||
            link.VersaoH4 != v.Versao || link.ProtocoloIdGuardian is not > 0 || link.ProtocoloIdGuardian == 3 ||
            link.ProtocoloIdGuardian.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) != c.ProtocoloGuardian ||
            link.NumeroReferenciaSolicitado != c.DocumentoSap || c.DocumentoSap != x.DocumentoLogisticoReferencia ||
            string.IsNullOrWhiteSpace(link.CodigoContratoConfirmado) ||
            link.CodigoContratoConfirmado != link.CodigoContratoSolicitado ||
            link.CodigoContratoConfirmado != v.NumeroContrato.TrimStart('0') ||
            (link.NumeroTicketGuardian is not null && link.NumeroTicketGuardian != c.TicketGuardian) ||
            x.Processo != "RECEBIMENTO" || x.Centro != "3011" || x.Centro != s.Centro ||
            x.TipoContrato != "AC3P" || x.TipoContrato != s.TipoContrato || x.TipoDocumento != s.TipoContrato ||
            x.Contrato != v.NumeroContrato || x.ContratoAcmSap != v.NumeroContrato ||
            x.Material != s.Material || x.BusinessPartnerEmissor != s.IdParceiro ||
            x.OperacaoContrato != s.OperacaoContrato || x.TipoOperacaoSap <= 0)
            return false;

        // ObterAsync reads by the actual persisted source metadata, never by the outbound event ID.
        var lookup = new ClaimIntegracaoMotor(source.Id, Guid.Empty, source.CorrelationId,
            source.Origem, source.Destino, source.Operacao, source.ChaveNegocio, source.FingerprintSha256,
            0, string.Empty, default);
        await journal.ExigirDisponibilidadeAsync(ct);
        var result = await journal.ObterAsync(lookup, ct);
        return result is { Status: StatusFluxoMvp.AguardandoCallback, Etapa: "R57_CONFIRMED" } &&
            result.Correlacao == source.CorrelationId && result.ProtocoloGuardian == c.ProtocoloGuardian &&
            !result.ErroResultadoIndeterminado && result.Erro is null && result.ErroCodigo is null;
    }

    public static bool EventoFinalComprovado(CorrelacaoRecebimentoMvp c)
    {
        if (c.Estado != EstadoCorrelacaoGuardianSap.TicketAssociado || c.Pesos is not { } p ||
            !GuardianFinalWeighingPolicy.Valid(p.PesoBruto, p.Tara, p.PesoLiquido) ||
            !long.TryParse(c.ProtocoloGuardian, out var protocol) || protocol <= 0 || protocol == 3 ||
            !long.TryParse(c.TicketGuardian, out var ticket) || ticket <= 0)
            return false;
        var legacy = new CallbackGuardianMvp(c.ProtocoloGuardian!, c.TicketGuardian!, p.PesoBruto, p.Tara, p.PesoLiquido, "5");
        var final = new EventoGuardianRecebimento(protocol, ticket, 5, p.PesoBruto, p.Tara, p.PesoLiquido);
        // Both existing ledgers are supported without rewriting their persisted fingerprints.
        return c.CallbackFingerprint == CallbackGuardianMvpFingerprint.Calcular(legacy) ||
            c.CallbackFingerprint == ProcessarEventoGuardianCasoDeUso.CalcularFingerprint(final);
    }
}
