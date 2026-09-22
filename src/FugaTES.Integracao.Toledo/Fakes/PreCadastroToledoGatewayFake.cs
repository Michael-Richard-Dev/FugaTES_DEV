using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;
using FugaTES.Integracao.Toledo.Dtos.PreCadastro;
using FugaTES.Integracao.Toledo.Mapeamentos;

namespace FugaTES.Integracao.Toledo.Fakes;

public sealed class PreCadastroToledoGatewayFake : IPreCadastroToledoGateway
{
    private readonly StatusPreCadastroToledo? falhaConfigurada;
    private readonly List<PreCadastroToledo> modelosRecebidos = [];
    private readonly List<PreCadastroToledoRequestDto> dtosGerados = [];
    private readonly List<string> jsonsDocumentais = [];

    public PreCadastroToledoGatewayFake(StatusPreCadastroToledo? falhaConfigurada = null)
    {
        this.falhaConfigurada = falhaConfigurada;
    }

    public int QuantidadeChamadas => modelosRecebidos.Count;

    public IReadOnlyCollection<PreCadastroToledo> ModelosRecebidos => modelosRecebidos;

    public IReadOnlyCollection<PreCadastroToledoRequestDto> DtosGerados => dtosGerados;

    public IReadOnlyCollection<string> JsonsDocumentais => jsonsDocumentais;

    public PreCadastroToledo? UltimoModeloRecebido => modelosRecebidos.LastOrDefault();

    public PreCadastroToledoRequestDto? UltimoDtoGerado => dtosGerados.LastOrDefault();

    public string? UltimoJsonDocumental => jsonsDocumentais.LastOrDefault();

    public Task<ResultadoPreCadastroToledo> EnviarAsync(PreCadastroToledo preCadastro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preCadastro);

        modelosRecebidos.Add(preCadastro);
        var dto = MapeadorPreCadastroToledo.Mapear(preCadastro);
        dtosGerados.Add(dto);
        jsonsDocumentais.Add(dto.Serializar());

        if (falhaConfigurada is not null)
        {
            return Task.FromResult(ResultadoPreCadastroToledo.Falha(falhaConfigurada.Value));
        }

        return Task.FromResult(ResultadoPreCadastroToledo.Sucesso(preCadastro));
    }
}
