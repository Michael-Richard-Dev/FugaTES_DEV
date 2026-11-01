using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Persistencia.Sd;

namespace FugaTES.WinForms.Sd;

/// <summary>
/// Monta a tela de devolutivas. Reusa a mesma conexao de escrita do fluxo SD (cluster 5433),
/// embora aqui so se leia: e a unica rota configurada do desktop ate aquele banco, e a tabela
/// 007S2 concede apenas SELECT ao papel fugates_sd_app.
/// </summary>
public static class DevolutivasSdComposition
{
    public static FrmDevolutivasSd Criar()
    {
        var aberta = ConexaoSdPostgreSql.AbrirAsync(CancellationToken.None).GetAwaiter().GetResult();
        return aberta.Fonte is null
            ? new FrmDevolutivasSd(null, null, aberta.Motivo)
            : new FrmDevolutivasSd(new DevolutivasSdPostgreSql(aberta.Fonte), aberta.Posse);
    }

    /// <summary>
    /// Versao assincrona, para quem abre a tela a partir da interface: abrir a conexao bloqueando
    /// a thread da interface trava o aplicativo enquanto o tunel SSH sobe.
    /// </summary>
    public static async Task<FrmDevolutivasSd> CriarAsync(CancellationToken ct)
    {
        var aberta = await ConexaoSdPostgreSql.AbrirAsync(ct).ConfigureAwait(true);
        return aberta.Fonte is null
            ? new FrmDevolutivasSd(null, null, aberta.Motivo)
            : new FrmDevolutivasSd(new DevolutivasSdPostgreSql(aberta.Fonte), aberta.Posse);
    }
}
