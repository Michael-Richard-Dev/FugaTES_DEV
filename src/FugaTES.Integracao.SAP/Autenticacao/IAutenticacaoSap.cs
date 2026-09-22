namespace FugaTES.Integracao.SAP.Autenticacao;

public interface IAutenticacaoSap
{
    Task AplicarAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}
