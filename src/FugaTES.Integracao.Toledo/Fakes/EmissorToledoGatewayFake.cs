using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;
using FugaTES.Integracao.Toledo.Dtos.Emissor;

namespace FugaTES.Integracao.Toledo.Fakes;

public sealed class EmissorToledoGatewayFake : IEmissorToledoGateway
{
    private readonly StatusOperacaoToledo? falhaConfigurada;
    private readonly List<EmissorToledo> emissoresRecebidos = [];
    private readonly List<string> requestsJsonRecebidos = [];

    public EmissorToledoGatewayFake(StatusOperacaoToledo? falhaConfigurada = null)
    {
        this.falhaConfigurada = falhaConfigurada;
    }

    public int QuantidadeChamadas => emissoresRecebidos.Count;

    public IReadOnlyCollection<EmissorToledo> EmissoresRecebidos => emissoresRecebidos;

    public IReadOnlyCollection<string> RequestsJsonRecebidos => requestsJsonRecebidos;

    public EmissorToledo? UltimoEmissorRecebido => emissoresRecebidos.LastOrDefault();

    public string? UltimoRequestJsonRecebido => requestsJsonRecebidos.LastOrDefault();

    public Task<ResultadoOperacaoToledo<EmissorToledoRegistrado>> EnviarAsync(EmissorToledo emissor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(emissor);
        emissoresRecebidos.Add(emissor);
        requestsJsonRecebidos.Add(EmissorToledoRequestDto.Criar(emissor).Serializar());

        if (falhaConfigurada is not null)
        {
            return Task.FromResult(ResultadoOperacaoToledo<EmissorToledoRegistrado>.Falha(falhaConfigurada.Value));
        }

        return Task.FromResult(ResultadoOperacaoToledo<EmissorToledoRegistrado>.Sucesso(new EmissorToledoRegistrado(emissor)));
    }
}