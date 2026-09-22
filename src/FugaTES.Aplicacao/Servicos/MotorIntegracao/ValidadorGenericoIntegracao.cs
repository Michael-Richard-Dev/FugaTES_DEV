using System.Text.RegularExpressions;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;

namespace FugaTES.Aplicacao.Servicos.MotorIntegracao;

// Validacao generica e determinista pre-claim (S3). Nenhum efeito externo, nenhuma regra empresarial.
public sealed partial class ValidadorGenericoIntegracao : IValidadorIntegracao
{
    public ResultadoValidacaoIntegracao Validar(IntegracaoMotor integracao)
    {
        ArgumentNullException.ThrowIfNull(integracao);

        if (string.IsNullOrWhiteSpace(integracao.Origem))
        {
            return ResultadoValidacaoIntegracao.Reprovar("origem-vazia");
        }

        if (string.IsNullOrWhiteSpace(integracao.Destino))
        {
            return ResultadoValidacaoIntegracao.Reprovar("destino-vazio");
        }

        if (string.IsNullOrWhiteSpace(integracao.Operacao))
        {
            return ResultadoValidacaoIntegracao.Reprovar("operacao-vazia");
        }

        if (string.IsNullOrWhiteSpace(integracao.ChaveNegocio))
        {
            return ResultadoValidacaoIntegracao.Reprovar("chave-negocio-vazia");
        }

        if (!Sha256HexRegex().IsMatch(integracao.FingerprintSha256 ?? string.Empty))
        {
            return ResultadoValidacaoIntegracao.Reprovar("fingerprint-fora-do-formato-sha256");
        }

        if (string.IsNullOrWhiteSpace(integracao.CorrelationId))
        {
            return ResultadoValidacaoIntegracao.Reprovar("correlation-id-vazio");
        }

        if (!MaquinaEstadoIntegracao.PodeAplicarTipoDisparo(integracao.Estado, integracao.TipoDisparoPendente))
        {
            return ResultadoValidacaoIntegracao.Reprovar("estado-tipo-disparo-incoerente");
        }

        // Estados enfileiraveis precisam de agenda temporal coerente.
        if ((integracao.Estado is EstadoIntegracao.Pendente or EstadoIntegracao.AguardandoReprocessamento)
            && integracao.ProximaTentativaEm is null)
        {
            return ResultadoValidacaoIntegracao.Reprovar("proxima-tentativa-ausente");
        }

        return ResultadoValidacaoIntegracao.Aprovar();
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256HexRegex();
}
