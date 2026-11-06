using System.Net;
using System.Text;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.HostIntegracao;
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

            // $batch: UM changeset, UMA transacao. Todas as partes sao conferidas contra a MESMA
            // versao corrente - e so no fim, se todas passarem, a versao muda uma vez. E isso que
            // faz o ETag nao envelhecer no meio do lote.
            //
            // Atomico de verdade: se UMA parte falha, NENHUMA e gravada e a versao nao muda.
            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("$batch", StringComparison.Ordinal))
            {
                return Task.FromResult(Lote(request));
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

        /// <summary>Quantas vezes o lote foi enviado - a retentativa de trava aparece aqui.</summary>
        public int Lotes { get; private set; }

        /// <summary>Recusar o primeiro lote com a trava de 06/11/2026, para exercitar a insistencia.</summary>
        public bool TravarUmaVez { get; set; }

        private HttpResponseMessage Lote(HttpRequestMessage request)
        {
            Lotes++;
            var corpo = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

            if (TravarUmaVez && Lotes == 1)
            {
                Respostas.Add(HttpStatusCode.InternalServerError);
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent(
                        "--b\r\nContent-Type: application/http\r\n\r\nHTTP/1.1 500 Internal Server Error\r\n"
                        + "Content-Type: application/json\r\n\r\n"
                        + """{"error":{"code":"LE_SHP_ODATA_API_OD/003","message":{"value":"The delivery is currently being processed by user FUGATES in system EVVBLKE"}}}"""
                        + "\r\n--b--\r\n", Encoding.UTF8, "multipart/mixed")
                };
            }

            // Confere TODAS as partes contra a versao corrente antes de gravar qualquer uma.
            var pedidos = new List<string>();
            var linhas = corpo.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            string? alvoAtual = null;
            foreach (var bruta in linhas)
            {
                var linha = bruta.Trim();
                if (linha.StartsWith("PATCH ", StringComparison.Ordinal))
                {
                    alvoAtual = Chave(linha);
                    continue;
                }
                if (alvoAtual is null || !linha.StartsWith("If-Match:", StringComparison.OrdinalIgnoreCase)) continue;

                var enviado = linha["If-Match:".Length..].Trim();
                if (!string.Equals(enviado, Etag(alvoAtual), StringComparison.Ordinal))
                {
                    // Changeset recusado: NADA e gravado e a versao NAO muda.
                    Respostas.Add(HttpStatusCode.PreconditionFailed);
                    return new HttpResponseMessage(HttpStatusCode.Accepted)
                    {
                        Content = new StringContent(
                            "--b\r\nContent-Type: application/http\r\n\r\nHTTP/1.1 412 Precondition Failed\r\n"
                            + "Content-Type: application/json\r\n\r\n"
                            + """{"error":{"message":{"value":"State of the resource (entity) was already changed (If-Match)"}}}"""
                            + "\r\n--b--\r\n", Encoding.UTF8, "multipart/mixed")
                    };
                }
                pedidos.Add(alvoAtual);
                alvoAtual = null;
            }

            foreach (var p in pedidos) Gravados.Add(p);
            // UMA transacao: a versao do documento muda uma vez so, no commit.
            if (pedidos.Count > 0) versao++;
            Respostas.Add(HttpStatusCode.Accepted);

            var partes = new StringBuilder();
            foreach (var _ in pedidos)
                partes.Append("--b\r\nContent-Type: application/http\r\n\r\nHTTP/1.1 204 No Content\r\n\r\n\r\n");
            partes.Append("--b--\r\n");
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            { Content = new StringContent(partes.ToString(), Encoding.UTF8, "multipart/mixed") };
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

    /// <summary>
    /// O cliente montado COMO EM PRODUCAO: atras da guarda de rota do motor.
    ///
    /// Ate 06/11/2026 estes testes usavam um HttpClient cru, e foi por isso que o $batch passou
    /// neles e teria sido recusado na VM: a guarda e fail-closed e a rota do lote nao estava na
    /// lista. Ela fez o trabalho dela - os testes e que nao passavam por ela.
    /// </summary>
    private static (AjusteRemessaSdSapClient Cliente, SapComEtag Sap, HttpClient Http) Montar()
    {
        var sap = new SapComEtag();
        var http = new HttpClient(new MotorSdSapGuard(sap, "https://sap.invalid"));
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
    /// UM LOTE, nao N. Os itens vao numa transacao so, e nao ha releitura entre eles - ela era o
    /// contorno do 412 e deixou de ser necessaria, porque dentro do changeset nada e aceito ate o
    /// commit e o ETag nao envelhece.
    ///
    /// A unica releitura que resta e a de SEMPRE: a do cabecalho, que vem depois do lote e por
    /// isso precisa da versao nova.
    /// </summary>
    [Fact]
    public async Task Os_itens_vao_num_lote_so_e_sem_releitura_entre_eles()
    {
        var (cliente, sap, http) = Montar();
        using var _ = http;

        var lida = await cliente.LerAsync(Remessa, CancellationToken.None);
        var antes = sap.Leituras;

        var etapas = await cliente.AjustarAsync(lida!, Cabecalho(), zerarItens: true, CancellationToken.None);

        Assert.All(etapas, e => Assert.True(e.Ok, $"{e.Etapa} recusada"));
        Assert.Equal(1, sap.Lotes);
        // So a releitura do cabecalho. Com dois itens, o desenho antigo fazia duas.
        Assert.Equal(1, sap.Leituras - antes);
    }

    /// <summary>
    /// A remessa de UM item - todos os outros setores - tem de continuar barata: um lote, uma
    /// releitura, um PATCH no cabecalho. Corrigir o caso de dois nao pode encarecer o caso de um.
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
                // exercitar mais uma parte no changeset.
                lida.Itens[1]
            ]
        };

        var etapas = await cliente.AjustarAsync(tres, Cabecalho(), zerarItens: true, CancellationToken.None);

        Assert.All(etapas, e => Assert.True(e.Ok, $"{e.Etapa} recusada"));
        Assert.DoesNotContain(HttpStatusCode.PreconditionFailed, sap.Respostas);
        Assert.Equal(1, sap.Lotes);
    }

    /// <summary>
    /// A REMESSA 80003934, de 06/11/2026. O SAP recusa o lote com a trava - "the delivery is
    /// currently being processed by user FUGATES" -, e duas coisas tem de acontecer:
    ///
    ///   1. NADA e gravado. Antes, o item 000010 ficava zerado e o 000020 nao, e a remessa ficava
    ///      num estado que ninguem pediu;
    ///   2. a trava e reconhecida como transitoria e o lote e reenviado. Era isso que faltava: o
    ///      codigo so conhecia a forma MC/601, entao nao houve UMA tentativa sequer.
    /// </summary>
    [Fact]
    public async Task Trava_no_lote_e_reconhecida_e_o_lote_e_reenviado_inteiro()
    {
        var (cliente, sap, http) = Montar();
        using var _ = http;
        sap.TravarUmaVez = true;

        var lida = await cliente.LerAsync(Remessa, CancellationToken.None);
        var etapas = await cliente.AjustarAsync(lida!, Cabecalho(), zerarItens: true, CancellationToken.None);

        Assert.All(etapas, e => Assert.True(e.Ok, $"{e.Etapa} recusada"));
        Assert.Equal(2, sap.Lotes);                      // insistiu uma vez
        Assert.Equal(["000010", "000020", "cabecalho"], sap.Gravados);   // e gravou uma vez so
    }

    /// <summary>
    /// Changeset recusado deixa tudo como estava. Com o lote mandando ETag velho, o SAP falso
    /// recusa - e nenhum item pode constar como gravado.
    /// </summary>
    [Fact]
    public async Task Lote_recusado_nao_grava_item_nenhum()
    {
        var (cliente, sap, http) = Montar();
        using var _ = http;

        var lida = await cliente.LerAsync(Remessa, CancellationToken.None);
        // ETag de outra versao: o lote inteiro vai ser recusado.
        var velha = lida! with
        {
            Itens = [lida.Itens[0] with { ETag = "W/\"99-000010\"" }, lida.Itens[1]]
        };

        var etapas = await cliente.AjustarAsync(velha, Cabecalho(), zerarItens: true, CancellationToken.None);

        Assert.All(etapas, e => Assert.False(e.Ok));
        Assert.Empty(sap.Gravados);          // nem o 000020, que tinha o ETag certo
        Assert.DoesNotContain("cabecalho", sap.Gravados);
    }
}
