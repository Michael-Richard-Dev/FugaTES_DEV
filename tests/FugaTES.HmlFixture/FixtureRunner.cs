using System.Globalization;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.HmlFixture;

public sealed record FixtureEvidence(Guid CorrelacaoId, int ParentRowCount, int EventLedgerRowCount);

public interface IFixtureReadback
{
    Task<FixtureEvidence> ReadAsync(string protocol, CancellationToken cancellationToken);
}

public sealed record FixtureResult(Guid CorrelacaoId, string ProtocoloGuardianTeste,
    bool TicketGuardianIsNull, int ParentRowCount, int EventLedgerRowCount);

public sealed class FixtureAssociationException() : InvalidOperationException("HML protocol association failed; parent may remain without a protocol.");

public static class FixtureRunner
{
    public static void ValidateExecutionGuard(string? enabled, string? environment)
    {
        if (enabled != "true" || environment != "HML")
            throw new InvalidOperationException("HML fixture guard is disabled.");
    }

    public static void ValidateGuards(string? enabled, string? environment, string? protocolText,
        RecebimentoSapCpiMvp? input)
    {
        ValidateExecutionGuard(enabled, environment);
        if (string.IsNullOrEmpty(protocolText) ||
            !long.TryParse(protocolText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var protocol) ||
            protocol.ToString(CultureInfo.InvariantCulture) != protocolText)
            throw new InvalidOperationException("A canonical Int64 test protocol is required.");
        if (input is null || input.Processo != "RECEBIMENTO" || input.CodigoFluxo != "FLX_RECEBIMENTO" ||
            string.IsNullOrWhiteSpace(input.DocumentoLogisticoReferencia) ||
            !input.DocumentoLogisticoReferencia.StartsWith("HML-", StringComparison.Ordinal) ||
            new[] { input.Material, input.BusinessPartnerEmissor, input.BusinessPartnerTransportadora,
                input.Centro, input.Contrato, input.PedidoCompras }.Any(x =>
                    string.IsNullOrWhiteSpace(x) || !x.StartsWith("HML-", StringComparison.Ordinal)) ||
            string.IsNullOrWhiteSpace(input.TipoDocumento) ||
            string.IsNullOrWhiteSpace(input.OperacaoContrato) ||
            string.IsNullOrWhiteSpace(input.TipoContrato) ||
            (input.ContratoAcmSap is not null && !input.ContratoAcmSap.StartsWith("HML-", StringComparison.Ordinal)))
            throw new InvalidOperationException("Required HML snapshot fields are absent or invalid.");
    }

    public static async Task<FixtureResult> ExecuteAsync(string? enabled, string? environment,
        string? protocolText, RecebimentoSapCpiMvp? input, IRepositorioCorrelacaoRecebimento repository,
        IFixtureReadback readback, CancellationToken cancellationToken)
    {
        ValidateGuards(enabled, environment, protocolText, input);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(readback);
        var protocol = protocolText!;
        var snapshot = input!;

        if (await repository.ObterPorProtocoloAsync(protocol, cancellationToken) is not null ||
            await repository.ObterPorDocumentoAsync(snapshot.DocumentoLogisticoReferencia, cancellationToken) is not null)
            throw new InvalidOperationException("HML fixture identity already exists.");

        var created = await repository.RegistrarSnapshotAsync(snapshot, cancellationToken);
        if (created.Status != StatusRegistroSnapshotMvp.Criada ||
            created.Correlacao.ProtocoloGuardian is not null || created.Correlacao.TicketGuardian is not null)
            throw new InvalidOperationException("HML parent was not created in its initial state.");

        var associated = await repository.AssociarProtocoloAsync(snapshot.DocumentoLogisticoReferencia,
            protocol, cancellationToken, DateTimeOffset.UtcNow);
        if (associated.Status != StatusAssociacaoProtocoloMvp.Associado)
            throw new FixtureAssociationException();

        var loaded = await repository.ObterPorProtocoloAsync(protocol, cancellationToken);
        if (loaded is null || loaded.DocumentoSap != snapshot.DocumentoLogisticoReferencia ||
            loaded.ProtocoloGuardian != protocol || loaded.TicketGuardian is not null ||
            loaded.CallbackFingerprint is not null || loaded.Pesos is not null ||
            loaded.Estado != EstadoCorrelacaoGuardianSap.AguardandoTicket)
            throw new InvalidOperationException("HML parent readback is not callback-ready.");

        var evidence = await readback.ReadAsync(protocol, cancellationToken);
        if (evidence.CorrelacaoId == Guid.Empty || evidence.ParentRowCount != 1 ||
            evidence.EventLedgerRowCount != 0)
            throw new InvalidOperationException("HML fixture diagnostic counts are not clean.");
        return new FixtureResult(evidence.CorrelacaoId, protocol, true,
            evidence.ParentRowCount, evidence.EventLedgerRowCount);
    }
}
