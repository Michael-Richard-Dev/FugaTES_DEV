using FugaTES.Aplicacao.Modelos.Recebimento;
namespace FugaTES.Aplicacao.Contratos.Recebimento;

public interface IRegistroPesagemToledoStore
{
    Task<bool> JaRecebidoAsync(PesagemToledoRecebida documento,string fingerprint,CancellationToken ct)=>Task.FromResult(false);
    // Return only after a confirmed transaction COMMIT; divergent replay must conflict.
    Task<ReciboPesagemToledo> RegistrarAsync(PesagemToledoRecebida documento,string fingerprint,CancellationToken ct);
}
