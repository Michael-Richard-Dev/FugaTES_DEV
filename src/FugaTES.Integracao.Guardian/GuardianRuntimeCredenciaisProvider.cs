using FugaTES.Aplicacao.Contratos.Recebimento;
using Microsoft.Extensions.Configuration;

namespace FugaTES.Integracao.Guardian;

public sealed class GuardianRuntimeCredenciaisProvider(IConfiguration configuration) : ICredenciaisGuardianSet26Provider
{
    public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var usuario = configuration["Guardian:Username"];
        var senha = configuration["Guardian:Password"];
        if (string.IsNullOrWhiteSpace(usuario) || string.IsNullOrWhiteSpace(senha))
            throw new InvalidOperationException("Guardian runtime credentials are absent.");
        return Task.FromResult(new CredenciaisGuardianSet26(usuario, senha));
    }
}
