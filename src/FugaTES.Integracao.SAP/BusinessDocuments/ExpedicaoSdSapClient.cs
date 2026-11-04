using System.Net;
using FugaTES.Integracao.SAP.Http;

namespace FugaTES.Integracao.SAP.BusinessDocuments;

/// <summary>
/// Desfecho de UMA etapa da expedicao. Guarda o CORPO da resposta tambem no sucesso: o SAP
/// responde 200 ao picking com um relatorio que pode estar vazio, e jogar esse corpo fora foi o
/// que nos fez gravar um CONFIRMADO que nao existia.
/// </summary>
public sealed record ExpedicaoSd(
    bool Ok, int HttpStatus, IReadOnlyList<MensagemSap> MensagensSap, string? Corpo = null)
{
    public string Resumo => LeitorErroSapOData.Resumir(MensagensSap);
}

public interface IExpedirRemessaSdSap
{
    Task<ExpedicaoSd> ConfirmarPickingAsync(string numeroRemessa, CancellationToken ct);

    Task<ExpedicaoSd> PostarSaidaAsync(string numeroRemessa, CancellationToken ct);
}

/// <summary>
/// Expedicao da remessa: separacao e saida de mercadoria. As duas sao acoes do servico (function
/// imports), nao entidades - a remessa vai na QUERY, nao na chave, e nao ha corpo.
///
/// QUEM faz cada uma nao e decidido aqui, e sim em <c>EscopoExpedicaoSd</c>. Em 02/11/2026 isso
/// mudou duas vezes no mesmo dia: primeiro as duas pareciam ser do SAP, depois o operador
/// confirmou que em casca e fibra a separacao e a saida sao do FugaTES, e a saida e o ultimo
/// passo daquele fluxo. Nos demais setores nenhuma das duas existe.
///
/// A SAIDA MOVE ESTOQUE e gera documento de material. Este cliente nao decide quando faze-la - so
/// a executa quando mandam, uma vez por chamada. Quem controla a unicidade e o registro em
/// remessa_sd, antes de chegar aqui.
///
/// O documento da Fuga escreve a rota do picking com duas barras (";v=2//ConfirmPicking...").
/// Aqui vai uma so: barra dupla no caminho e aceita por alguns servidores e recusada por outros,
/// e nao ha motivo para depender disso.
/// </summary>
public sealed class ExpedicaoSdSapClient(SapODataHttpExecutor executor) : IExpedirRemessaSdSap
{
    public const string Servico = "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/";
    public const string RotaPicking = Servico + "ConfirmPickingAllItems";
    public const string RotaSaida = Servico + "PostGoodsIssue";

    /// <summary>
    /// Separacao. HTTP 200 aqui NAO quer dizer que separou: o SAP responde com um PickingReport,
    /// e a lista vem vazia quando ele nao conseguiu separar nada.
    /// </summary>
    public async Task<ExpedicaoSd> ConfirmarPickingAsync(string numeroRemessa, CancellationToken ct)
    {
        var desfecho = await ExecutarAsync(RotaPicking, numeroRemessa, ct).ConfigureAwait(false);
        if (!desfecho.Ok) return desfecho;

        var separados = LerPickingReport(desfecho.Corpo);
        return separados > 0
            ? desfecho with
            {
                MensagensSap = [new MensagemSap("SD_PICKING_ITENS", "info",
                    $"{separados} item(ns) no PickingReport.")]
            }
            // Sem isto o 200 vira CONFIRMADO e tranca o passo com uma separacao que nao existe.
            // Foi o que aconteceu na remessa 80003869, em 02/11/2026: 200 OK, "results": [],
            // picking zerado no VL03N e a saida recusada depois com VU/010 por documento
            // incompleto. O SAP nao mentiu - nos e que nao lemos a resposta.
            : desfecho with
            {
                Ok = false,
                MensagensSap = [new MensagemSap("SD_PICKING_SEM_EFEITO", "error",
                    "O SAP respondeu HTTP 200 com o PickingReport vazio: nenhum item foi separado. "
                    + "Confira saldo, depósito e lote do item antes de repetir.")]
            };
    }

    public Task<ExpedicaoSd> PostarSaidaAsync(string numeroRemessa, CancellationToken ct)
        => ExecutarAsync(RotaSaida, numeroRemessa, ct);

    /// <summary>
    /// Quantos itens o SAP relata ter separado. Corpo que nao e o relatorio esperado conta como
    /// zero: aqui o silencio nao pode ser lido como sucesso.
    /// </summary>
    public static int LerPickingReport(string? corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(corpo);
            return doc.RootElement.TryGetProperty("d", out var d)
                && d.TryGetProperty("results", out var results)
                && results.ValueKind == System.Text.Json.JsonValueKind.Array
                ? results.GetArrayLength()
                : 0;
        }
        catch (System.Text.Json.JsonException) { return 0; }
    }

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
        return new(ok, status, ok ? [] : LeitorErroSapOData.Ler(resposta.Body), resposta.Body);
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
