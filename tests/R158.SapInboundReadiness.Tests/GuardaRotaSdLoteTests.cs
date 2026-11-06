using System.Net;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.Integracao.SAP.Http;
using FugaTES.WinForms.Sd;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// As guardas de rota do SD, e o $batch dentro delas.
///
/// Sao duas guardas iguais em espirito - uma no motor, outra na tela - e as duas sao
/// fail-closed: o que nao esta na lista nao sai. Em 06/11/2026 o $batch entrou no cliente sem
/// passar por elas, e teria sido recusado na VM em TODO ajuste. A guarda fez o trabalho dela; os
/// testes do ajuste e que usavam HttpClient cru e nao passavam por ali.
///
/// Este arquivo fecha a lacuna pelo outro lado: prova o que cada guarda libera e, sobretudo, o
/// que ela continua recusando.
/// </summary>
public sealed class GuardaRotaSdLoteTests
{
    private const string Origem = "https://sap.invalid";
    private const string Lote = AjusteRemessaSdSapClient.Servico + "$batch";

    private sealed class Aceita : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
    }

    private static HttpClient Motor() => new(new MotorSdSapGuard(new Aceita(), Origem));
    private static HttpClient Tela() => new(new RemessaSdHttpGuard(new Aceita(), Origem));

    private static async Task<bool> Passa(HttpClient http, HttpMethod metodo, string caminho)
    {
        using var pedido = new HttpRequestMessage(metodo, Origem + caminho);
        try { using var _ = await http.SendAsync(pedido); return true; }
        catch (InvalidOperationException) { return false; }
    }

    // --- o que o lote precisa ----------------------------------------------------------------

    [Fact]
    public async Task O_motor_libera_o_post_do_lote()
    {
        using var http = Motor();
        Assert.True(await Passa(http, HttpMethod.Post, Lote));
    }

    [Fact]
    public async Task A_tela_libera_o_post_do_lote()
    {
        using var http = Tela();
        Assert.True(await Passa(http, HttpMethod.Post, Lote));
    }

    /// <summary>
    /// A rota do lote e a que o cliente monta - se as duas divergirem, a guarda recusa e o
    /// ajuste para. Fixar as duas pontas no mesmo teste e o que impede isso de voltar.
    /// </summary>
    [Fact]
    public void A_rota_do_lote_e_a_que_o_cliente_monta()
        => Assert.Equal(Lote, AjusteRemessaSdSapClient.Servico + SapODataLoteV2.Sufixo);

    // --- O LIMITE: o que continua recusado ---------------------------------------------------

    /// <summary>
    /// Liberar o lote nao pode ter aberto o servico. Estas rotas nao estao na lista e tem de
    /// continuar fora - inclusive outras colecoes do MESMO servico da remessa.
    /// </summary>
    /// <remarks>
    /// A colecao A_OutbDeliveryHeader NAO entra nesta lista: POST nela e como a remessa e
    /// CRIADA, e e liberado de proposito. Eu a tinha posto aqui por engano, e o teste acusou.
    /// </remarks>
    [Theory]
    [InlineData("/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/A_OutbDeliveryItem")]
    [InlineData("/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/$batch/extra")]
    [InlineData("/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/batch")]
    [InlineData("/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner")]
    [InlineData("/sap/bc/adt/qualquer")]
    public async Task Post_fora_da_lista_continua_recusado(string caminho)
    {
        using var motor = Motor();
        using var tela = Tela();

        Assert.False(await Passa(motor, HttpMethod.Post, caminho));
        Assert.False(await Passa(tela, HttpMethod.Post, caminho));
    }

    /// <summary>
    /// O lote e POST e so POST. GET ou DELETE na rota do lote nao fazem sentido e nao passam -
    /// liberar um caminho nao pode liberar os verbos dele.
    /// </summary>
    [Fact]
    public async Task Na_rota_do_lote_so_o_post_passa()
    {
        using var http = Motor();

        Assert.False(await Passa(http, HttpMethod.Get, Lote));
        Assert.False(await Passa(http, HttpMethod.Delete, Lote));
        Assert.False(await Passa(http, HttpMethod.Put, Lote));
    }

    /// <summary>A origem continua valendo: rota certa em host errado nao passa.</summary>
    [Fact]
    public async Task Rota_do_lote_em_outra_origem_nao_passa()
    {
        using var http = new HttpClient(new MotorSdSapGuard(new Aceita(), Origem));
        using var pedido = new HttpRequestMessage(HttpMethod.Post, "https://outro.invalid" + Lote);

        await Assert.ThrowsAsync<InvalidOperationException>(() => http.SendAsync(pedido));
    }
}
