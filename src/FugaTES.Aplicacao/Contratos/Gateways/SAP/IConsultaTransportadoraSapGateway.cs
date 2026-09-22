using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;

namespace FugaTES.Aplicacao.Contratos.Gateways.SAP;

public interface IConsultaTransportadoraSapGateway
{
    Task<ResultadoConsultaSap<ParceiroNegocioConsultado>> ConsultarAsync(string businessPartner, CancellationToken cancellationToken);
}
