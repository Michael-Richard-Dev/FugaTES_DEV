using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;

namespace FugaTES.Aplicacao.Contratos.Gateways.Toledo;

public interface IEmissorToledoGateway
{
    Task<ResultadoOperacaoToledo<EmissorToledoRegistrado>> EnviarAsync(EmissorToledo emissor, CancellationToken cancellationToken);
}