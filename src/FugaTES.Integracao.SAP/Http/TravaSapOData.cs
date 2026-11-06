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
    /// As formas conhecidas de o SAP dizer "este documento esta ocupado agora".
    ///
    /// Sao DUAS mensagens diferentes para a mesma coisa, e isso custou a remessa 80003934 em
    /// 06/11/2026: o enqueue generico fala "currently locked by user FUGATES" (MC/601), mas o
    /// servico da remessa fala "The delivery is currently being processed by user FUGATES"
    /// (LE_SHP_ODATA_API_OD/003). Reconheciamos so a primeira, entao a segunda passou como recusa
    /// definitiva, sem UMA tentativa sequer - e o ajuste parou no meio.
    ///
    /// Comparar por trecho de texto e fragil, e aqui e deliberado: o codigo do servico varia por
    /// API e nao da para enumerar todos de antemao. O trecho escolhido e o que descreve a
    /// natureza - ocupado AGORA, por alguem -, nao o verbo de uma API.
    /// </summary>
    private static readonly string[] TrechosDeTrava =
    [
        "currently locked",             // MC/601, enqueue generico
        "currently being processed",    // LE_SHP_ODATA_API_OD/003, remessa
        "is locked by user"
    ];

    private static readonly string[] CodigosDeTrava = ["MC/601", "LE_SHP_ODATA_API_OD/003"];

    /// <summary>
    /// Se a recusa e trava. Pelo CODIGO quando ele e conhecido, e pelo TEXTO quando nao e: a
    /// distincao entre transitorio e definitivo e o que autoriza insistir, e errar para o lado de
    /// nao insistir deixa a remessa pela metade.
    /// </summary>
    public static bool EhTrava(IReadOnlyList<MensagemSap>? mensagens)
        => mensagens is not null
           && mensagens.Any(m =>
               CodigosDeTrava.Any(c => string.Equals(m.Codigo, c, StringComparison.OrdinalIgnoreCase))
               || TrechosDeTrava.Any(t => m.Texto.Contains(t, StringComparison.OrdinalIgnoreCase)));

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
