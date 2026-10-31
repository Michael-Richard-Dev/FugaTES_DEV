using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Aplicacao.Contratos.Recebimento;

public interface IRegistroEventoGuardianStore
{
    Task<StatusRegistroEventoGuardian> RegistrarAsync(
        EventoGuardianRecebimento evento, string fingerprint, CancellationToken cancellationToken);
}
