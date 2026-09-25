using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Infraestrutura.MotorIntegracao;

namespace FugaTES.Infraestrutura.Recebimento;

public sealed class RepositorioCorrelacaoRecebimentoMemoria : IRepositorioCorrelacaoRecebimento
{
    private readonly object sync = new();
    private readonly Dictionary<string, CorrelacaoRecebimentoMvp> porDocumento = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CorrelacaoRecebimentoMvp> porProtocolo = new(StringComparer.Ordinal);

    public Task<ResultadoRegistroSnapshotMvp> RegistrarSnapshotAsync(RecebimentoSapCpiMvp entrada, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entrada); cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (porDocumento.TryGetValue(entrada.DocumentoLogisticoReferencia, out var atual))
                return Task.FromResult(new ResultadoRegistroSnapshotMvp(SnapshotIgual(atual.Entrada, entrada) ? StatusRegistroSnapshotMvp.Idempotente : StatusRegistroSnapshotMvp.Conflito, atual));
            var criada = new CorrelacaoRecebimentoMvp(entrada.DocumentoLogisticoReferencia, null, entrada with { ContratoAcmSap = null });
            porDocumento.Add(criada.DocumentoSap, criada);
            return Task.FromResult(new ResultadoRegistroSnapshotMvp(StatusRegistroSnapshotMvp.Criada, criada));
        }
    }

    public Task<CorrelacaoRecebimentoMvp?> ObterPorDocumentoAsync(string documentoSap, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult(porDocumento.GetValueOrDefault(documentoSap));
    }

    public Task<ResultadoAssociacaoContratoAcmMvp> AssociarContratoAcmAsync(string documentoSap, string contratoAcmSap, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(contratoAcmSap)) throw new ArgumentException("ACM contract is absent.", nameof(contratoAcmSap));
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!porDocumento.TryGetValue(documentoSap, out var atual)) throw new InvalidOperationException("Receipt snapshot was not found.");
            if (atual.Entrada.ContratoAcmSap is not null)
                return Task.FromResult(new ResultadoAssociacaoContratoAcmMvp(atual.Entrada.ContratoAcmSap == contratoAcmSap ? StatusAssociacaoContratoAcmMvp.Idempotente : StatusAssociacaoContratoAcmMvp.Conflito, atual));
            var atualizado = atual with { Entrada = atual.Entrada with { ContratoAcmSap = contratoAcmSap } };
            porDocumento[documentoSap] = atualizado;
            if (atual.ProtocoloGuardian is not null) porProtocolo[atual.ProtocoloGuardian] = atualizado;
            return Task.FromResult(new ResultadoAssociacaoContratoAcmMvp(StatusAssociacaoContratoAcmMvp.Associado, atualizado));
        }
    }

    public Task<ResultadoAssociacaoProtocoloMvp> AssociarProtocoloAsync(string documentoSap, string protocoloGuardian, CancellationToken cancellationToken, DateTimeOffset protocoloCriadoEm)
    {
        if (protocoloCriadoEm == default) throw new ArgumentException("Protocol creation timestamp is required.", nameof(protocoloCriadoEm));
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!porDocumento.TryGetValue(documentoSap, out var atual)) throw new InvalidOperationException("Receipt snapshot was not found.");
            var instante = new DateTimeOffset(protocoloCriadoEm.ToUniversalTime().Ticks / 10 * 10, TimeSpan.Zero);
            if (atual.ProtocoloGuardian is not null)
                return Task.FromResult(new ResultadoAssociacaoProtocoloMvp(
                    atual.ProtocoloGuardian == protocoloGuardian && atual.ProtocoloCriadoEm == instante
                        ? StatusAssociacaoProtocoloMvp.Idempotente : StatusAssociacaoProtocoloMvp.Conflito, atual));
            if (porProtocolo.ContainsKey(protocoloGuardian)) return Task.FromResult(new ResultadoAssociacaoProtocoloMvp(StatusAssociacaoProtocoloMvp.Conflito, atual));
            var atualizado = atual with { ProtocoloGuardian = protocoloGuardian, ProtocoloCriadoEm = instante, Estado = EstadoCorrelacaoGuardianSap.AguardandoTicket };
            porDocumento[documentoSap] = atualizado;
            porProtocolo[protocoloGuardian] = atualizado;
            return Task.FromResult(new ResultadoAssociacaoProtocoloMvp(StatusAssociacaoProtocoloMvp.Associado, atualizado));
        }
    }

    public Task<CorrelacaoRecebimentoMvp?> ObterPorProtocoloAsync(string protocoloGuardian, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); lock (sync) return Task.FromResult(porProtocolo.GetValueOrDefault(protocoloGuardian)); }

    public Task<ResultadoRegistroCallbackMvp> RegistrarCallbackAsync(CallbackGuardianMvp callback, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!porProtocolo.TryGetValue(callback.ProtocoloGuardian, out var atual)) throw new InvalidOperationException("Guardian callback correlation was not found.");
            var fingerprint = CallbackGuardianMvpFingerprint.Calcular(callback);
            if (atual.CallbackFingerprint is not null) return Task.FromResult(new ResultadoRegistroCallbackMvp(atual.TicketGuardian == callback.Sequencial.Trim() && atual.CallbackFingerprint == fingerprint ? StatusRegistroCallbackMvp.Idempotente : StatusRegistroCallbackMvp.Conflito, atual));
            var atualizado = atual with { TicketGuardian = callback.Sequencial.Trim(), Callback = callback, Estado = EstadoCorrelacaoGuardianSap.TicketAssociado, CallbackFingerprint = fingerprint, Pesos = new(callback.PesoBruto, callback.Tara, callback.PesoLiquido) };
            porProtocolo[callback.ProtocoloGuardian] = atualizado;
            porDocumento[atual.DocumentoSap] = atualizado;
            return Task.FromResult(new ResultadoRegistroCallbackMvp(StatusRegistroCallbackMvp.Novo, atualizado));
        }
    }

    public Task<ResultadoRegistroCallbackMvp> RegistrarCallbackEEnfileirarAsync(
        CallbackGuardianMvp callback, CandidatoIntegracao candidato, RepositorioMotorIntegracaoFake motor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!porProtocolo.TryGetValue(callback.ProtocoloGuardian, out var atual))
                throw new InvalidOperationException("Guardian callback correlation was not found.");
            var fingerprint = CallbackGuardianMvpFingerprint.Calcular(callback);
            var pesos = new PesosCallbackSapMvp(callback.PesoBruto, callback.Tara, callback.PesoLiquido);
            if (atual.CallbackFingerprint is not null)
            {
                var same = atual.TicketGuardian == callback.Sequencial.Trim() &&
                    atual.CallbackFingerprint == fingerprint && atual.Pesos == pesos;
                if (same && !motor.Integracoes.Any(i => i.FingerprintSha256 == candidato.FingerprintSha256 &&
                    i.Operacao == candidato.Operacao && i.ChaveNegocio == candidato.ChaveNegocio))
                    throw new InvalidOperationException("Accepted callback has no registered integration.");
                return Task.FromResult(new ResultadoRegistroCallbackMvp(same ? StatusRegistroCallbackMvp.Idempotente : StatusRegistroCallbackMvp.Conflito, atual));
            }
            if (motor.RegistrarCandidatoAsync(candidato, DateTimeOffset.UtcNow, cancellationToken)
                .GetAwaiter().GetResult() != ResultadoRegistroIntegracao.Criada)
                throw new InvalidOperationException("Callback integration identity already exists.");
            var atualizado = atual with { TicketGuardian = callback.Sequencial.Trim(), Callback = callback,
                Estado = EstadoCorrelacaoGuardianSap.TicketAssociado, CallbackFingerprint = fingerprint, Pesos = pesos };
            porProtocolo[callback.ProtocoloGuardian] = atualizado;
            porDocumento[atual.DocumentoSap] = atualizado;
            return Task.FromResult(new ResultadoRegistroCallbackMvp(StatusRegistroCallbackMvp.Novo, atualizado));
        }
    }

    private static bool SnapshotIgual(RecebimentoSapCpiMvp left, RecebimentoSapCpiMvp right) =>
        left.DocumentoLogisticoReferencia == right.DocumentoLogisticoReferencia &&
        left.Material == right.Material && left.BusinessPartnerEmissor == right.BusinessPartnerEmissor &&
        left.BusinessPartnerTransportadora == right.BusinessPartnerTransportadora && left.Centro == right.Centro &&
        left.TipoDocumento == right.TipoDocumento && left.Contrato == right.Contrato &&
        left.OperacaoContrato == right.OperacaoContrato && left.TipoOperacaoSap == right.TipoOperacaoSap &&
        left.PedidoCompras == right.PedidoCompras && left.TipoContrato == right.TipoContrato &&
        left.UrlParaRetorno == right.UrlParaRetorno;
}
