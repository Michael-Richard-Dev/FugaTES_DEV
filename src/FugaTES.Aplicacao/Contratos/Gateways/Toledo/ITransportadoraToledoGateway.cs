using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;

namespace FugaTES.Aplicacao.Contratos.Gateways.Toledo;

public interface ITransportadoraToledoGateway
{
    Task<ResultadoOperacaoToledo<TransportadoraToledoRegistrada>> EnviarAsync(TransportadoraToledo transportadora, CancellationToken cancellationToken);
}