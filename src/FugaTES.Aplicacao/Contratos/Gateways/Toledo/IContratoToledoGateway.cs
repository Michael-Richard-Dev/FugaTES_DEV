using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;

namespace FugaTES.Aplicacao.Contratos.Gateways.Toledo;

public interface IContratoToledoGateway
{
    Task<ResultadoOperacaoToledo<ContratoToledoEnviado>> EnviarAsync(ContratoToledo contrato, CancellationToken cancellationToken);
}