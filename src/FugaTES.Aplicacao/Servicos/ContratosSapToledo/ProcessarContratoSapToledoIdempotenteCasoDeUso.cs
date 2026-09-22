using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
namespace FugaTES.Aplicacao.Servicos.ContratosSapToledo;
public enum ResultadoContratoSapToledo { Concluida, IdempotenteConcluida, ValidacaoConhecida, ResultadoIndeterminado, ClaimBloqueado, ProcessamentoEmAndamento, FalhaTecnica }
public sealed class ProcessarContratoSapToledoIdempotenteCasoDeUso(ContratoSapToledoCanonicalizer canonicalizer,IContratoSapToledoVersaoStore store,IMotorIntegracaoRepository repository,MotorIntegracaoServico motor)
{
    public async Task<ResultadoContratoSapToledo> ExecutarAsync(ContratoSapToledoSnapshot snapshot,string correlationId,CancellationToken ct)
    {
        ContratoSapToledoCanonico canonical; try{canonical=canonicalizer.Canonicalizar(snapshot);}catch(ArgumentException){return ResultadoContratoSapToledo.ValidacaoConhecida;}
        var version=await store.RegistrarOuObterAsync(canonical.Snapshot,canonical.FingerprintSha256,canonical.PayloadCanonicoJson,correlationId,ct); var integration=await repository.ObterPorIdAsync(version.IntegracaoId,ct); if(integration is null)return ResultadoContratoSapToledo.FalhaTecnica;
        if(integration.Estado==EstadoIntegracao.Concluida)return version.Resultado==ResultadoRegistroVersaoContrato.JaExistente?ResultadoContratoSapToledo.IdempotenteConcluida:ResultadoContratoSapToledo.Concluida;
        if(integration.Estado==EstadoIntegracao.ResultadoIndeterminado)return ResultadoContratoSapToledo.ResultadoIndeterminado;
        if(integration.Estado==EstadoIntegracao.EmProcessamento)return ResultadoContratoSapToledo.ProcessamentoEmAndamento;
        if(await store.TemPredecessorNaoTerminalAsync(version.NumeroContrato,version.Versao,ct))return ResultadoContratoSapToledo.ClaimBloqueado;
        var result=await motor.ExecutarPorIdAsync(version.IntegracaoId,ct);
        if(result==CicloMotorIntegracaoResultado.Processado){var final=(await repository.ObterPorIdAsync(version.IntegracaoId,ct))?.Estado;return final switch{EstadoIntegracao.Concluida=>ResultadoContratoSapToledo.Concluida,EstadoIntegracao.FalhaDefinitiva=>ResultadoContratoSapToledo.ValidacaoConhecida,_=>ResultadoContratoSapToledo.FalhaTecnica};}
        return result switch { CicloMotorIntegracaoResultado.ResultadoIndeterminado=>ResultadoContratoSapToledo.ResultadoIndeterminado, CicloMotorIntegracaoResultado.ClaimNaoAdquirido=>ResultadoContratoSapToledo.ClaimBloqueado, CicloMotorIntegracaoResultado.ValidacaoReprovada=>ResultadoContratoSapToledo.ValidacaoConhecida, _=>ResultadoContratoSapToledo.FalhaTecnica };
    }
}
