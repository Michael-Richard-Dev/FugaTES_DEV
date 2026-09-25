using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Infraestrutura.MotorIntegracao;

namespace FugaTES.Infraestrutura.Recebimento;

public sealed class RegistroCallbackPesagemSapMemoria(
    RepositorioCorrelacaoRecebimentoMemoria correlacoes,
    RepositorioMotorIntegracaoFake motor) : IRegistroCallbackPesagemSapStore
{
    public Task<ResultadoRegistroCallbackMvp> RegistrarEEnfileirarAsync(
        CallbackGuardianMvp callback, CandidatoIntegracao candidato, CancellationToken cancellationToken) =>
        correlacoes.RegistrarCallbackEEnfileirarAsync(callback, candidato, motor, cancellationToken);
}
