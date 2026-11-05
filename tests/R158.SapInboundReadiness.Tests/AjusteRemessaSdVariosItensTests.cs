using System.Net;
using System.Text;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.Integracao.SAP.Http;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// Ajuste de remessa com MAIS DE UM item.
///
/// Reproduz a remessa 80003910, de 05/11/2026 - a primeira criada com duas linhas da ordem depois
/// que o biodiesel passou a incluir as linhas em aberto. O ajuste foi recusado:
///
///   item 000010: HTTP 204 ok
///   item 000020: HTTP 412 - State of the resource (entity) was already changed (If-Match)
///
/// Gravar um item muda a versao do DOCUMENTO, e com ela o ETag dos OUTROS itens. O ETag lido antes
/// do laco vale para o primeiro PATCH e envelhece assim que ele e aceito. O codigo ja evitava esse
/// mesmo 412 para o cabecalho, relendo a remessa antes de grava-lo; faltava estender a releitura
/// ao laco dos itens.
///
/// O SAP falso aqui exige o ETag corrente, como o de verdade: com o ETag velho ele devolve 412.
/// Sem essa exigencia o teste passaria com ou sem a correcao, e nao provaria nada.
/// </summary>
public sealed class AjusteRemessaSdVariosItensTests
{
    private const string Remessa = "80003910";

    /// <summary>
    /// SAP falso com controle de versao. Cada PATCH aceito incrementa a versao do documento, e os
    /// ETags de TODOS os itens passam a refletir a versao nova - que e o comportamento que
    /// produziu o 412 real.
    /// </summary>
    private sealed class SapComEtag : HttpMessageHandler
    {
        private int versao = 1;
        public int Leituras { get; private set; }
        public List<string> Gravados { get; } = [];
        public List<HttpStatusCode> Respostas { get; } = [];

        private string Etag(string item) => $"W/\"{versao}-{item}\"";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Busca do token CSRF: um GET com o cabecalho de fetch.
            if (request.Method == HttpMethod.Get && request.Headers.Contains("X-CSRF-Token"))
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK);
                r.Headers.Add("X-CSRF-Token", "TOKEN");
                r.Content = new StringContent(Corpo(), Encoding.UTF8, "application/json");
                return Task.FromResult(r);
            }

            if (request.Method == HttpMethod.Get)
            {
                Leituras++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(Corpo(), Encoding.UTF8, "application/json") });
            }

            // PATCH: so aceita quem mandar o ETag da versao corrente.
            var alvo = request.RequestUri!.AbsolutePath.Contains("A_OutbDeliveryItem", StringComparison.Ordinal)
                ? Chave(request.RequestUri.AbsolutePath) : "cabecalho";
            // Cru: o executor adiciona o If-Match com TryAddWithoutValidation, e a leitura
            // tipada pode nao reconhecer o valor.
            var enviado = request.Headers.TryGetValues("If-Match", out var v) ? v.FirstOrDefault() : null;
            var esperado = alvo == "cabecalho" ? Etag("H") : Etag(alvo);

            if (!string.Equals(enviado, esperado, StringComparison.Ordinal))
            {
                Respostas.Add(HttpStatusCode.PreconditionFailed);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
                {
                    Content = new StringContent(
                        """{"error":{"message":{"value":"State of the resource (entity) was already changed (If-Match)"}}}""",
                        Encoding.UTF8, "application/json")
                });
            }

            Gravados.Add(alvo);
            versao++;   // a gravacao muda a versao do documento INTEIRO
            Respostas.Add(HttpStatusCode.NoContent);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        private static string Chave(string caminho)
        {
            var marca = "DeliveryDocumentItem='";
            var i = caminho.IndexOf(marca, StringComparison.Ordinal) + marca.Length;
            return caminho[i..caminho.IndexOf('\'', i)];
        }

        // Concatenacao em vez de string bruta interpolada: o JSON tem "}}" colado, que colide com
        // o delimitador de interpolacao.
        // O ETag de verdade leva aspas (W/"1-000010"); dentro do JSON elas vao escapadas.
        private string EtagJson(string chave) => Etag(chave).Replace("\"", "\\\"");

        private string Item(string chave) =>
            "{\"DeliveryDocument\":\"" + Remessa + "\",\"DeliveryDocumentItem\":\"" + chave
            + "\",\"__metadata\":{\"etag\":\"" + EtagJson(chave) + "\"}}";

        private string Corpo() =>
            "{\"d\":{\"DeliveryDocument\":\"" + Remessa + "\",\"__metadata\":{\"etag\":\""
            + EtagJson("H") + "\"},\"to_DeliveryDocumentItem\":{\"results\":["
            + Item("000010") + "," + Item("000020") + "]}}}";
    }

    private sealed class AuthFake : IAutenticacaoSap
    {
        public Task AplicarAsync(HttpRequestMessage request, CancellationToken ct)
        { request.Headers.Authorization = new("Basic", "TESTE"); return Task.CompletedTask; }
    }

    private static (AjusteRemessaSdSapClient Cliente, SapComEtag Sap, HttpClient Http) Montar()
    {
        var sap = new SapComEtag();
        var http = new HttpClient(sap);
        var executor = new SapODataHttpExecutor(http,
            new() { BaseUrl = new("https://sap.invalid"), SapClient = "110", Timeout = TimeSpan.FromSeconds(5) },
            new AuthFake(), new CookieContainer());
        return (new AjusteRemessaSdSapClient(executor), sap, http);
    }

    private static AjusteCabecalhoRemessaSdRequest Cabecalho() =>
        MapeadorAjusteRemessaSd.MontarCabecalho(SetorAtividadeSd.Biodiesel, 56100m, 36600m, "JAK3F79");

    [Fact]
    public async Task Com_duas_linhas_os_dois_itens_e_o_cabecalho_sao_gravados()
    {
        var (cliente, sap, http) = Montar();
        using var _ = http;

        var lida = await cliente.LerAsync(Remessa, CancellationToken.None);
        Assert.NotNull(lida);
        Assert.Equal(2, lida!.Itens.Count);

        var etapas = await cliente.AjustarAsync(lida, Cabecalho(), zerarItens: true, CancellationToken.None);

        // Nenhuma recusa: era exatamente aqui que o 412 aparecia no item 000020.
        Assert.All(etapas, e => Assert.True(e.Ok, $"{e.Etapa} recusada"));
        Assert.DoesNotContain(HttpStatusCode.PreconditionFailed, sap.Respostas);
        Assert.Equal(["000010", "000020", "cabecalho"], sap.Gravados);
    }

    /// <summary>
    /// A remessa de UM item - todos os outros setores - tem de continuar fazendo exatamente as
    /// mesmas chamadas de antes: um PATCH no item, uma releitura, um PATCH no cabecalho. Nenhum
    /// GET a mais. Corrigir o caso de dois nao pode encarecer o caso de um.
    /// </summary>
    [Fact]
    public async Task Com_uma_linha_nao_ha_releitura_a_mais()
    {
        var (cliente, sap, http) = Montar();
        using var _ = http;

        var lida = await cliente.LerAsync(Remessa, CancellationToken.None);
        var umItem = lida! with { Itens = [lida.Itens[0]] };
        var antes = sap.Leituras;

        var etapas = await cliente.AjustarAsync(umItem, Cabecalho(), zerarItens: true, CancellationToken.None);

        Assert.All(etapas, e => Assert.True(e.Ok));
        // So a releitura que ja existia, antes do cabecalho.
        Assert.Equal(1, sap.Leituras - antes);
        Assert.Equal(["000010", "cabecalho"], sap.Gravados);
    }

    [Fact]
    public async Task Tres_linhas_tambem_passam()
    {
        var (cliente, sap, http) = Montar();
        using var _ = http;

        var lida = await cliente.LerAsync(Remessa, CancellationToken.None);
        var tres = lida! with
        {
            Itens =
            [
                lida.Itens[0], lida.Itens[1],
                // O SAP falso devolve dois itens; o terceiro reusa a chave do segundo so para
                // exercitar mais uma volta do laco com releitura.
                lida.Itens[1]
            ]
        };

        var etapas = await cliente.AjustarAsync(tres, Cabecalho(), zerarItens: true, CancellationToken.None);
        Assert.All(etapas, e => Assert.True(e.Ok, $"{e.Etapa} recusada"));
        Assert.DoesNotContain(HttpStatusCode.PreconditionFailed, sap.Respostas);
    }
}
