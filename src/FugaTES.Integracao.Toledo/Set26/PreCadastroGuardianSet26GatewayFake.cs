using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;

namespace FugaTES.Integracao.Toledo.Set26;

public sealed class PreCadastroGuardianSet26GatewayFake(string protocolo) : IPreCadastroGuardianSet26Gateway
{
    private readonly List<PreCadastroRecebimentoGuardianSet26> recebidos = [];
    public int QuantidadeChamadas => recebidos.Count;
    public IReadOnlyCollection<PreCadastroRecebimentoGuardianSet26> Recebidos => recebidos;

    public Task<ProtocoloGuardianSet26> EnviarAsync(PreCadastroRecebimentoGuardianSet26 preCadastro, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        recebidos.Add(preCadastro ?? throw new ArgumentNullException(nameof(preCadastro)));
        if (string.IsNullOrWhiteSpace(protocolo)) throw new InvalidOperationException("Configured fake protocol is absent.");
        return Task.FromResult(new ProtocoloGuardianSet26(protocolo));
    }
}
