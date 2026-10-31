using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.CasosDeUso.Toledo;
using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;
using FugaTES.Integracao.Toledo.Fakes;

namespace FugaTES.Testes;

public sealed class Inc004PreCadastroToledoTests
{
    [Fact]
    public async Task CasoDeUsoPreCadastro_ValidaNumeroContratoBaseAntesDoGateway()
    {
        var gateway = new PreCadastroToledoGatewayFake();
        var casoDeUso = new EnviarPreCadastroToledoCasoDeUso(gateway);
        var preCadastro = PreCadastroSintetico("   ", TipoMovimentoPreCadastro.Carga);

        var resultado = await casoDeUso.ExecutarAsync(preCadastro, CancellationToken.None);

        Assert.Equal(StatusPreCadastroToledo.FalhaValidacao, resultado.Status);
        Assert.Null(resultado.Dados);
        Assert.Equal(0, gateway.QuantidadeChamadas);
        Assert.Null(gateway.UltimoJsonDocumental);
    }

    [Fact]
    public async Task PreCadastroDescarga_MaterializaCodigoContratoComSufixoD_EFixosDocumentais()
    {
        var gateway = new PreCadastroToledoGatewayFake();
        var casoDeUso = new EnviarPreCadastroToledoCasoDeUso(gateway);
        var preCadastro = PreCadastroSintetico("900001", TipoMovimentoPreCadastro.Descarga);

        var resultado = await casoDeUso.ExecutarAsync(preCadastro, CancellationToken.None);
        var json = JsonNode.Parse(gateway.UltimoJsonDocumental!)!;
        var dados = json["dadosProtocoloPreCadastro"]!;
        var campos = dados["camposAdicionais"]!.AsArray();

        Assert.Equal(StatusPreCadastroToledo.Sucesso, resultado.Status);
        Assert.Same(preCadastro, resultado.Dados);
        Assert.Equal(1, gateway.QuantidadeChamadas);
        Assert.Same(preCadastro, gateway.UltimoModeloRecebido);
        Assert.Equal("900001D", (string?)dados["codigoContrato"]);
        Assert.Equal(1, (int?)dados["idAgenda"]);
        Assert.Single(campos);
        Assert.Equal(1, (int?)campos[0]!["numero"]);
        Assert.Equal("Interno", (string?)campos[0]!["valor"]);
    }

    [Fact]
    public async Task PreCadastroCarga_MaterializaCodigoContratoComSufixoC_EFixosDocumentais()
    {
        var gateway = new PreCadastroToledoGatewayFake();
        var casoDeUso = new EnviarPreCadastroToledoCasoDeUso(gateway);
        var preCadastro = PreCadastroSintetico("900002", TipoMovimentoPreCadastro.Carga);

        await casoDeUso.ExecutarAsync(preCadastro, CancellationToken.None);
        var dados = JsonNode.Parse(gateway.UltimoJsonDocumental!)!["dadosProtocoloPreCadastro"]!;
        var campos = dados["camposAdicionais"]!.AsArray();

        Assert.Equal("900002C", (string?)dados["codigoContrato"]);
        Assert.Equal(1, (int?)dados["idAgenda"]);
        Assert.Single(campos);
        Assert.Equal(1, (int?)campos[0]!["numero"]);
        Assert.Equal("Interno", (string?)campos[0]!["valor"]);
    }

    [Fact]
    public async Task PoliticaDocumentalFakeConservadora_NaoMaterializaCamposPendentesQuandoNulos()
    {
        var gateway = new PreCadastroToledoGatewayFake();
        var preCadastro = new PreCadastroToledo(
            NumeroContratoBase: "900003",
            TipoMovimento: TipoMovimentoPreCadastro.Carga,
            CodigoFluxo: null,
            CodigoItem: null,
            CodigoTransportadora: null,
            DataAgendamento: null,
            NumeroReferencia: null);

        await gateway.EnviarAsync(preCadastro, CancellationToken.None);
        var jsonTexto = gateway.UltimoJsonDocumental!;
        var json = JsonNode.Parse(jsonTexto)!;
        var configuracao = json["configuracao"]!.AsObject();
        var dados = json["dadosProtocoloPreCadastro"]!.AsObject();

        Assert.Empty(configuracao);
        Assert.Contains("\"configuracao\":{}", jsonTexto, StringComparison.Ordinal);
        Assert.DoesNotContain("\"urlParaRetorno\":\"\"", jsonTexto, StringComparison.Ordinal);
        Assert.False(dados.ContainsKey("id"));
        Assert.False(dados.ContainsKey("previsaoCargaContrato"));
        Assert.False(dados.ContainsKey("placaVeiculo"));
        Assert.False(dados.ContainsKey("placaCarreta"));
        Assert.False(dados.ContainsKey("identificador"));
        Assert.False(dados.ContainsKey("codigoEmissor"));
        Assert.False(dados.ContainsKey("codigoMotorista"));
        Assert.False(dados.ContainsKey("codigoVeiculo"));
        Assert.False(dados.ContainsKey("codigoFluxo"));
        Assert.False(dados.ContainsKey("codigoItem"));
        Assert.False(dados.ContainsKey("codigoTransportadora"));
        Assert.False(dados.ContainsKey("dataAgendamento"));
        Assert.False(dados.ContainsKey("numeroReferencia"));
        Assert.DoesNotContain("\"id\":0", jsonTexto, StringComparison.Ordinal);
        Assert.DoesNotContain("\"previsaoCargaContrato\":0", jsonTexto, StringComparison.Ordinal);
        Assert.DoesNotContain("\"placaVeiculo\":\"\"", jsonTexto, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodigoFluxoComCedilhaEPreservadoLiteralmenteSemEnumToledo()
    {
        var gateway = new PreCadastroToledoGatewayFake();
        var preCadastro = PreCadastroSintetico("900004", TipoMovimentoPreCadastro.Carga, codigoFluxo: "FLX_EXPEDIÇÃO");

        await gateway.EnviarAsync(preCadastro, CancellationToken.None);
        var dados = JsonNode.Parse(gateway.UltimoJsonDocumental!)!["dadosProtocoloPreCadastro"]!;

        Assert.Equal("FLX_EXPEDIÇÃO", (string?)dados["codigoFluxo"]);
        Assert.Equal("900004C", (string?)dados["codigoContrato"]);
    }

    [Fact]
    public async Task CodigoFluxoNaoDeterminaSufixoCargaDescarga()
    {
        const string codigoFluxo = "FLX_EXPEDIÇÃO";
        var gatewayCarga = new PreCadastroToledoGatewayFake();
        var gatewayDescarga = new PreCadastroToledoGatewayFake();

        await gatewayCarga.EnviarAsync(PreCadastroSintetico("900005", TipoMovimentoPreCadastro.Carga, codigoFluxo: codigoFluxo), CancellationToken.None);
        await gatewayDescarga.EnviarAsync(PreCadastroSintetico("900005", TipoMovimentoPreCadastro.Descarga, codigoFluxo: codigoFluxo), CancellationToken.None);
        var carga = JsonNode.Parse(gatewayCarga.UltimoJsonDocumental!)!["dadosProtocoloPreCadastro"]!;
        var descarga = JsonNode.Parse(gatewayDescarga.UltimoJsonDocumental!)!["dadosProtocoloPreCadastro"]!;

        Assert.Equal(codigoFluxo, (string?)carga["codigoFluxo"]);
        Assert.Equal(codigoFluxo, (string?)descarga["codigoFluxo"]);
        Assert.Equal("900005C", (string?)carga["codigoContrato"]);
        Assert.Equal("900005D", (string?)descarga["codigoContrato"]);
    }

    [Fact]
    public async Task DataAgendamentoPermaneceTextoSemParseTimezoneOuConversao()
    {
        var gateway = new PreCadastroToledoGatewayFake();

        await gateway.EnviarAsync(PreCadastroSintetico("900006", TipoMovimentoPreCadastro.Descarga, dataAgendamento: "2099-01-15"), CancellationToken.None);
        var json = gateway.UltimoJsonDocumental!;
        var dados = JsonNode.Parse(json)!["dadosProtocoloPreCadastro"]!;

        Assert.Equal("2099-01-15", (string?)dados["dataAgendamento"]);
        Assert.DoesNotContain("T00:00", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Z\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public async Task CulturaAtualNaoAlteraPayloadDocumentalControlado(string cultura)
    {
        var anterior = CultureInfo.CurrentCulture;
        var anteriorUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultura);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
            var gateway = new PreCadastroToledoGatewayFake();

            await gateway.EnviarAsync(PreCadastroSintetico("900007", TipoMovimentoPreCadastro.Carga, codigoFluxo: "FLX_EXPEDIÇÃO"), CancellationToken.None);
            var dados = JsonNode.Parse(gateway.UltimoJsonDocumental!)!["dadosProtocoloPreCadastro"]!;

            Assert.Equal("900007C", (string?)dados["codigoContrato"]);
            Assert.Equal("FLX_EXPEDIÇÃO", (string?)dados["codigoFluxo"]);
            Assert.Equal("REFERENCIA-SINTETICA-001", (string?)dados["numeroReferencia"]);
            Assert.Equal("2099-01-15", (string?)dados["dataAgendamento"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = anterior;
            CultureInfo.CurrentUICulture = anteriorUi;
        }
    }

    [Fact]
    public async Task FakeRetornaFalhaConfiguradaSemTransporteReal()
    {
        var gateway = new PreCadastroToledoGatewayFake(StatusPreCadastroToledo.FalhaTecnica);

        var resultado = await gateway.EnviarAsync(PreCadastroSintetico("900008", TipoMovimentoPreCadastro.Descarga), CancellationToken.None);

        Assert.Equal(StatusPreCadastroToledo.FalhaTecnica, resultado.Status);
        Assert.Null(resultado.Dados);
        Assert.Equal(1, gateway.QuantidadeChamadas);
        Assert.NotNull(gateway.UltimoDtoGerado);
        Assert.NotNull(gateway.UltimoJsonDocumental);
    }

    [Fact]
    public async Task DtoDocumentalContemConfiguracaoEDadosProtocoloPreCadastro()
    {
        var gateway = new PreCadastroToledoGatewayFake();

        await gateway.EnviarAsync(PreCadastroSintetico("900009", TipoMovimentoPreCadastro.Carga), CancellationToken.None);
        using var documento = JsonDocument.Parse(gateway.UltimoJsonDocumental!);
        var raiz = documento.RootElement;

        Assert.True(raiz.TryGetProperty("configuracao", out var configuracao));
        Assert.Equal(JsonValueKind.Object, configuracao.ValueKind);
        Assert.True(raiz.TryGetProperty("dadosProtocoloPreCadastro", out var dados));
        Assert.Equal(JsonValueKind.Object, dados.ValueKind);
        Assert.True(dados.TryGetProperty("camposAdicionais", out var campos));
        Assert.Equal(JsonValueKind.Array, campos.ValueKind);
    }

    [Fact]
    public void ArquiteturaInc004_PortsEModelosFicamNaAplicacaoSemDetalhesDeWire()
    {
        var raiz = LocalizarRaizRepositorio();
        var port = Path.Combine(raiz, "src", "FugaTES.Aplicacao", "Contratos", "Gateways", "Toledo", "IPreCadastroToledoGateway.cs");
        var aplicacaoCsproj = File.ReadAllText(Path.Combine(raiz, "src", "FugaTES.Aplicacao", "FugaTES.Aplicacao.csproj"));

        Assert.True(File.Exists(port));
        Assert.DoesNotContain("FugaTES.Integracao.Toledo", aplicacaoCsproj, StringComparison.OrdinalIgnoreCase);
        var application = typeof(FugaTES.Aplicacao.Modelos.Toledo.ContratoToledo).Assembly;
        Assert.DoesNotContain(application.GetReferencedAssemblies(), assembly => assembly.Name == "System.Net.Http"
            || assembly.Name?.StartsWith("FugaTES.Integracao.", StringComparison.Ordinal) == true);
        ArchitectureBoundaries.NoToledoWireDtos(application.GetTypes());
    }

    private static bool ContemProtocoloDeWire(string conteudo) => System.Text.RegularExpressions.Regex.IsMatch(conteudo, @"\b(?:SOAP|REST)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    [Theory]
    [InlineData("CarregarEstadoAsync", false)]
    [InlineData("CarrierStoredBp", false)]
    [InlineData("REST/JSON", true)]
    [InlineData("SOAP endpoint", true)]
    public void ArquiteturaInc004_ProtocoloDeWireUsaLimitesDePalavra(string source, bool expected) => Assert.Equal(expected, ContemProtocoloDeWire(source));

    [Fact]
    public void ArquiteturaInc004_DtoPreCadastroFicaConfinadoNaIntegracaoToledo()
    {
        var raiz = LocalizarRaizRepositorio();
        var arquivosForaIntegracao = Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(arquivo => !arquivo.Contains(Path.Combine("src", "FugaTES.Integracao.Toledo"), StringComparison.OrdinalIgnoreCase));

        foreach (var arquivo in arquivosForaIntegracao)
        {
            var conteudo = File.ReadAllText(arquivo);
            Assert.DoesNotContain("PreCadastroToledoRequestDto", conteudo, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DadosProtocoloPreCadastroToledoDto", conteudo, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ArquiteturaInc004_NaoReferenciaSapContratoTransportadoraHostWinFormsOuPersistencia()
    {
        var raiz = LocalizarRaizRepositorio();
        var integracaoToledoArquivos = Directory.EnumerateFiles(Path.Combine(raiz, "src", "FugaTES.Integracao.Toledo"), "*.cs", SearchOption.AllDirectories)
            .Where(arquivo => arquivo.Contains("PreCadastro", StringComparison.OrdinalIgnoreCase));
        var hostProgram = File.ReadAllText(Path.Combine(raiz, "src", "FugaTES.HostIntegracao", "Program.cs"));
        var winFormsArquivos = Directory.EnumerateFiles(Path.Combine(raiz, "src", "FugaTES.WinForms"), "*.cs", SearchOption.AllDirectories);
        var persistenciaArquivos = Directory.EnumerateFiles(Path.Combine(raiz, "src", "FugaTES.Persistencia"), "*.cs", SearchOption.AllDirectories);

        foreach (var arquivo in integracaoToledoArquivos)
        {
            var conteudo = File.ReadAllText(arquivo);
            Assert.DoesNotContain("FugaTES.Integracao.SAP", conteudo, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("IContratoToledoGateway", conteudo, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ITransportadoraToledoGateway", conteudo, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var assembly in new[]
        {
            typeof(FugaTES.HostIntegracao.HostIntegracaoComposition).Assembly,
            typeof(FugaTES.WinForms.FrmMonitorMvp).Assembly,
            typeof(FugaTES.Persistencia.MotorIntegracao.RepositorioMotorIntegracaoPostgreSql).Assembly
        }) ArchitectureBoundaries.NoToledoWireDtos(assembly.GetTypes());
    }

    [Fact]
    public void SegurancaInc004_FakeNaoContemTransporteRealOuPremissasOperacionais()
    {
        var raiz = LocalizarRaizRepositorio();
        var arquivos = new[]
        {
            Path.Combine(raiz, "src", "FugaTES.Integracao.Toledo", "Fakes", "PreCadastroToledoGatewayFake.cs"),
            Path.Combine(raiz, "src", "FugaTES.Integracao.Toledo", "Mapeamentos", "MapeadorPreCadastroToledo.cs"),
            Path.Combine(raiz, "src", "FugaTES.Integracao.Toledo", "Dtos", "PreCadastro", "PreCadastroToledoRequestDto.cs")
        };
        var proibidos = new[]
        {
            "HttpClient",
            "HttpRequestMessage",
            "SoapClient",
            "PostAsync",
            "PutAsync",
            "PatchAsync",
            "DeleteAsync",
            "CadastraContrato",
            "http://",
            "https://",
            "Bearer",
            "Authorization",
            "credential",
            "redirect",
            "retry",
            "idempot",
            "MaxLength",
            "Substring",
            "DateTime.Parse",
            "DateOnly.Parse",
            "decimal.Parse"
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
    public void EncodingInc004_ArquivosPropriosSaoUtf8BomCrLfSemMojibake()
    {
        var raiz = LocalizarRaizRepositorio();
        var arquivos = new[]
        {
            Path.Combine(raiz, "src", "FugaTES.Aplicacao", "Modelos", "Toledo", "PreCadastroToledo.cs"),
            Path.Combine(raiz, "src", "FugaTES.Aplicacao", "Contratos", "Gateways", "Toledo", "IPreCadastroToledoGateway.cs"),
            Path.Combine(raiz, "src", "FugaTES.Aplicacao", "CasosDeUso", "Toledo", "EnviarPreCadastroToledoCasoDeUso.cs"),
            Path.Combine(raiz, "src", "FugaTES.Aplicacao", "Resultados", "Toledo", "ResultadoPreCadastroToledo.cs"),
            Path.Combine(raiz, "src", "FugaTES.Integracao.Toledo", "Dtos", "PreCadastro", "PreCadastroToledoRequestDto.cs"),
            Path.Combine(raiz, "src", "FugaTES.Integracao.Toledo", "Mapeamentos", "MapeadorPreCadastroToledo.cs"),
            Path.Combine(raiz, "src", "FugaTES.Integracao.Toledo", "Fakes", "PreCadastroToledoGatewayFake.cs"),
            Path.Combine(raiz, "tests", "FugaTES.Testes", "Inc004PreCadastroToledoTests.cs")
        };

        foreach (var arquivo in arquivos)
        {
            var bytes = File.ReadAllBytes(arquivo);
            var texto = System.Text.Encoding.UTF8.GetString(bytes);
            var possuiBom = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191;
            if (texto.Length > 0 && texto[0] == '\uFEFF')
            {
                texto = texto[1..];
            }

            Assert.True(possuiBom, arquivo);
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(texto, "(?<!\\r)\\n"), arquivo);
            Assert.DoesNotContain(((char)0xFFFD).ToString(), texto, StringComparison.Ordinal);
        }

        var testeInc004 = File.ReadAllText(Path.Combine(raiz, "tests", "FugaTES.Testes", "Inc004PreCadastroToledoTests.cs"));
        Assert.Contains("FLX_EXPEDIÇÃO", testeInc004, StringComparison.Ordinal);
    }

    private static PreCadastroToledo PreCadastroSintetico(
        string numeroContratoBase,
        TipoMovimentoPreCadastro tipoMovimento,
        string codigoFluxo = "FLX_RECEBIMENTO_SINTETICO",
        string dataAgendamento = "2099-01-15")
    {
        return new PreCadastroToledo(
            NumeroContratoBase: numeroContratoBase,
            TipoMovimento: tipoMovimento,
            CodigoFluxo: codigoFluxo,
            CodigoItem: "ITEM-SINTETICO-001",
            CodigoTransportadora: "TRANSPORTADORA-SINTETICA-001",
            DataAgendamento: dataAgendamento,
            NumeroReferencia: "REFERENCIA-SINTETICA-001");
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
