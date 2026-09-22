using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Integracao.SAP.Http;

namespace FugaTES.Integracao.SAP.BusinessPartner;

public sealed class ConsultaClienteSapAdapter : ConsultaParceiroNegocioSapAdapterBase, IConsultaClienteSapGateway
{
    public ConsultaClienteSapAdapter(ClienteHttpParceiroNegocioSap clienteHttp, TimeProvider timeProvider)
        : base(clienteHttp, timeProvider)
    {
    }

    public Task<ResultadoConsultaSap<ParceiroNegocioConsultado>> ConsultarAsync(string businessPartner, CancellationToken cancellationToken)
    {
        return ConsultarAsync(businessPartner, CategoriaParceiroNegocio.Cliente, cancellationToken);
    }
}
