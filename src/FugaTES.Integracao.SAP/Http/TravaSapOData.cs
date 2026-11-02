namespace FugaTES.Integracao.SAP.Http;

/// <summary>
/// Trava de enqueue do SAP sobre um documento, e como insistir nela.
///
/// Acontece entre gravacoes proximas no MESMO documento - inclusive entre duas nossas. Em
/// 02/11/2026, na remessa 80003864: o PATCH do item passou e o do cabecalho veio logo atras,
/// antes de o SAP soltar a trava. "MC/601 Requested object EVVBLKE is currently locked by user
/// FUGATES" - somos nos, na requisicao anterior.
///
/// Por que insistir e seguro AQUI e nao em qualquer recusa: a trava e transitoria, e a recusa
/// garante que a gravacao NAO aconteceu. Num erro de negocio - "VL/096 ordem incompleta", "VL/150
/// sem saldo" - insistir so repetiria a recusa e marteria o SAP.
///
/// Com o fluxo rodando sozinho isto deixa de ser detalhe: pela tela ha segundos de intervalo
/// humano entre os passos; o motor encadeia criar, ajustar e gravar o texto sem pausa, sobre o
/// mesmo documento.
/// </summary>
public static class TravaSapOData
{
    public const int Tentativas = 3;

    public static readonly TimeSpan Espera = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Se a recusa e trava. Pelo CODIGO, e nao por "deu 400": a distincao entre transitorio e
    /// definitivo e o que autoriza insistir.
    /// </summary>
    public static bool EhTrava(IReadOnlyList<MensagemSap>? mensagens)
        => mensagens is not null
           && mensagens.Any(m => string.Equals(m.Codigo, "MC/601", StringComparison.OrdinalIgnoreCase)
               || m.Texto.Contains("currently locked", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Executa e insiste enquanto a recusa for trava, ate <see cref="Tentativas"/>. Devolve o
    /// ultimo resultado - inclusive o recusado, quando a trava nao soltar.
    /// </summary>
    public static async Task<T> InsistirSeTravadoAsync<T>(
        Func<CancellationToken, Task<T>> enviar,
        Func<T, bool> concluiu,
        Func<T, IReadOnlyList<MensagemSap>> mensagens,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(enviar);
        ArgumentNullException.ThrowIfNull(concluiu);
        ArgumentNullException.ThrowIfNull(mensagens);

        var tentativa = 0;
        while (true)
        {
            var resultado = await enviar(ct).ConfigureAwait(false);
            if (concluiu(resultado) || ++tentativa >= Tentativas || !EhTrava(mensagens(resultado)))
                return resultado;
            await Task.Delay(Espera, ct).ConfigureAwait(false);
        }
    }
}
