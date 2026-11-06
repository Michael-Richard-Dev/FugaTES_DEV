using FugaTES.Aplicacao.Modelos.Mm;
namespace FugaTES.Aplicacao.Contratos.Mm;
public interface IMmRetryStore
{
 Task<RegistroProtocoloMm?> ReadRegistryAsync(string pedido,string item,CancellationToken ct);
 Task<bool> HasPendingAsync(string key,CancellationToken ct);
 Task<MmRetryCommand?> FindCommandAsync(Guid id,CancellationToken ct);
 Task<MmRetryReceipt> EnqueueAsync(MmRetryCommand command,CancellationToken ct);
 Task<MmRetryCommand?> ClaimAsync(CancellationToken ct);
 Task<bool> MarkDispatchAsync(MmRetryCommand command,CancellationToken ct);
 Task FinishAsync(MmRetryCommand command,ResultadoProtocoloMm result,bool dispatched,CancellationToken ct);
}
