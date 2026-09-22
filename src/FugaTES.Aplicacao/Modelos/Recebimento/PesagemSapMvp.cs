namespace FugaTES.Aplicacao.Modelos.Recebimento;

public sealed record PesagemSapMvp(string SistemaBalanca, string Centro, string TipoPesagem, string IdPesagem, int? IdAgendamento, string IdTransportadora, string Material, string IdParceiro, decimal PesoBruto, decimal PesoTara, decimal PesoAntesDesconto, string? TipoDocumento, string? PedidoCompras, string DocumentoSap, short TipoOperacao, string ContratoACM);
public enum StatusCallbackGuardianMvp { Processado, Idempotente }
public sealed record ResultadoCallbackGuardianMvp(StatusCallbackGuardianMvp Status, PesagemSapMvp? Pesagem);
public sealed record ResultadoRecebimentoMvp(string DocumentoSap, string ProtocoloGuardian);
