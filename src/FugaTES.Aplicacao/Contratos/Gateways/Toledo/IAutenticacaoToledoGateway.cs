using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;

namespace FugaTES.Aplicacao.Contratos.Gateways.Toledo;

public interface IAutenticacaoToledoGateway
{
    Task<ResultadoOperacaoToledo<SessaoAutenticacaoToledo>> AutenticarAsync(CancellationToken cancellationToken);
}