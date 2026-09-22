using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Aplicacao.CasosDeUso.Recebimento;

public sealed class ProcessarCallbackGuardianMvpCasoDeUso(IRepositorioCorrelacaoRecebimento correlacoes, IPesagemSapMvpGateway sap)
{
    public async Task<ResultadoCallbackGuardianMvp> ExecutarAsync(CallbackGuardianMvp callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (new[] { callback.ProtocoloGuardian, callback.Sequencial, callback.Estado }.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Required callback field is absent.");
        if (callback.PesoBruto < 0 || callback.Tara < 0 || callback.PesoLiquido < 0) throw new ArgumentException("Callback weights are invalid.");
        var contexto = await correlacoes.ObterPorProtocoloAsync(callback.ProtocoloGuardian, cancellationToken) ?? throw new InvalidOperationException("Guardian callback correlation was not found.");
        var contratoAcm = contexto.Entrada.ContratoAcmSap;
        if (string.IsNullOrWhiteSpace(contratoAcm)) throw new InvalidOperationException("ACM contract is absent.");
        var registro = await correlacoes.RegistrarCallbackAsync(callback, cancellationToken);
        if (registro.Status == StatusRegistroCallbackMvp.Conflito) throw new InvalidOperationException("Conflicting callback was rejected.");
        if (registro.Status == StatusRegistroCallbackMvp.Idempotente) return new(StatusCallbackGuardianMvp.Idempotente, null);
        var entrada = registro.Correlacao.Entrada;
        var pesagem = new PesagemSapMvp("Toledo", entrada.Centro, "E", callback.Sequencial, 1, entrada.BusinessPartnerTransportadora, entrada.Material, entrada.BusinessPartnerEmissor, callback.PesoBruto, callback.Tara, callback.PesoLiquido, entrada.TipoDocumento, entrada.PedidoCompras, entrada.DocumentoLogisticoReferencia, entrada.TipoOperacaoSap, contratoAcm);
        await sap.EnviarAsync(pesagem, cancellationToken);
        return new(StatusCallbackGuardianMvp.Processado, pesagem);
    }
}
