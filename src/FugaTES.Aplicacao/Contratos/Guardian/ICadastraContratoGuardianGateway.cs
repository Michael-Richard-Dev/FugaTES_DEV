using FugaTES.Aplicacao.Modelos.Guardian;

namespace FugaTES.Aplicacao.Contratos.Guardian;

public interface ICadastraContratoGuardianGateway
{
    Task CadastrarAsync(ContratoGuardianSet26 contrato, CancellationToken cancellationToken);
}
