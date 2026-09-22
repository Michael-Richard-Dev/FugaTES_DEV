namespace FugaTES.Aplicacao.Modelos.Recebimento;

public sealed record RecebimentoSapCpiMvp(string Processo, string CodigoFluxo, string Material, string BusinessPartnerEmissor, string BusinessPartnerTransportadora, string Contrato, string DocumentoLogisticoReferencia, string TipoContrato, string Centro, string OperacaoContrato, string TipoDocumento, string? PedidoCompras, short TipoOperacaoSap, string? ContratoAcmSap);
public sealed record CallbackGuardianMvp(string ProtocoloGuardian, string Sequencial, decimal PesoBruto, decimal Tara, decimal PesoLiquido, string Estado);
public enum EstadoCorrelacaoGuardianSap { AguardandoProtocolo, AguardandoTicket, TicketAssociado }
public sealed record CorrelacaoRecebimentoMvp(string DocumentoSap, string? ProtocoloGuardian, RecebimentoSapCpiMvp Entrada, string? TicketGuardian = null, CallbackGuardianMvp? Callback = null, EstadoCorrelacaoGuardianSap Estado = EstadoCorrelacaoGuardianSap.AguardandoProtocolo, string? CallbackFingerprint = null);
public enum StatusRegistroSnapshotMvp { Criada, Idempotente, Conflito }
public sealed record ResultadoRegistroSnapshotMvp(StatusRegistroSnapshotMvp Status, CorrelacaoRecebimentoMvp Correlacao);
public enum StatusAssociacaoProtocoloMvp { Associado, Idempotente, Conflito }
public sealed record ResultadoAssociacaoProtocoloMvp(StatusAssociacaoProtocoloMvp Status, CorrelacaoRecebimentoMvp Correlacao);
public enum StatusAssociacaoContratoAcmMvp { Associado, Idempotente, Conflito }
public sealed record ResultadoAssociacaoContratoAcmMvp(StatusAssociacaoContratoAcmMvp Status, CorrelacaoRecebimentoMvp Correlacao);
public enum StatusRegistroCallbackMvp { Novo, Idempotente, Conflito }
public sealed record ResultadoRegistroCallbackMvp(StatusRegistroCallbackMvp Status, CorrelacaoRecebimentoMvp Correlacao);
