namespace FugaTES.Aplicacao.Consultas.RecebimentosToledo;

// Read-side proof only. No command, writer or credential is carried by this contract.
public sealed record ToledoSapJsonEvento(Guid Id,string Evento,Guid? TentativaId,DateTimeOffset CriadoEm,
 string? RequestSha256,string? PayloadVersion,long? EpochMilliseconds,bool? PostIniciado,int? HttpStatus,string? Codigo,bool Valido)
{
 public FugaTES.Aplicacao.Servicos.Recebimento.ToledoDeliveryPartnerProof? DeliveryPartners {get;init;}
 public string? ParceirosVersao {get;init;}
 public IReadOnlyList<FugaTES.Aplicacao.Servicos.Recebimento.ToledoSapParceiroAuditado> ParceirosContrato {get;init;}=[];
}
public sealed record ToledoSapJsonFonte(RecebimentoToledoDocumento Original,Guid? IntegracaoId,string? FingerprintIntegracao,
 int IntegracoesEncontradas,IReadOnlyList<ToledoSapJsonEvento> Eventos,bool EvidenciaTruncada)
{
 public FugaTES.Aplicacao.Servicos.Recebimento.ToledoDeliveryPartnerProof? DeliveryPartners {get;init;}
 public DateTimeOffset RecebidoEm {get;init;}
 public IReadOnlyList<FugaTES.Aplicacao.Servicos.Recebimento.ToledoSapContratoFonte> ContratosSap {get;init;}=[];
}
public sealed record ToledoSapJsonVisualizacao(Guid ReciboId,string? Json,string Estado,string Mensagem,string? Sha256,
 string? Versao,Guid? TentativaId,DateTimeOffset? PreparadoEm,int? HttpStatus,string? Projecao);
public interface IToledoSapJsonReadRepository
{ Task<ToledoSapJsonFonte> ObterFonteJsonSapAsync(Guid reciboId,CancellationToken ct); }
