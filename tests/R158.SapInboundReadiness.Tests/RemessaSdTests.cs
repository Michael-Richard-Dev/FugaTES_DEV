using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.Integracao.SAP.Http;

namespace R158.SapInboundReadiness.Tests;

// SD passo 3: criacao da remessa no SAP a partir da pesagem devolvida pelo Guardian.
public sealed class RemessaSdTests
{
    private static DevolutivaPesagemSd Pesagem(decimal liquido = 20100.0m,
        string? ordem = "2985", string? linha = "10")
        => new("0020401", "3011", "S", new DateTime(2026, 11, 1, 14, 32, 7),
            32500.0m, 12400.0m, liquido, ordem, linha, "COF7595", "MARCIO FERENZ", "{}");

    private static JsonElement Json(RemessaSdRequest p)
        => JsonDocument.Parse(MapeadorRemessaSd.Serializar(p)).RootElement;

    private static JsonElement PrimeiroItem(RemessaSdRequest p)
        => Json(p).GetProperty("to_DeliveryDocumentItem").GetProperty("results")[0];

    // Regra da planilha: "Quando for Biodiesel = 1 se nao o proprio peso da balanca" e
    // "Quando for Biodiesel = L se nao = KG".
    [Fact]
    public void Biodiesel_cria_remessa_com_um_litro()
    {
        var item = PrimeiroItem(MapeadorRemessaSd.Montar(Pesagem(), SetorAtividadeSd.Biodiesel));
        Assert.Equal("1", item.GetProperty("ActualDeliveryQuantity").GetString());
        Assert.Equal("L", item.GetProperty("DeliveryQuantityUnit").GetString());
    }

    [Theory]
    [InlineData("60")] [InlineData("61")] [InlineData("62")]
    [InlineData("64")] [InlineData("65")] [InlineData("66")] [InlineData("67")]
    public void Demais_setores_usam_o_peso_da_balanca_em_quilo(string setor)
    {
        var item = PrimeiroItem(MapeadorRemessaSd.Montar(Pesagem(), setor));
        Assert.Equal("20100.000", item.GetProperty("ActualDeliveryQuantity").GetString());
        Assert.Equal("KG", item.GetProperty("DeliveryQuantityUnit").GetString());
    }

    // No biodiesel o peso real vai depois, no cabecalho, com os itens zerados.
    [Fact]
    public void So_o_biodiesel_pede_ajuste_de_peso_no_cabecalho()
    {
        Assert.True(FluxoRemessaSd.Escolher(SetorAtividadeSd.Biodiesel, 20100m).AjustaPesoNoCabecalho);
        Assert.False(FluxoRemessaSd.Escolher(SetorAtividadeSd.FareloDeSoja, 20100m).AjustaPesoNoCabecalho);
    }

    // O peso zero so e aceitavel no biodiesel, onde a quantidade e fixa em 1 litro.
    [Fact]
    public void Peso_zero_fora_do_biodiesel_bloqueia()
        => Assert.Equal("SD_REMESSA_PESO_INVALIDO", Assert.Throws<RemessaSdBloqueadaException>(
            () => MapeadorRemessaSd.Montar(Pesagem(liquido: 0m), SetorAtividadeSd.Graos)).Codigo);

    [Fact]
    public void Peso_zero_no_biodiesel_nao_atrapalha()
        => Assert.Equal("1", PrimeiroItem(MapeadorRemessaSd.Montar(
            Pesagem(liquido: 0m), SetorAtividadeSd.Biodiesel)).GetProperty("ActualDeliveryQuantity").GetString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sem_setor_nao_da_para_escolher_o_fluxo(string? setor)
        => Assert.Equal("SD_REMESSA_SETOR_AUSENTE", Assert.Throws<RemessaSdBloqueadaException>(
            () => MapeadorRemessaSd.Montar(Pesagem(), setor)).Codigo);

    [Fact]
    public void Setor_fora_da_lista_bloqueia_em_vez_de_supor_quilo()
        => Assert.Equal("SD_REMESSA_SETOR_DESCONHECIDO", Assert.Throws<RemessaSdBloqueadaException>(
            () => MapeadorRemessaSd.Montar(Pesagem(), "99")).Codigo);

    [Fact]
    public void Pesagem_sem_correlacao_nao_vira_remessa()
        => Assert.Equal("SD_REMESSA_SEM_CORRELACAO", Assert.Throws<RemessaSdBloqueadaException>(
            () => MapeadorRemessaSd.Montar(Pesagem(ordem: null, linha: null), SetorAtividadeSd.Graos)).Codigo);

    // Forma minima comprovada na remessa 80003858: so a navegacao de itens. Mandar ShippingPoint
    // a mais nao ajudou e ainda me levou a uma hipotese errada no diagnostico.
    [Fact]
    public void Corpo_e_o_minimo_comprovado()
    {
        var corpo = MapeadorRemessaSd.Serializar(MapeadorRemessaSd.Montar(Pesagem(), SetorAtividadeSd.Graos));
        Assert.Equal(["to_DeliveryDocumentItem"],
            JsonDocument.Parse(corpo).RootElement.EnumerateObject().Select(p => p.Name));
        foreach (var ausente in new[] { "ShippingPoint", "ShipToParty", "SoldToParty", "DeliveryDocumentType" })
            Assert.DoesNotContain(ausente, corpo, StringComparison.Ordinal);
    }

    // "000010" nao casa com o item "10" da ordem: o SAP devolve sem zeros, e e assim que vai.
    [Fact]
    public void Referencia_vai_sem_zeros_a_esquerda()
    {
        var item = PrimeiroItem(MapeadorRemessaSd.Montar(Pesagem(), SetorAtividadeSd.Graos));
        Assert.Equal("2985", item.GetProperty("ReferenceSDDocument").GetString());
        Assert.Equal("10", item.GetProperty("ReferenceSDDocumentItem").GetString());
    }

    [Theory]
    [InlineData("""{"d":{"DeliveryDocument":"80003858"}}""", "80003858")]
    [InlineData("""{"DeliveryDocument":"80003858"}""", "80003858")]
    public void Numero_da_remessa_e_lido_da_resposta(string corpo, string esperado)
        => Assert.Equal(esperado, MapeadorRemessaSd.LerNumeroRemessa(corpo));

    [Theory]
    [InlineData("")]
    [InlineData("nao e json")]
    [InlineData("""{"d":{"outro":"1"}}""")]
    public void Resposta_sem_numero_nao_inventa(string corpo)
        => Assert.Null(MapeadorRemessaSd.LerNumeroRemessa(corpo));

    // --- leitura do erro do SAP -----------------------------------------------------------
    // Caso real de 01/11/2026: o topo trazia a CONSEQUENCIA e a causa estava num "info" dentro
    // de errordetails. Guardar so o topo esconderia o motivo de quem precisa corrigir.
    private const string ErroReal = """
    {"error":{"code":"VL/367","message":{"lang":"en","value":"An item with no delivery quantity is not permitted. Item will be deleted."},
     "innererror":{"errordetails":[
       {"code":"VL/150","message":"Only 0,000 L of material 4000096  available","severity":"info"},
       {"code":"VL/367","message":"An item with no delivery quantity is not permitted. Item will be deleted.","severity":"error"}]}}}
    """;

    [Fact]
    public void Erro_do_sap_traz_a_causa_antes_da_consequencia()
    {
        var mensagens = LeitorErroSapOData.Ler(ErroReal);
        Assert.Equal(2, mensagens.Count);
        Assert.Equal("VL/150", mensagens[0].Codigo);
        Assert.Equal("info", mensagens[0].Severidade);
        Assert.Contains("Only 0,000 L", mensagens[0].Texto, StringComparison.Ordinal);
        Assert.Equal("VL/367", mensagens[1].Codigo);
        Assert.Equal("error", mensagens[1].Severidade);
    }

    [Fact]
    public void Resumo_mostra_codigo_severidade_e_texto()
    {
        var resumo = LeitorErroSapOData.Resumir(LeitorErroSapOData.Ler(ErroReal));
        Assert.StartsWith("VL/150 (info): Only 0,000 L", resumo, StringComparison.Ordinal);
        Assert.Contains("VL/367 (error)", resumo, StringComparison.Ordinal);
    }

    // Erro sem detalhes nao pode sumir: a mensagem do topo entra.
    [Fact]
    public void Erro_sem_errordetails_ainda_e_lido()
    {
        var m = Assert.Single(LeitorErroSapOData.Ler(
            """{"error":{"code":"SY/530","message":{"value":"Falha generica"}}}"""));
        Assert.Equal("SY/530", m.Codigo);
        Assert.Equal("Falha generica", m.Texto);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<?xml version=\"1.0\"?><error><code>VL/367</code></error>")]
    [InlineData("{}")]
    public void Corpo_sem_erro_legivel_devolve_lista_vazia(string corpo)
        => Assert.Empty(LeitorErroSapOData.Ler(corpo));
}
