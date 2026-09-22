using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using Microsoft.Extensions.Options;

namespace FugaTES.HostIntegracao;

public sealed class PoliticaTimeoutProcessamentoIntegracaoConfigurada : IPoliticaTimeoutProcessamentoIntegracao
{
    private readonly TimeoutProcessamentoIntegracaoOptions options;

    public PoliticaTimeoutProcessamentoIntegracaoConfigurada(IOptions<TimeoutProcessamentoIntegracaoOptions> options)
    {
        this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (!this.options.EhValida())
        {
            throw new ArgumentException("Configuracao invalida para timeout operacional de integracao.", nameof(options));
        }
    }

    public TimeSpan ObterTimeout(ClaimIntegracaoMotor claim)
    {
        return options.Timeout;
    }
}
