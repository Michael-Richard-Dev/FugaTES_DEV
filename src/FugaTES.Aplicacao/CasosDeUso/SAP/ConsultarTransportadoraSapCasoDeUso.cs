using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;

namespace FugaTES.Aplicacao.CasosDeUso.SAP;

public sealed class ConsultarTransportadoraSapCasoDeUso
{
    private readonly IConsultaTransportadoraSapGateway gateway;

    public ConsultarTransportadoraSapCasoDeUso(IConsultaTransportadoraSapGateway gateway)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    public Task<ResultadoConsultaSap<ParceiroNegocioConsultado>> ExecutarAsync(string businessPartner, CancellationToken cancellationToken)
    {
        ValidarBusinessPartner(businessPartner);
        return gateway.ConsultarAsync(businessPartner, cancellationToken);
    }

    private static void ValidarBusinessPartner(string businessPartner)
    {
        ArgumentNullException.ThrowIfNull(businessPartner);
        if (string.IsNullOrWhiteSpace(businessPartner))
        {
            throw new ArgumentException("BusinessPartner deve ser informado.", nameof(businessPartner));
        }
    }
}
