using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Servicos.Mvp;

using FugaTES.Aplicacao.Configuracao;
namespace FugaTES.Aplicacao.CasosDeUso.Recebimento;

public sealed class ProcessarCallbackGuardianMvpCasoDeUso(IRepositorioCorrelacaoRecebimento correlacoes, IRegistroCallbackPesagemSapStore registro)
{
    public async Task<ResultadoCallbackGuardianMvp> ExecutarAsync(CallbackGuardianMvp callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (new[] { callback.ProtocoloGuardian, callback.Sequencial, callback.Estado }.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Required callback field is absent.");
        if (callback.PesoBruto < 0 || callback.Tara < 0 || callback.PesoLiquido < 0) throw new ArgumentException("Callback weights are invalid.");
        var contexto = await correlacoes.ObterPorProtocoloAsync(callback.ProtocoloGuardian, cancellationToken) ?? throw new InvalidOperationException("Guardian callback correlation was not found.");
        if (callback.ProtocoloCloud is not null && !string.Equals(callback.ProtocoloCloud, contexto.ProtocoloGuardian, StringComparison.Ordinal))
            throw new InvalidOperationException("Guardian ProtocoloCloud conflicts with persisted protocol.");
        var contratoAcm = contexto.Entrada.ContratoAcmSap;
        if (string.IsNullOrWhiteSpace(contratoAcm)) throw new InvalidOperationException("ACM contract is absent.");
        if (AcmR106SapReturnPreflight.RequiresAcmGuards(contexto.Entrada))
        {
            if (!CentrosAcm.EhPermitido(contexto.Entrada.Centro) || contexto.Entrada.TipoContrato != "AC3P")
                throw new ArgumentException("ACM_CALLBACK_CONTEXT_INVALID");
            if (callback.Estado != "5") throw new ArgumentException("ACM_FINAL_EVENT_REQUIRED");
            GuardianFinalWeighingPolicy.RequireFinal(callback.PesoBruto, callback.Tara, callback.PesoLiquido);
        }
        var fingerprint = CallbackGuardianMvpFingerprint.Calcular(callback);
        var candidato = new CandidatoIntegracao("GUARDIAN", "SAP", GuardianPesagemSapMotorWork.Operacao,
            callback.ProtocoloGuardian, GuardianPesagemSapMotorWork.Fingerprint(callback.ProtocoloGuardian, fingerprint), contexto.DocumentoSap);
        var resultado = await registro.RegistrarEEnfileirarAsync(callback, candidato, cancellationToken);
        if (resultado.Status == StatusRegistroCallbackMvp.Conflito) throw new InvalidOperationException("Conflicting callback was rejected.");
        return new(resultado.Status == StatusRegistroCallbackMvp.Novo
            ? StatusCallbackGuardianMvp.Aceito : StatusCallbackGuardianMvp.Idempotente, null);
    }
}
