using System.Net;
using FugaTES.Integracao.SAP.Http;

namespace FugaTES.Integracao.SAP.BusinessDocuments;

/// <summary>Desfecho de UMA etapa da expedicao.</summary>
public sealed record ExpedicaoSd(
    bool Ok, int HttpStatus, IReadOnlyList<MensagemSap> MensagensSap)
{
    public string Resumo => LeitorErroSapOData.Resumir(MensagensSap);
}

public interface IExpedirRemessaSdSap
{
    Task<ExpedicaoSd> ConfirmarPickingAsync(string numeroRemessa, CancellationToken ct);

}

/// <summary>
/// Separacao de todos os itens da remessa - o picking de casca e fibra.
///
/// E uma acao do servico (function import), nao uma entidade: a remessa vai na QUERY, nao na
/// chave, e nao ha corpo.
///
/// A SAIDA DE MERCADORIA nao esta aqui, de proposito. Ela e feita dentro do SAP, por quem confere
/// o carregamento (ver EscopoExpedicaoSd), e chegou a existir neste arquivo enquanto eu supunha
/// que fosse nossa. Codigo que nao pode rodar nao deve existir: ele passa a impressao de que a
/// capacidade esta disponivel, e um dia alguem a liga sem perceber o que isso significa.
///
/// O documento da Fuga escreve a rota do picking com duas barras (";v=2//ConfirmPicking...").
/// Aqui vai uma so: barra dupla no caminho e aceita por alguns servidores e recusada por outros,
/// e nao ha motivo para depender disso.
/// </summary>
public sealed class ExpedicaoSdSapClient(SapODataHttpExecutor executor) : IExpedirRemessaSdSap
{
    public const string Servico = "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/";
    public const string RotaPicking = Servico + "ConfirmPickingAllItems";
    public Task<ExpedicaoSd> ConfirmarPickingAsync(string numeroRemessa, CancellationToken ct)
        => ExecutarAsync(RotaPicking, numeroRemessa, ct);

    private async Task<ExpedicaoSd> ExecutarAsync(string rota, string numeroRemessa, CancellationToken ct)
    {
        var uri = executor.ConstruirUri(rota, Parametro(numeroRemessa));

        // O fetch do CSRF e um GET, e GET na acao nao e permitido: busca-se o token no cabecalho
        // da propria remessa, que esta na mesma sessao e nos mesmos cookies.
        var token = await executor
            .ObterTokenCsrfAsync(executor.ConstruirUri(AjusteRemessaSdSapClient.ChaveCabecalho(numeroRemessa)), ct)
            .ConfigureAwait(false);

        // If-Match "*": a acao nao tem ETag proprio, e e o que o documento da Fuga especifica.
        var resposta = await executor
            .EnviarComCsrfAsync(HttpMethod.Post, uri, conteudo: null, token, ifMatch: "*", ct)
            .ConfigureAwait(false);

        var status = (int)resposta.Status;
        var ok = resposta.Status is HttpStatusCode.OK or HttpStatusCode.Created
            or HttpStatusCode.NoContent or HttpStatusCode.Accepted;
        return new(ok, status, ok ? [] : LeitorErroSapOData.Ler(resposta.Body));
    }

    /// <summary>
    /// A remessa vai na query, entre aspas simples, como o documento mostra. O numero e conferido
    /// antes: um valor arbitrario aqui mudaria a remessa alvo da acao.
    /// </summary>
    public static string Parametro(string numeroRemessa)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(numeroRemessa);
        var limpo = numeroRemessa.Trim();
        if (limpo.Length > 10 || !limpo.All(char.IsAsciiDigit))
            throw new ArgumentException("SD_EXPEDICAO_CHAVE_INVALIDA", nameof(numeroRemessa));
        return "DeliveryDocument='" + limpo + "'";
    }
}
