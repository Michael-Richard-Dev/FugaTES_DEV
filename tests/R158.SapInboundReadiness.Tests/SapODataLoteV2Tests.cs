using System.Net;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.Integracao.SAP.Http;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// O $batch com um changeset: o que montamos e o que entendemos da resposta.
///
/// A remessa 80003934, em 06/11/2026, ficou com o item 000010 zerado e o 000020 nao, porque cada
/// item ia num PATCH proprio e o SAP recusou o segundo dizendo que a remessa estava sendo
/// processada por nos mesmos. Num changeset ha um enqueue e um commit - e, sobretudo, ou todos
/// os itens mudam ou nenhum.
/// </summary>
public sealed class SapODataLoteV2Tests
{
    private static readonly GravacaoLoteSap[] Dois =
    [
        new("item 000010", "A_OutbDeliveryItem(DeliveryDocument='80003934',DeliveryDocumentItem='000010')",
            """{"ActualDeliveryQuantity":"0"}""", "W/\"A\""),
        new("item 000020", "A_OutbDeliveryItem(DeliveryDocument='80003934',DeliveryDocumentItem='000020')",
            """{"ActualDeliveryQuantity":"0"}""", "W/\"B\"")
    ];

    private static int Contar(string texto, string trecho)
    {
        var n = 0;
        for (var i = texto.IndexOf(trecho, StringComparison.Ordinal); i >= 0;
             i = texto.IndexOf(trecho, i + trecho.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string Corpo(IReadOnlyList<GravacaoLoteSap> g)
        => SapODataLoteV2.MontarCorpo(g, "batch_X", "changeset_Y");

    // --- o que vai para o SAP -----------------------------------------------------------------

    [Fact]
    public void As_gravacoes_vao_num_changeset_so()
    {
        var corpo = Corpo(Dois);

        // Um changeset, e nao um por item: e isso que faz uma transacao so.
        Assert.Equal(1, Contar(corpo, "Content-Type: multipart/mixed; boundary=changeset_Y"));
        Assert.Equal(2, Contar(corpo, "PATCH A_OutbDeliveryItem"));
        Assert.EndsWith("--changeset_Y--\r\n--batch_X--\r\n", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public void Cada_parte_leva_a_sua_precondicao()
    {
        var corpo = Corpo(Dois);

        Assert.Contains("If-Match: W/\"A\"", corpo, StringComparison.Ordinal);
        Assert.Contains("If-Match: W/\"B\"", corpo, StringComparison.Ordinal);
    }

    /// <summary>Sem ETag vai "*", a mesma regra do PATCH avulso.</summary>
    [Fact]
    public void Sem_etag_a_precondicao_e_asterisco()
        => Assert.Contains("If-Match: *",
            Corpo([new("item", "A_OutbDeliveryItem(X)", "{}", null)]), StringComparison.Ordinal);

    /// <summary>
    /// O caminho dentro do lote e RELATIVO ao servico. Um caminho absoluto ali faz o SAP recusar
    /// a parte, e o erro nao parece com o que ele e.
    /// </summary>
    [Fact]
    public void O_caminho_da_parte_nao_tem_barra_inicial()
    {
        var relativa = AjusteRemessaSdSapClient.ChaveItemRelativa("80003934", "000020");

        Assert.StartsWith("A_OutbDeliveryItem(", relativa, StringComparison.Ordinal);
        Assert.DoesNotContain("/sap/opu/odata", relativa, StringComparison.Ordinal);
        Assert.Contains("PATCH A_OutbDeliveryItem(DeliveryDocument='80003934'", Corpo(Dois), StringComparison.Ordinal);
    }

    [Fact]
    public void Content_id_numera_as_partes_a_partir_de_um()
    {
        var corpo = Corpo(Dois);

        Assert.Contains("Content-ID: 1", corpo, StringComparison.Ordinal);
        Assert.Contains("Content-ID: 2", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public void Lote_vazio_e_recusado_antes_de_sair()
        => Assert.Throws<ArgumentException>(() => Corpo([]));

    // --- o que entendemos da resposta ---------------------------------------------------------

    private const string RespostaOk = """
        --batch_resp
        Content-Type: multipart/mixed; boundary=changeset_resp

        --changeset_resp
        Content-Type: application/http

        HTTP/1.1 204 No Content
        Content-Length: 0


        --changeset_resp
        Content-Type: application/http

        HTTP/1.1 204 No Content
        Content-Length: 0


        --changeset_resp--
        --batch_resp--
        """;

    [Fact]
    public void Changeset_aceito_marca_as_duas_gravacoes_como_ok()
    {
        var r = SapODataLoteV2.LerRespostas(Dois, HttpStatusCode.Accepted, RespostaOk);

        Assert.Equal(2, r.Count);
        Assert.All(r, x => Assert.True(x.Ok));
        Assert.All(r, x => Assert.Equal(204, x.HttpStatus));
        Assert.Equal("item 000010", r[0].Nome);
        Assert.Equal("item 000020", r[1].Nome);
    }

    /// <summary>
    /// A TRAVA DE 06/11/2026, agora dentro do lote. O SAP recusa o changeset INTEIRO com uma
    /// parte de erro so - e as duas gravacoes voltam recusadas, porque e a verdade: nenhuma foi
    /// aplicada. Antes, o item 000010 ficava zerado e o 000020 nao.
    /// </summary>
    [Fact]
    public void Changeset_recusado_nao_deixa_gravacao_pela_metade()
    {
        const string corpo = """
            --batch_resp
            Content-Type: application/http

            HTTP/1.1 500 Internal Server Error
            Content-Type: application/json

            {"error":{"code":"LE_SHP_ODATA_API_OD/003","message":{"value":"The delivery is currently being processed by user FUGATES in system EVVBLKE"}}}
            --batch_resp--
            """;

        var r = SapODataLoteV2.LerRespostas(Dois, HttpStatusCode.Accepted, corpo);

        Assert.Equal(2, r.Count);
        Assert.All(r, x => Assert.False(x.Ok));
        Assert.All(r, x => Assert.Equal(500, x.HttpStatus));
        // E a mensagem chega inteira, para a trava poder ser reconhecida.
        Assert.True(TravaSapOData.EhTrava(r[0].MensagensSap));
    }

    [Fact]
    public void Post_do_lote_recusado_nao_afirma_gravacao()
    {
        var r = SapODataLoteV2.LerRespostas(Dois, HttpStatusCode.Forbidden, "CSRF token validation failed");

        Assert.All(r, x => Assert.False(x.Ok));
        Assert.All(r, x => Assert.Equal(403, x.HttpStatus));
    }

    /// <summary>
    /// 202 com corpo que nao se entende NAO e sucesso. Dizer "ok" aqui inventaria uma gravacao
    /// que ninguem viu acontecer - e e justamente o tipo de relatorio confiante que custa caro.
    /// </summary>
    [Fact]
    public void Resposta_ilegivel_e_tratada_como_recusa()
    {
        var r = SapODataLoteV2.LerRespostas(Dois, HttpStatusCode.Accepted, "nada reconhecivel aqui");

        Assert.All(r, x => Assert.False(x.Ok));
        Assert.Contains(r[0].MensagensSap, m => m.Codigo == "SAP_LOTE_SEM_RESPOSTA");
    }

    [Fact]
    public void Uma_parte_recusada_entre_duas_reprova_as_duas()
    {
        const string corpo = """
            --batch_resp
            Content-Type: application/http

            HTTP/1.1 412 Precondition Failed
            Content-Type: application/json

            {"error":{"code":"SY/530","message":{"value":"State of the resource was already changed"}}}
            --batch_resp--
            """;

        var r = SapODataLoteV2.LerRespostas(Dois, HttpStatusCode.Accepted, corpo);

        Assert.All(r, x => Assert.False(x.Ok));
        Assert.All(r, x => Assert.Equal(412, x.HttpStatus));
    }
}

/// <summary>
/// O reconhecimento da trava. Em 06/11/2026 a remessa 80003934 parou no meio porque esta
/// conferencia so conhecia a forma MC/601 - a recusa do servico da remessa passou como
/// definitiva e nao houve UMA tentativa sequer.
/// </summary>
public sealed class TravaSapODataTests
{
    private static MensagemSap[] Uma(string codigo, string texto) => [new(codigo, "error", texto)];

    [Theory]
    [InlineData("MC/601", "Requested object EVVBLKE is currently locked by user FUGATES")]
    [InlineData("LE_SHP_ODATA_API_OD/003", "The delivery is currently being processed by user FUGATES in system EVVBLKE")]
    [InlineData("QUALQUER/999", "Document is locked by user FUGATES")]
    public void As_formas_conhecidas_de_trava_sao_reconhecidas(string codigo, string texto)
        => Assert.True(TravaSapOData.EhTrava(Uma(codigo, texto)));

    /// <summary>
    /// O LIMITE: erro de negocio NAO e trava. Insistir nele so repetiria a recusa e martelaria o
    /// SAP - e, pior, esconderia a causa real atras de tres tentativas iguais.
    /// </summary>
    [Theory]
    [InlineData("VL/096", "Ordem incompleta")]
    [InlineData("VL/150", "Sem saldo disponivel")]
    [InlineData("SY/530", "State of the resource (entity) was already changed (If-Match)")]
    public void Erro_de_negocio_nao_e_trava(string codigo, string texto)
        => Assert.False(TravaSapOData.EhTrava(Uma(codigo, texto)));

    [Fact]
    public void Sem_mensagem_nao_e_trava()
    {
        Assert.False(TravaSapOData.EhTrava(null));
        Assert.False(TravaSapOData.EhTrava([]));
    }
}
