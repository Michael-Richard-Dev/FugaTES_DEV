using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Infraestrutura.Recebimento;

public sealed class PesagemSapMvpGatewayFake : IPesagemSapMvpGateway
{
    private readonly List<PesagemSapMvp> recebidas = [];
    public int QuantidadeChamadas => recebidas.Count;
    public IReadOnlyCollection<PesagemSapMvp> Recebidas => recebidas;
    public PesagemSapMvp? UltimaRecebida => recebidas.LastOrDefault();
    public Task EnviarAsync(PesagemSapMvp pesagem, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); recebidas.Add(pesagem ?? throw new ArgumentNullException(nameof(pesagem))); return Task.CompletedTask; }
}
