using FugaTES.Aplicacao.Modelos.Mm;
namespace FugaTES.Aplicacao.Contratos.Mm;
public interface IRegistroProtocolosMm
{
    Task<IReadOnlyList<RegistroProtocoloMm>> LerAsync(CancellationToken ct);
    Task<bool> TentarReservarAsync(RegistroProtocoloMm reserva,CancellationToken ct);
    Task FecharAsync(Guid reservaId,string chave,ResultadoProtocoloMm resultado,CancellationToken ct);
}
