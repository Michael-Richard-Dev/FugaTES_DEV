using System.Net.Http;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Infraestrutura.Mm;
using FugaTES.Integracao.SAP.BusinessDocuments;

namespace FugaTES.Testes;

/// <summary>
/// O MM lendo o SAP certo.
///
/// Ate 05/11/2026 a origem e o sap-client estavam escritos dentro do adaptador, a guarda de
/// transporte exigia o host do DEV e o cache recusava qualquer rotulo que nao fosse DEV110. Um MM
/// de QAS120 nao conseguiria ler, e se conseguisse gravaria um cache dizendo DEV110 - o rotulo e
/// justamente o que distingue os dois arquivos.
///
/// O que estes testes fixam e o par: origem E sap-client juntos. A origem do DEV com o cliente do
/// QAS nao e ambiente nenhum, e e esse cruzamento que uma conferencia separada deixa passar.
/// </summary>
public sealed class MmAmbienteTests
{
    private const string Dev = "https://vhfufds4ci.sap.fugacouros.com.br:44300";
    private const string Qas = "https://vhfufqs4ci.sap.fugacouros.com.br:44300";

    private static Uri Leitura(string origem, string? client)
        => new(origem + PedidosCompraMmConsultaAdapter.Rota + "PurchaseOrder"
               + (client is null ? "" : "?sap-client=" + client));

    // ---- a guarda de transporte do servico MM (estatica, usada pelo handler) ----

    [Theory]
    [InlineData(Dev, "110")]
    [InlineData(Qas, "120")]
    [InlineData(Dev, null)]
    [InlineData(Qas, null)]
    public void Guarda_de_transporte_aceita_os_dois_ambientes_aprovados(string origem, string? client)
        => Assert.Equal(Leitura(origem, client), PedidosCompraMmConsultaAdapter.ValidateUri(Leitura(origem, client)));

    [Theory]
    [InlineData("https://outro.sap.fugacouros.com.br:44300", "110")]  // host nao aprovado
    [InlineData("http://vhfufds4ci.sap.fugacouros.com.br:44300", "110")]  // sem TLS
    [InlineData(Dev, "120")]   // par cruzado: origem do DEV, cliente do QAS
    [InlineData(Qas, "110")]   // par cruzado ao contrario
    [InlineData(Dev, "200")]   // cliente que nao e de nenhum ambiente
    public void Guarda_de_transporte_recusa_host_nao_aprovado_e_par_cruzado(string origem, string? client)
        => Assert.Throws<FormatException>(() => PedidosCompraMmConsultaAdapter.ValidateUri(Leitura(origem, client)));

    [Fact]
    public void Guarda_de_transporte_recusa_rota_fora_da_leitura_de_pedidos()
        => Assert.Throws<FormatException>(() =>
            PedidosCompraMmConsultaAdapter.ValidateUri(new Uri(Qas + "/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner")));

    // ---- o adaptador, fixado no ambiente que recebeu ----

    [Fact]
    public async Task Adaptador_de_qas_busca_no_host_do_qas_com_o_cliente_120()
    {
        var transport = new PedidosCompraMmTests.Transport(_ => PedidosCompraMmTests.Page(PedidosCompraMmTests.Header()));
        using var http = new HttpClient(transport);
        var result = await PedidosCompraMmTests.Query(http, null, AmbientesSap.Qas120).ConsultarAsync(default);

        Assert.True(result.Sucesso, result.Codigo);
        var uri = Assert.Single(transport.Uris);
        Assert.Equal(new Uri(Qas).Host, uri.Host);
        Assert.Contains("sap-client=120", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("ds4ci", uri.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sap-client=110", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adaptador_de_dev_continua_buscando_no_host_do_dev()
    {
        var transport = new PedidosCompraMmTests.Transport(_ => PedidosCompraMmTests.Page(PedidosCompraMmTests.Header()));
        using var http = new HttpClient(transport);
        var result = await PedidosCompraMmTests.Query(http, null, AmbientesSap.Dev110).ConsultarAsync(default);

        Assert.True(result.Sucesso, result.Codigo);
        var uri = Assert.Single(transport.Uris);
        Assert.Equal(new Uri(Dev).Host, uri.Host);
        Assert.Contains("sap-client=110", uri.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// O @odata.nextLink vem da resposta do SAP, e por isso e dado - nao instrucao. Um link de
    /// continuacao apontando para o OUTRO ambiente aprovado passaria a guarda de transporte, que
    /// so exige "aprovado"; e o adaptador que recusa, porque metade dos pedidos de um ambiente e
    /// metade do outro daria um resultado com toda a aparencia de inteiro.
    /// </summary>
    /// <remarks>
    /// Os dois casos sao recusados pela guarda do PAR, nao pela conferencia de origem: no segundo,
    /// o link vem sem sap-client, o adaptador acrescenta o SEU (110) e o par QAS+110 nao e ambiente
    /// nenhum. A conferencia de origem em ValidarNoAmbiente continua la de proposito - ela diz a
    /// invariante diretamente, em vez de depender dessa corrente - mas nao da para isola-la num
    /// teste, e este teste nao a cobre. O que ele fixa e o efeito: a segunda pagina nao e buscada.
    /// </remarks>
    [Theory]
    [InlineData(Qas + "/sap/opu/odata4/sap/api_purchaseorder_2/srvd_a2x/sap/purchaseorder/0001/PurchaseOrder?sap-client=120&$skiptoken=2")]
    [InlineData(Qas + "/sap/opu/odata4/sap/api_purchaseorder_2/srvd_a2x/sap/purchaseorder/0001/PurchaseOrder?$skiptoken=2")]
    public async Task Continuacao_apontando_para_o_outro_ambiente_e_recusada(string atravessado)
    {
        var transport = new PedidosCompraMmTests.Transport(_ =>
            "{\"value\":[],\"@odata.nextLink\":\"" + atravessado + "\"}");
        using var http = new HttpClient(transport);

        var result = await PedidosCompraMmTests.Query(http, null, AmbientesSap.Dev110).ConsultarAsync(default);

        Assert.False(result.Sucesso);
        Assert.Equal("MM_SAP_RESPONSE_INVALID", result.Codigo);
        Assert.Single(transport.Uris);   // a segunda pagina nunca foi buscada
    }

    // ---- o cache ----

    [Theory]
    [InlineData(Dev, "110")]
    [InlineData(Qas, "120")]
    public void Cache_aceita_o_rotulo_dos_dois_ambientes(string origem, string client)
        => MmFileSnapshotStore.Validate(new MmPurchaseOrderSnapshot { Origin = origem, Client = client });

    [Theory]
    [InlineData(Dev, "120")]
    [InlineData(Qas, "110")]
    [InlineData("https://outro.sap.fugacouros.com.br:44300", "110")]
    public void Cache_recusa_rotulo_cruzado_ou_nao_aprovado(string origem, string client)
        => Assert.Throws<InvalidOperationException>(
            () => MmFileSnapshotStore.Validate(new MmPurchaseOrderSnapshot { Origin = origem, Client = client }));
}
