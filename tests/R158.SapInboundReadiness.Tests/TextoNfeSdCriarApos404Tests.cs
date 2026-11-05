using System.Net;
using System.Text;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.Integracao.SAP.Http;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// Remessa 80003917, 05/11/2026: a navegacao to_DeliveryDocumentText devolveu o texto que vem da
/// ordem - com os rotulos vazios -, entao escolhemos PATCH; mas a ENTIDADE por chave ainda nao
/// existia e o SAP respondeu 404 "Resource not found for segment 'A_OutbDeliveryHeaderTextType'".
///
/// A chave estava certa: um GET na MESMA url, depois que alguem salvou o texto a mao, devolveu 200.
/// O erro foi confiar na leitura para decidir entre criar e atualizar. Quem decide e a escrita.
/// </summary>
public sealed class TextoNfeSdCriarApos404Tests
{
    private const string Remessa = "80003917";

    private sealed class SapFake(HttpStatusCode respostaDoPatch) : HttpMessageHandler
    {
        public List<string> Metodos { get; } = [];
        public List<string> Caminhos { get; } = [];
        public string? CorpoDoPost { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get && request.Headers.Contains("X-CSRF-Token"))
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK);
                r.Headers.Add("X-CSRF-Token", "TOKEN");
                return r;
            }
            Metodos.Add(request.Method.Method);
            Caminhos.Add(request.RequestUri!.AbsolutePath);

            if (request.Method == HttpMethod.Patch)
                return new HttpResponseMessage(respostaDoPatch)
                {
                    Content = new StringContent(
                        "{\"error\":{\"message\":{\"value\":\"Resource not found for segment 'A_OutbDeliveryHeaderTextType'\"}}}",
                        Encoding.UTF8, "application/json")
                };

            CorpoDoPost = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    private sealed class AuthFake : IAutenticacaoSap
    {
        public Task AplicarAsync(HttpRequestMessage request, CancellationToken ct)
        { request.Headers.Authorization = new("Basic", "TESTE"); return Task.CompletedTask; }
    }

    private static (TextoNfeSdSapClient Cliente, SapFake Sap, HttpClient Http) Montar(HttpStatusCode patch)
    {
        var sap = new SapFake(patch);
        var http = new HttpClient(sap);
        var executor = new SapODataHttpExecutor(http,
            new() { BaseUrl = new("https://sap.invalid"), SapClient = "110", Timeout = TimeSpan.FromSeconds(5) },
            new AuthFake(), new CookieContainer());
        return (new TextoNfeSdSapClient(executor), sap, http);
    }

    // O texto "existe" pela leitura: e o que leva ao PATCH.
    private static TextoNfeSdAtual Existente() => new("VENDEDOR: BIOFUGA\nLACRES: ", "W/\"1\"");

    [Fact]
    public async Task Patch_com_404_vira_criacao()
    {
        var (cliente, sap, http) = Montar(HttpStatusCode.NotFound);
        using var _ = http;

        var r = await cliente.GravarAsync(Remessa, Existente(), "TEXTO NOVO", CancellationToken.None);

        Assert.True(r.Ok);
        Assert.True(r.Criou);
        Assert.Equal(["PATCH", "POST"], sap.Metodos);
        // O POST vai na NAVEGACAO, e leva elemento e idioma - a criacao precisa dizer qual texto e.
        Assert.EndsWith("/to_DeliveryDocumentText", sap.Caminhos[1], StringComparison.Ordinal);
        Assert.Contains("ZNFE", sap.CorpoDoPost!);
        Assert.Contains("TEXTO NOVO", sap.CorpoDoPost!);
    }

    // So o 404 vira criacao. Outro erro continua sendo erro - inventar um POST em cima de um 412
    // ou de um 500 criaria texto onde o SAP recusou por outro motivo.
    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Outro_erro_no_patch_nao_vira_criacao(HttpStatusCode erro)
    {
        var (cliente, sap, http) = Montar(erro);
        using var _ = http;

        var r = await cliente.GravarAsync(Remessa, Existente(), "TEXTO NOVO", CancellationToken.None);

        Assert.False(r.Ok);
        Assert.False(r.Criou);
        Assert.DoesNotContain("POST", sap.Metodos);
    }

    // Quando a leitura ja diz que nao ha texto, segue o caminho de sempre: um POST so.
    [Fact]
    public async Task Sem_texto_na_leitura_continua_sendo_um_post_unico()
    {
        var (cliente, sap, http) = Montar(HttpStatusCode.NotFound);
        using var _ = http;

        var r = await cliente.GravarAsync(Remessa, new(null, null), "TEXTO NOVO", CancellationToken.None);

        Assert.True(r.Ok);
        Assert.True(r.Criou);
        Assert.Equal(["POST"], sap.Metodos);
    }
}
