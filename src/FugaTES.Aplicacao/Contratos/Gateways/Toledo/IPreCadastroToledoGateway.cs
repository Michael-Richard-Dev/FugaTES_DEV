using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;

namespace FugaTES.Aplicacao.Contratos.Gateways.Toledo;

public interface IPreCadastroToledoGateway
{
    Task<ResultadoPreCadastroToledo> EnviarAsync(PreCadastroToledo preCadastro, CancellationToken cancellationToken);
}
