using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;

namespace FugaTES.Integracao.Toledo.Fakes;

public sealed class AutenticacaoToledoGatewayFake : IAutenticacaoToledoGateway
{
    private readonly ResultadoOperacaoToledo<SessaoAutenticacaoToledo> resultado;

    public AutenticacaoToledoGatewayFake(ResultadoOperacaoToledo<SessaoAutenticacaoToledo>? resultado = null)
    {
        this.resultado = resultado ?? ResultadoOperacaoToledo<SessaoAutenticacaoToledo>.Sucesso(new SessaoAutenticacaoToledo("sessao-sintetica"));
    }

    public int QuantidadeChamadas { get; private set; }

    public Task<ResultadoOperacaoToledo<SessaoAutenticacaoToledo>> AutenticarAsync(CancellationToken cancellationToken)
    {
        QuantidadeChamadas++;
        if (resultado.Status == StatusOperacaoToledo.Sucesso && string.IsNullOrWhiteSpace(resultado.Dados?.ValorSintetico))
        {
            return Task.FromResult(ResultadoOperacaoToledo<SessaoAutenticacaoToledo>.Falha(StatusOperacaoToledo.FalhaContrato));
        }

        return Task.FromResult(resultado);
    }
}