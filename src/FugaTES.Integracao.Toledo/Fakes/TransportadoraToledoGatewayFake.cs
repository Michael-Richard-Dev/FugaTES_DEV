using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;
using FugaTES.Integracao.Toledo.Dtos.Transportadora;

namespace FugaTES.Integracao.Toledo.Fakes;

public sealed class TransportadoraToledoGatewayFake : ITransportadoraToledoGateway
{
    private readonly StatusOperacaoToledo? falhaConfigurada;
    private readonly List<TransportadoraToledo> transportadorasRecebidas = [];
    private readonly List<string> requestsJsonRecebidos = [];

    public TransportadoraToledoGatewayFake(StatusOperacaoToledo? falhaConfigurada = null)
    {
        this.falhaConfigurada = falhaConfigurada;
    }

    public int QuantidadeChamadas => transportadorasRecebidas.Count;

    public IReadOnlyCollection<TransportadoraToledo> TransportadorasRecebidas => transportadorasRecebidas;

    public IReadOnlyCollection<string> RequestsJsonRecebidos => requestsJsonRecebidos;

    public TransportadoraToledo? UltimaTransportadoraRecebida => transportadorasRecebidas.LastOrDefault();

    public string? UltimoRequestJsonRecebido => requestsJsonRecebidos.LastOrDefault();

    public Task<ResultadoOperacaoToledo<TransportadoraToledoRegistrada>> EnviarAsync(TransportadoraToledo transportadora, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transportadora);
        transportadorasRecebidas.Add(transportadora);
        requestsJsonRecebidos.Add(TransportadoraToledoRequestDto.Criar(transportadora).Serializar());

        if (falhaConfigurada is not null)
        {
            return Task.FromResult(ResultadoOperacaoToledo<TransportadoraToledoRegistrada>.Falha(falhaConfigurada.Value));
        }

        return Task.FromResult(ResultadoOperacaoToledo<TransportadoraToledoRegistrada>.Sucesso(new TransportadoraToledoRegistrada(transportadora)));
    }
}