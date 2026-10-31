using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Guardian;

namespace FugaTES.Aplicacao.Contratos.Guardian;

public enum EstadoConsultaContratoGuardian { Existente, InexistenteComprovado, Indeterminado }
public sealed record ConsultaContratoGuardian(EstadoConsultaContratoGuardian Estado, ContratoGuardianSet26? Contrato = null, long? SequencialContrato = null);

public interface IGuardianContratoSincronizacaoGateway : ICadastraContratoGuardianGateway, IContratoGuardianRespostaComprovada
{
    Task<ConsultaContratoGuardian> ConsultarAsync(string codigo, CancellationToken ct);
    Task<ResultadoContratoGuardianComResposta> AlterarComRespostaAsync(
        ContratoGuardianSet26 atual, ContratoGuardianSet26 desejado, CancellationToken ct);
}