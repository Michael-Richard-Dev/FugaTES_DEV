using System.Globalization;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.CasosDeUso.Toledo;
using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;
using FugaTES.Integracao.Toledo.Fakes;

namespace FugaTES.Testes;

public sealed class Inc003ContratoToledoTests
{
    [Fact]
    public async Task CasoDeUsoContrato_ValidaEntradaEDelegaGateway()
    {
        var gateway = new ContratoToledoGatewayFake();
        var casoDeUso = new EnviarContratoToledoCasoDeUso(gateway);
        var contrato = ContratoSintetico("900001");

        var resultado = await casoDeUso.ExecutarAsync(contrato, CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.Sucesso, resultado.Status);
        Assert.Equal(1, gateway.QuantidadeChamadas);
        Assert.Same(contrato, gateway.UltimoContratoRecebido);
        Assert.Same(contrato, resultado.Dados?.Contrato);
        Assert.NotNull(gateway.UltimoRequestJsonRecebido);
    }

    [Fact]
    public async Task CasoDeUsoContrato_RejeitaContratoNuloAntesDoGateway()
    {
        var gateway = new ContratoToledoGatewayFake();
        var casoDeUso = new EnviarContratoToledoCasoDeUso(gateway);

        await Assert.ThrowsAsync<ArgumentNullException>(() => casoDeUso.ExecutarAsync(null!, CancellationToken.None));

        Assert.Equal(0, gateway.QuantidadeChamadas);
    }

    [Fact]
    public async Task FakeContrato_CapturaRequestJsonComEnvelopeContratoArrayUmItem()
    {
        var gateway = new ContratoToledoGatewayFake();
        var contrato = ContratoSintetico("900001");

        await gateway.EnviarAsync(contrato, CancellationToken.None);
        var jsonTexto = gateway.UltimoRequestJsonRecebido!;
        var json = JsonNode.Parse(jsonTexto)!;
        var item = json["Contrato"]!.AsArray().Single()!;

        Assert.Equal(1, gateway.QuantidadeChamadas);
        Assert.Equal("SISTEMA-BALANCA-SINTETICO", (string?)item["SistemaBalanca"]);
        Assert.Equal(900001, (int?)item["NumeroContrato"]);
        Assert.Equal("TIPO-SINTETICO", (string?)item["TipoContrato"]);
        Assert.Equal("DESCRICAO-TIPO-SINTETICA", (string?)item["DescricaoTipoContrato"]);
        Assert.Equal("2026-01-10", (string?)item["DataEmissao"]);
        Assert.Equal("2026-01-11", (string?)item["DataEntregaInicio"]);
        Assert.Equal("2026-01-12", (string?)item["DataEntregaFim"]);
        Assert.Equal(0, (int?)item["DiasToler"]);
        Assert.Equal("2026-01-13", (string?)item["DataLimiteEntrega"]);
        Assert.Equal("STATUS-SINTETICO", (string?)item["StatusContrato"]);
        Assert.Equal("1.000.000.000", (string?)item["QuantidadeContrato"]);
        Assert.Equal("1.000.000.000", (string?)item["QuantidadeSaldoDevolucao"]);
        Assert.Equal(700001, (int?)item["IdParceiro"]);
        Assert.Equal(800001, (int?)item["Material"]);
        Assert.Equal("TIPO-PESAGEM-SINTETICO", (string?)item["TipoPesagem"]);
        Assert.Equal(900101, (int?)item["TabelaQualidadeID"]);
        Assert.Equal(2, (int?)item["TabelaQualidadeVersao"]);
        Assert.Equal(20260110, (int?)item["TabelaQualidadeVersaoValidade"]);
        Assert.Equal("OBSERVACAO-SINTETICA-INC003", (string?)item["Observacao"]);
        Assert.Equal("1,5", (string?)item["ValorUnitario"]);
        Assert.Equal("CENTRO-SINTETICO-A", (string?)item["Centro"]);
        Assert.Equal(900010, (int?)item["OperacaoContrato"]);
        Assert.Contains("\"NumeroContrato\":900001", jsonTexto, StringComparison.Ordinal);
        Assert.DoesNotContain("\"NumeroContrato\":\"900001\"", jsonTexto, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvelopeDocumental_MultiplosContratosPreservaQuantidadeEOrdemSemDeclararBatchReal()
    {
        var gateway = new ContratoToledoGatewayFake();
        var contratos = new[] { ContratoSintetico("900001", centro: "CENTRO-SINTETICO-A"), ContratoSintetico("900002", centro: "CENTRO-SINTETICO-B") };

        var json = JsonNode.Parse(gateway.CapturarRequestDocumental(contratos))!;
        var envelope = json["Contrato"]!.AsArray();

        Assert.Equal(2, envelope.Count);
        Assert.Equal(900001, (int?)envelope[0]!["NumeroContrato"]);
        Assert.Equal(900002, (int?)envelope[1]!["NumeroContrato"]);
        Assert.Equal("CENTRO-SINTETICO-A", (string?)envelope[0]!["Centro"]);
        Assert.Equal("CENTRO-SINTETICO-B", (string?)envelope[1]!["Centro"]);
        Assert.Equal(0, gateway.QuantidadeChamadas);
        Assert.Single(gateway.RequestsJsonRecebidos);
    }

    [Fact]
    public async Task CentroOpcional_MultiplosItensPreservaOrdemEUsaSomenteCampoCentro()
    {
        var gateway = new ContratoToledoGatewayFake();
        var contrato = ContratoSintetico("900003", centrosOpcionais: ["CENTRO-OPCIONAL-A", "CENTRO-OPCIONAL-B"]);

        await gateway.EnviarAsync(contrato, CancellationToken.None);
        var centros = JsonNode.Parse(gateway.UltimoRequestJsonRecebido!)!["Contrato"]!.AsArray().Single()!["CentroOpcional"]!.AsArray();

        Assert.Equal(2, centros.Count);
        Assert.Equal("CENTRO-OPCIONAL-A", (string?)centros[0]!["Centro"]);
        Assert.Equal("CENTRO-OPCIONAL-B", (string?)centros[1]!["Centro"]);
        Assert.Single(centros[0]!.AsObject());
        Assert.Single(centros[1]!.AsObject());
    }

    [Fact]
    public async Task CentroOpcional_PreservacaoDocumentalColecaoExplicitamenteVaziaSerializaArrayVazio()
    {
        var gateway = new ContratoToledoGatewayFake();
        var contrato = ContratoSintetico("900004", centrosOpcionais: []);

        await gateway.EnviarAsync(contrato, CancellationToken.None);
        var item = JsonNode.Parse(gateway.UltimoRequestJsonRecebido!)!["Contrato"]!.AsArray().Single()!.AsObject();
        var centros = item["CentroOpcional"]!.AsArray();

        Assert.True(item.ContainsKey("CentroOpcional"));
        Assert.Empty(centros);
        Assert.Contains("\"CentroOpcional\":[]", gateway.UltimoRequestJsonRecebido, StringComparison.Ordinal);
    }
    [Fact]
    public async Task DtoContrato_NullsNaoRecebemDefaultsArtificiaisEZeroExplicitoNaoDesaparece()
    {
        var gateway = new ContratoToledoGatewayFake();
        var contrato = new ContratoToledo(
            SistemaBalanca: null,
            NumeroContrato: "0",
            TipoContrato: null,
            DescricaoTipoContrato: null,
            DataEmissao: null,
            DataEntregaInicio: null,
            DataEntregaFim: null,
            DiasToler: "0",
            DataLimiteEntrega: null,
            StatusContrato: null,
            QuantidadeContrato: null,
            QuantidadeSaldoDevolucao: null,
            IdParceiro: null,
            Material: null,
            TipoPesagem: null,
            TabelaQualidadeID: null,
            TabelaQualidadeVersao: null,
            TabelaQualidadeVersaoValidade: null,
            Observacao: null,
            ValorUnitario: null,
            Centro: null,
            OperacaoContrato: "0",
            CentroOpcional: null);

        await gateway.EnviarAsync(contrato, CancellationToken.None);
        var item = JsonNode.Parse(gateway.UltimoRequestJsonRecebido!)!["Contrato"]!.AsArray().Single()!.AsObject();
        var json = gateway.UltimoRequestJsonRecebido!;

        Assert.Equal(0, (int?)item["NumeroContrato"]);
        Assert.Equal(0, (int?)item["DiasToler"]);
        Assert.Equal(0, (int?)item["OperacaoContrato"]);
        Assert.False(item.ContainsKey("SistemaBalanca"));
        Assert.False(item.ContainsKey("TipoContrato"));
        Assert.False(item.ContainsKey("StatusContrato"));
        Assert.False(item.ContainsKey("TipoPesagem"));
        Assert.False(item.ContainsKey("QuantidadeContrato"));
        Assert.False(item.ContainsKey("ValorUnitario"));
        Assert.False(item.ContainsKey("Centro"));
        Assert.False(item.ContainsKey("CentroOpcional"));
        Assert.DoesNotContain("Tole" + "do", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AC" + "3S", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"DiasToler\":" + "90", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"OperacaoContrato\":" + "10", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public async Task CulturaAtualNaoAlteraQuantidadesValorUnitarioOuDatas(string cultura)
    {
        var anterior = CultureInfo.CurrentCulture;
        var anteriorUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultura);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
            var gateway = new ContratoToledoGatewayFake();

            await gateway.EnviarAsync(ContratoSintetico("900004"), CancellationToken.None);
            var item = JsonNode.Parse(gateway.UltimoRequestJsonRecebido!)!["Contrato"]!.AsArray().Single()!;

            Assert.Equal("1.000.000.000", (string?)item["QuantidadeContrato"]);
            Assert.Equal("1.000.000.000", (string?)item["QuantidadeSaldoDevolucao"]);
            Assert.Equal("1,5", (string?)item["ValorUnitario"]);
            Assert.Equal("2026-01-10", (string?)item["DataEmissao"]);
            Assert.DoesNotContain("1E+09", gateway.UltimoRequestJsonRecebido, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("1.5", gateway.UltimoRequestJsonRecebido, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = anterior;
            CultureInfo.CurrentUICulture = anteriorUi;
        }
    }

    [Fact]
    public async Task DatasPermanecemTextoSemTimezoneOuConversao()
    {
        var gateway = new ContratoToledoGatewayFake();

        await gateway.EnviarAsync(ContratoSintetico("900005"), CancellationToken.None);
        var item = JsonNode.Parse(gateway.UltimoRequestJsonRecebido!)!["Contrato"]!.AsArray().Single()!;

        Assert.Equal("2026-01-10", (string?)item["DataEmissao"]);
        Assert.Equal("2026-01-11", (string?)item["DataEntregaInicio"]);
        Assert.Equal("2026-01-12", (string?)item["DataEntregaFim"]);
        Assert.Equal("2026-01-13", (string?)item["DataLimiteEntrega"]);
        Assert.DoesNotContain("T00:00", gateway.UltimoRequestJsonRecebido, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Z\"", gateway.UltimoRequestJsonRecebido, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FakeContrato_RetornaFalhaConfiguradaSemHttpReal()
    {
        var gateway = new ContratoToledoGatewayFake(StatusOperacaoToledo.FalhaTecnica);

        var resultado = await gateway.EnviarAsync(ContratoSintetico("900006"), CancellationToken.None);

        Assert.Equal(StatusOperacaoToledo.FalhaTecnica, resultado.Status);
        Assert.Null(resultado.Dados);
        Assert.Equal(1, gateway.QuantidadeChamadas);
        Assert.NotNull(gateway.UltimoRequestJsonRecebido);
    }

    [Fact]
    public void ContratoInc003_NaoMaterializaTransporteRealOuPremissasGuardian()
    {
        var raiz = LocalizarRaizRepositorio();
        var arquivos = Directory.EnumerateFiles(Path.Combine(raiz, "src", "FugaTES.Integracao.Toledo"), "*.cs", SearchOption.AllDirectories);
        var proibidos = new[]
        {
            "HttpClient",
            "HttpRequestMessage",
            "SoapClient",
            "WS_GUARDIAN_PLUS",
            "CadastraContrato",
            "http://",
            "https://",
            "required",
            "maxLength",
            "PostAsync",
            "PutAsync",
            "PatchAsync",
            "DeleteAsync",
            "MERGE",
            "idempot",
            "retry",
            "redirect"
        };

        foreach (var arquivo in arquivos)
        {
            var conteudo = File.ReadAllText(arquivo);
            foreach (var proibido in proibidos)
            {
                Assert.DoesNotContain(proibido, conteudo, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void ContratoInc003_NaoVazaDtoOuTiposWireParaAplicacaoWinFormsOuPersistencia()
    {
        // Durable snapshots and JSON previews are not Toledo wire DTO dependencies.
        foreach (var assembly in new[]
        {
            typeof(FugaTES.Aplicacao.Modelos.Toledo.ContratoToledo).Assembly,
            typeof(FugaTES.WinForms.FrmMonitorMvp).Assembly,
            typeof(FugaTES.Persistencia.MotorIntegracao.RepositorioMotorIntegracaoPostgreSql).Assembly
        }) ArchitectureBoundaries.NoToledoWireDtos(assembly.GetTypes());
    }

    [Fact]
    public void ContratoInc003_NaoCriaEnumsOuParseDeContratoToledo()
    {
        var raiz = LocalizarRaizRepositorio();
        var arquivos = Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(arquivo => arquivo.Contains("ContratoToledo", StringComparison.OrdinalIgnoreCase));
        var proibidos = new[] { "enum TipoContrato", "enum StatusContrato", "enum TipoPesagem", "enum OperacaoContrato", "decimal.Parse", "DateTime.Parse", "DateOnly.Parse" };

        foreach (var arquivo in arquivos)
        {
            var conteudo = File.ReadAllText(arquivo);
            foreach (var proibido in proibidos)
            {
                Assert.DoesNotContain(proibido, conteudo, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void ContratoInc003_FixturesSaoSinteticasENaoCarregamPayloadReal()
    {
        var raiz = LocalizarRaizRepositorio();
        var conteudo = File.ReadAllText(Path.Combine(raiz, "tests", "FugaTES.Testes", "Inc003ContratoToledoTests.cs"));
        var proibidos = new[] { "recebe " + "pesagem", "envia " + "contrato", "AC" + "3S", "StatusContrato = " + "\"T\"", "SistemaBalanca: " + "\"Toledo\"", "DiasToler: " + "\"90\"", "OperacaoContrato: " + "\"10\"" };

        foreach (var proibido in proibidos)
        {
            Assert.DoesNotContain(proibido, conteudo, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ContratoInc003_ArquiteturaPermaneceSemAplicacaoParaIntegracaoToledo()
    {
        var raiz = LocalizarRaizRepositorio();
        var aplicacaoCsproj = File.ReadAllText(Path.Combine(raiz, "src", "FugaTES.Aplicacao", "FugaTES.Aplicacao.csproj"));
        var hostProgram = File.ReadAllText(Path.Combine(raiz, "src", "FugaTES.HostIntegracao", "Program.cs"));

        Assert.DoesNotContain("FugaTES.Integracao.Toledo", aplicacaoCsproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ContratoToledo", hostProgram, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContratoInc003_WhenWritingDefaultNaoUsado()
    {
        var raiz = LocalizarRaizRepositorio();
        var conteudo = File.ReadAllText(Path.Combine(raiz, "src", "FugaTES.Integracao.Toledo", "Dtos", "Contrato", "ContratoToledoRequestDto.cs"));

        Assert.Contains("JsonIgnoreCondition.WhenWritingNull", conteudo, StringComparison.Ordinal);
        Assert.DoesNotContain("WhenWritingDefault", conteudo, StringComparison.OrdinalIgnoreCase);
    }

    private static ContratoToledo ContratoSintetico(string numeroContrato, string centro = "CENTRO-SINTETICO-A", IReadOnlyCollection<string>? centrosOpcionais = null)
    {
        return new ContratoToledo(
            SistemaBalanca: "SISTEMA-BALANCA-SINTETICO",
            NumeroContrato: numeroContrato,
            TipoContrato: "TIPO-SINTETICO",
            DescricaoTipoContrato: "DESCRICAO-TIPO-SINTETICA",
            DataEmissao: "2026-01-10",
            DataEntregaInicio: "2026-01-11",
            DataEntregaFim: "2026-01-12",
            DiasToler: "0",
            DataLimiteEntrega: "2026-01-13",
            StatusContrato: "STATUS-SINTETICO",
            QuantidadeContrato: "1.000.000.000",
            QuantidadeSaldoDevolucao: "1.000.000.000",
            IdParceiro: "700001",
            Material: "800001",
            TipoPesagem: "TIPO-PESAGEM-SINTETICO",
            TabelaQualidadeID: "900101",
            TabelaQualidadeVersao: "2",
            TabelaQualidadeVersaoValidade: "20260110",
            Observacao: "OBSERVACAO-SINTETICA-INC003",
            ValorUnitario: "1,5",
            Centro: centro,
            OperacaoContrato: "900010",
            CentroOpcional: (centrosOpcionais ?? ["CENTRO-OPCIONAL-A"]).Select(valor => new CentroOpcionalContratoToledo(valor)).ToArray());
    }

    private static string LocalizarRaizRepositorio()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);

        while (diretorio is not null && !File.Exists(Path.Combine(diretorio.FullName, "FugaTES.slnx")))
        {
            diretorio = diretorio.Parent;
        }

        return diretorio?.FullName ?? AppContext.BaseDirectory;
    }
}
