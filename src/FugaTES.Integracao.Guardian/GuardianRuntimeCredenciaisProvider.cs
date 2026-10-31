using FugaTES.Aplicacao.Contratos.Recebimento;
using Microsoft.Extensions.Configuration;

namespace FugaTES.Integracao.Guardian;

public sealed class GuardianRuntimeCredenciaisProvider(IConfiguration configuration) : ICredenciaisGuardianSet26Provider
{
    public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var usuario = configuration["Guardian:Email"];
        var senha = configuration["Guardian:Password"];
        var assinatura = configuration["Guardian:Signature"];
        if (string.IsNullOrWhiteSpace(usuario) || string.IsNullOrWhiteSpace(senha) || string.IsNullOrWhiteSpace(assinatura))
            throw new InvalidOperationException("Guardian runtime credentials are absent.");
        return Task.FromResult(new CredenciaisGuardianSet26(usuario, senha, assinatura));
    }
}
