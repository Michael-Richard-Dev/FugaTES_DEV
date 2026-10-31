using System.Net;
using FugaTES.Integracao.Guardian;
using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.WinForms.Sd;

namespace R158.SapInboundReadiness.Tests;

// SD: o que cerca o POST do protocolo - guarda de rota/metodo, leitura do id devolvido e o
// registro durável que impede a mesma linha de virar dois protocolos.
public sealed class ProtocoloSdEnvioTests
{
    private sealed class Contador : HttpMessageHandler
    {
        public int Chamadas { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Chamadas++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }

    private static async Task<(bool Passou, int Chamadas)> Enviar(HttpMethod metodo, string url)
    {
        var contador = new Contador();
        using var client = new HttpClient(new ProtocoloSdHttpGuard(contador));
        using var request = new HttpRequestMessage(metodo, url);
        try { using var r = await client.SendAsync(request); return (true, contador.Chamadas); }
        catch (InvalidOperationException) { return (false, contador.Chamadas); }
    }

    [Theory]
    [InlineData("POST", "https://guardian.cloudprix.com.br/api/v1/Guardian/ProtocoloPreCadastro")]
    [InlineData("POST", "https://guardian.cloudprix.com.br/api/v2/CreateToken")]
    public async Task Rotas_do_protocolo_e_do_token_passam(string metodo, string url)
        => Assert.True((await Enviar(new HttpMethod(metodo), url)).Passou);

    // Nenhuma outra rota do Guardian e alcancavel desta tela - nem contrato, nem emissor, nem item.
    [Theory]
    [InlineData("POST", "https://guardian.cloudprix.com.br/api/v0/Guardian/Emissor")]
    [InlineData("POST", "https://guardian.cloudprix.com.br/api/v0/Guardian/Item")]
    [InlineData("POST", "https://guardian.cloudprix.com.br/api/v0/Guardian/Transportadora")]
    [InlineData("GET", "https://guardian.cloudprix.com.br/api/v1/Guardian/ProtocoloPreCadastro")]
    [InlineData("DELETE", "https://guardian.cloudprix.com.br/api/v1/Guardian/ProtocoloPreCadastro")]
    [InlineData("POST", "https://outro.invalido/api/v1/Guardian/ProtocoloPreCadastro")]
    [InlineData("POST", "http://guardian.cloudprix.com.br/api/v1/Guardian/ProtocoloPreCadastro")]
    public async Task Rota_metodo_ou_origem_fora_do_recorte_nao_sai(string metodo, string url)
    {
        var (passou, chamadas) = await Enviar(new HttpMethod(metodo), url);
        Assert.False(passou);
        Assert.Equal(0, chamadas);
    }

    // O id do protocolo e o que liga a linha ao Guardian depois. Nao achar seria perder o rastro.
    [Theory]
    [InlineData("""{"id":1234}""", 1234L)]
    [InlineData("""{"dadosProtocoloPreCadastro":{"id":99}}""", 99L)]
    [InlineData("""{"data":{"idProtocolo":"777"}}""", 777L)]
    [InlineData("""{"result":{"numeroProtocolo":5}}""", 5L)]
    [InlineData("1234", 1234L)]
    public void Id_do_protocolo_e_lido_das_formas_conhecidas(string corpo, long esperado)
        => Assert.Equal(esperado, GuardianProtocoloSdClient.LerProtocoloId(corpo));

    [Theory]
    [InlineData("")]
    [InlineData("nao e json")]
    [InlineData("""{"mensagem":"ok"}""")]
    public void Resposta_sem_id_nao_inventa_numero(string corpo)
        => Assert.Null(GuardianProtocoloSdClient.LerProtocoloId(corpo));

    // Um clique a mais nao pode virar um lote.
    [Fact]
    public void Limite_de_envios_por_sessao_e_um()
        => Assert.Equal(1, FugaTES.WinForms.FrmOrdensVendaSd.EnviosPermitidosPorSessao);

    // Os estados tem de ser exatamente os que a restricao da tabela 007S1 aceita.
    [Fact]
    public void Estados_sao_os_aceitos_pela_tabela()
    {
        Assert.Equal("CRIADO", EstadoProtocoloSd.Criado);
        Assert.Equal("RECUSADO", EstadoProtocoloSd.Recusado);
        Assert.Equal("DESFECHO_DESCONHECIDO", EstadoProtocoloSd.DesfechoDesconhecido);
    }

    // Regressao contra evidencia real: este e o payload do protocolo 6 (ordem 782, linha 10,
    // criado no Guardian em 31/10/2026) e a impressao digital registrada daquele envio. Se o
    // mapeamento ou a serializacao mudarem, este teste quebra - e tem de quebrar, porque o que
    // sairia deixaria de ser o que foi conferido e aprovado.
    [Fact]
    public void Impressao_do_protocolo_6_continua_a_mesma()
    {
        var linha = new OrdemVendaSdLeitura("3011", "0000000782", "0007000000", "000010",
            "A", "ZBAL", "3000", "0020000184", "01", "0020000733", "{}");
        var pedido = MapeadorProtocoloSd.Montar(linha,
            "https://webhook.site/717543aa-e052-4a2d-95b1-53983c881c1e");
        Assert.Equal("782/10", MapeadorProtocoloSd.ChaveNatural(linha));
        Assert.Equal("F096D7785DE97524554FDE99A754B514A085EC9C4B04ED5F124024A9963EA96A",
            GuardianProtocoloSdClient.Impressao(pedido));
    }
}
