using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;

namespace FugaTES.Aplicacao.CasosDeUso.Toledo;

public sealed class EnviarContratoToledoCasoDeUso
{
    private readonly IContratoToledoGateway contratoToledoGateway;

    public EnviarContratoToledoCasoDeUso(IContratoToledoGateway contratoToledoGateway)
    {
        this.contratoToledoGateway = contratoToledoGateway ?? throw new ArgumentNullException(nameof(contratoToledoGateway));
    }

    public Task<ResultadoOperacaoToledo<ContratoToledoEnviado>> ExecutarAsync(ContratoToledo contrato, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contrato);
        return contratoToledoGateway.EnviarAsync(contrato, cancellationToken);
    }

    public async Task<IReadOnlyList<ResultadoOperacaoToledo<ContratoToledoEnviado>>> ExecutarLoteAsync(
        IReadOnlyCollection<ContratoToledo> contratos,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contratos);
        if (contratos.Count == 0) throw new ArgumentException("At least one contract is required.", nameof(contratos));

        var resultados = new List<ResultadoOperacaoToledo<ContratoToledoEnviado>>(contratos.Count);
        foreach (var contrato in contratos)
        {
            ArgumentNullException.ThrowIfNull(contrato);
            resultados.Add(await ExecutarAsync(contrato, cancellationToken).ConfigureAwait(false));
        }

        return resultados;
    }
}
