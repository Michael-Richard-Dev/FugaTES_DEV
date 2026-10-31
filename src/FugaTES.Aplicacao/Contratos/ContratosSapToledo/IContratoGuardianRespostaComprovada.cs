using FugaTES.Aplicacao.Modelos.Guardian;

namespace FugaTES.Aplicacao.Contratos.ContratosSapToledo;

// Contrato.Codigo so e confirmado quando a resposta o devolve explicitamente.
// Erro=0 sem Contrato.Codigo NAO equivale a identidade de contrato retornada.
public sealed record ResultadoContratoGuardianComResposta(string? CodigoContratoConfirmado, long? SequencialContrato = null);
public interface IContratoGuardianRespostaComprovada
{
    Task<ResultadoContratoGuardianComResposta> CadastrarComRespostaAsync(
        ContratoGuardianSet26 contrato, CancellationToken cancellationToken);
}
