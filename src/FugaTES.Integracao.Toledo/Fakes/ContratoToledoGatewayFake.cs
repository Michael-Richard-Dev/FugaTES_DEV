using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;
using FugaTES.Integracao.Toledo.Dtos.Contrato;

namespace FugaTES.Integracao.Toledo.Fakes;

public sealed class ContratoToledoGatewayFake : IContratoToledoGateway
{
    private readonly StatusOperacaoToledo? falhaConfigurada;
    private readonly List<ContratoToledo> contratosRecebidos = [];
    private readonly List<string> requestsJsonRecebidos = [];

    public ContratoToledoGatewayFake(StatusOperacaoToledo? falhaConfigurada = null)
    {
        this.falhaConfigurada = falhaConfigurada;
    }

    public int QuantidadeChamadas => contratosRecebidos.Count;

    public IReadOnlyCollection<ContratoToledo> ContratosRecebidos => contratosRecebidos;

    public IReadOnlyCollection<string> RequestsJsonRecebidos => requestsJsonRecebidos;

    public ContratoToledo? UltimoContratoRecebido => contratosRecebidos.LastOrDefault();

    public string? UltimoRequestJsonRecebido => requestsJsonRecebidos.LastOrDefault();

    public string CapturarRequestDocumental(IReadOnlyCollection<ContratoToledo> contratos)
    {
        ArgumentNullException.ThrowIfNull(contratos);
        var json = ContratoToledoRequestDto.Criar(contratos).Serializar();
        requestsJsonRecebidos.Add(json);
        return json;
    }

    public Task<ResultadoOperacaoToledo<ContratoToledoEnviado>> EnviarAsync(ContratoToledo contrato, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contrato);
        contratosRecebidos.Add(contrato);
        requestsJsonRecebidos.Add(ContratoToledoRequestDto.Criar(contrato).Serializar());

        if (falhaConfigurada is not null)
        {
            return Task.FromResult(ResultadoOperacaoToledo<ContratoToledoEnviado>.Falha(falhaConfigurada.Value));
        }

        return Task.FromResult(ResultadoOperacaoToledo<ContratoToledoEnviado>.Sucesso(new ContratoToledoEnviado(contrato)));
    }
}
