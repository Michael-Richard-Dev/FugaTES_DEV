using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;

namespace FugaTES.Aplicacao.CasosDeUso.Toledo;

public sealed class EnviarPreCadastroToledoCasoDeUso
{
    private readonly IPreCadastroToledoGateway preCadastroToledoGateway;

    public EnviarPreCadastroToledoCasoDeUso(IPreCadastroToledoGateway preCadastroToledoGateway)
    {
        this.preCadastroToledoGateway = preCadastroToledoGateway ?? throw new ArgumentNullException(nameof(preCadastroToledoGateway));
    }

    public Task<ResultadoPreCadastroToledo> ExecutarAsync(PreCadastroToledo preCadastro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preCadastro);

        if (string.IsNullOrWhiteSpace(preCadastro.NumeroContratoBase))
        {
            return Task.FromResult(ResultadoPreCadastroToledo.Falha(StatusPreCadastroToledo.FalhaValidacao));
        }

        return preCadastroToledoGateway.EnviarAsync(preCadastro, cancellationToken);
    }
}
