using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;

namespace FugaTES.Aplicacao.Contratos.Gateways.SAP;

public interface IConsultaClienteSapGateway
{
    Task<ResultadoConsultaSap<ParceiroNegocioConsultado>> ConsultarAsync(string businessPartner, CancellationToken cancellationToken);
}
