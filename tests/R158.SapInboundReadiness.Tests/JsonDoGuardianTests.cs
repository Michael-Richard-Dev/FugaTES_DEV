using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// O endurecimento do receptor: caractere de controle cru dentro de string nao derruba mais a
/// pesagem. Autorizado pelo operador em 06/11/2026, depois de a pesagem 0030384 ser recusada
/// duas vezes com "'0x0D' is invalid within a JSON string".
///
/// A quebra de linha na "Observacao" e PARTE DO PROCESSO - e o que separa o lacre da carga do
/// lacre do envelope da amostra testemunha -, entao ela vai acontecer em toda pesagem com
/// testemunha. Depender do modelo do Guardian estar escapado corretamente seria depender de algo
/// que quem opera nao controla.
///
/// O limite esta fixado aqui: NADA ALEM DISSO e tolerado.
/// </summary>
public sealed class JsonDoGuardianTests
{
    // --- o que e consertado ------------------------------------------------------------------

    [Theory]
    [InlineData("\r", "\\r")]
    [InlineData("\n", "\\n")]
    [InlineData("\r\n", "\\r\\n")]
    [InlineData("\t", "\\t")]
    [InlineData("\b", "\\b")]
    [InlineData("\f", "\\f")]
    [InlineData("\u0001", "\\u0001")]
    [InlineData("\u001f", "\\u001f")]
    public void Controle_cru_dentro_de_string_e_escapado(string cru, string escapado)
        => Assert.Equal($$"""{"a":"x{{escapado}}y"}""", JsonDoGuardian.Sanear($$"""{"a":"x{{cru}}y"}"""));

    [Fact]
    public void O_documento_saneado_passa_a_ser_json_valido()
    {
        var cru = "{\"Observacao\":\"148594,148595 \r\n59187,59188\"}";

        using var doc = JsonDocument.Parse(JsonDoGuardian.Sanear(cru));

        Assert.Equal("148594,148595 \r\n59187,59188", doc.RootElement.GetProperty("Observacao").GetString());
    }

    // --- o que NAO e tocado ------------------------------------------------------------------

    /// <summary>
    /// Fora de string, quebra de linha e tabulacao sao formatacao legitima entre tokens. Mexer
    /// nelas corromperia qualquer JSON indentado - ou seja, todos os que chegam.
    /// </summary>
    [Fact]
    public void Quebra_de_linha_entre_tokens_nao_e_tocada()
    {
        var indentado = "{\n  \"a\": \"x\",\r\n\t\"b\": \"y\"\n}";

        Assert.Equal(indentado, JsonDoGuardian.Sanear(indentado));
    }

    /// <summary>
    /// O que ja esta escapado passa intacto. Mexer no caractere seguinte a uma barra invertida
    /// estragaria "\\n" (barra literal mais a letra n) e "\u000D".
    /// </summary>
    [Theory]
    [InlineData("""{"a":"x\ny"}""")]
    [InlineData("""{"a":"x\\ny"}""")]
    [InlineData("""{"a":"x\u000Dy"}""")]
    [InlineData("""{"a":"aspas \" dentro"}""")]
    [InlineData("""{"a":"barra final \\"}""")]
    public void O_que_ja_esta_escapado_passa_intacto(string json)
        => Assert.Equal(json, JsonDoGuardian.Sanear(json));

    [Fact]
    public void Documento_sem_defeito_volta_sem_copia()
    {
        var json = """{"a":"x"}""";

        Assert.Same(json, JsonDoGuardian.Sanear(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Vazio_e_nulo_nao_quebram(string? json)
        => Assert.Equal("", JsonDoGuardian.Sanear(json));

    /// <summary>
    /// A DELIMITACAO DO ENDURECIMENTO. Nenhum destes e consertado - e nem deve ser. Virgula
    /// faltando, aspas sem par e chave sem fechamento continuam sendo documento quebrado, e quem
    /// recusa e o leitor de JSON. Tolerar isto seria adivinhar o que o Guardian quis dizer.
    /// </summary>
    [Theory]
    [InlineData("""{"a":"x" "b":"y"}""")]
    [InlineData("""{"a":#TCK_PLACA#}""")]
    [InlineData("""{"a":"x}""")]
    [InlineData("""{"a":"x",}""")]
    [InlineData("nao e json")]
    public void Outros_defeitos_continuam_sendo_recusados(string json)
        => Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(JsonDoGuardian.Sanear(json)));

    // --- ponta a ponta, nos dois pontos de leitura -------------------------------------------

    private const string DevolutivaComEnterCru =
        "{\"SistemaBalanca\":\"Toledo\",\"DataPesagem\":\"05/11/2026 19:19:17\",\"TipoPesagem\":\"S\"," +
        "\"IdPesagem\":\"0030384\",\"Centro\":\"3008\",\"PesoBruto\":\"56100,0\",\"PesoTara\":\"19500,0\"," +
        "\"PesoAposDesconto\":\"36600,0\",\"Placa\":\"MLH4G82\",\"Placa Carreta\":\"JAO0G12\"," +
        "\"NomeTransportadora\":\"Transporte Jabuti\",\"IdTransportadora\":\"14300001\"," +
        "\"Observacao\":\"148594,148595,148596,148597 \r\n59187,59188,59189\"," +
        "\"CamposAdicionais\":[{\"numero\":1003,\"valor\":\"4308\"},{\"numero\":1009,\"valor\":\"10\"}]}";

    /// <summary>A admissao: o documento que dava 400 duas vezes agora entra.</summary>
    [Fact]
    public void A_devolutiva_com_enter_cru_passa_a_ser_lida()
    {
        var devolutiva = LeitorDevolutivaSd.Ler(DevolutivaComEnterCru);

        Assert.Equal("0030384", devolutiva.IdPesagem);
        Assert.Equal("3008", devolutiva.Centro);
    }

    /// <summary>
    /// A SEGUNDA leitura, do documento GUARDADO - que e o original, com o Enter cru. Sem o
    /// saneamento aqui, LerComplemento engoliria a JsonException e devolveria complemento vazio:
    /// a nota sairia sem lacre, sem placa e sem motorista, sem uma linha de aviso.
    /// </summary>
    [Fact]
    public void O_complemento_tambem_sai_do_documento_guardado_com_enter_cru()
    {
        var c = MapeadorTextoNfeSd.LerComplemento(DevolutivaComEnterCru);

        Assert.Equal("148594,148595,148596,148597", c.Lacre);
        Assert.Equal("59187,59188,59189", c.Testemunha);
        Assert.Equal("MLH4G82 / JAO0G12", c.Placas);
        Assert.Equal("14300001 - Transporte Jabuti", c.Transportadora);
    }
}
