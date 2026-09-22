using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
namespace FugaTES.Aplicacao.Contratos.ContratosSapToledo;
public enum ResultadoRegistroVersaoContrato { Criada, JaExistente }
public sealed record VersaoContratoSapToledo(ResultadoRegistroVersaoContrato Resultado, string NumeroContrato, long Versao, string FingerprintSha256, Guid IntegracaoId, ContratoSapToledoSnapshot Snapshot);
public interface IContratoSapToledoVersaoStore
{
    Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot snapshot, string fingerprintSha256, string payloadCanonicoJson, string correlationId, CancellationToken cancellationToken);
    Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid integracaoId, CancellationToken cancellationToken);
    Task<bool> TemPredecessorNaoTerminalAsync(string numeroContrato, long versao, CancellationToken cancellationToken);
}
