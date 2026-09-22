using System.Globalization;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.CasosDeUso.Toledo;
using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;
using FugaTES.Callback.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FugaTES.Testes;

public sealed class Inc006SapContratoInboundTests
{
    private const string JsonAutoritativo = """
        {
          "Contrato": [
            {
              "SistemaBalanca": "Toledo",
              "NumeroContrato": "0000001008",
              "TipoContrato": "AC3P",
              "DescricaoTipoContrato": "ACM contrato compra de terceiros",
              "DataEmissao": "11/09/2026",
              "DataEntregaInicio": "11/09/2026",
              "DataEntregaFim": "31/12/2026",
              "DiasToler": 10,
              "DataLimiteEntrega": "31/12/2026",
              "StatusContrato": "T",
              "QuantidadeContrato": 100000,
              "QuantidadeSaldoDevolucao": 10000,
              "IdParceiro": "0020000051",
              "Material": "000000000007000001",
              "TipoPesagem": "E",
              "TabelaQualidadeID": "0000000042",
              "TabelaQualidadeVersao": "001",
              "TabelaQualidadeVersaoValidade": "001",
              "Observacao": "teste",
              "ValorUnitario": 1.5,
              "Centro": "3011",
              "OperacaoContrato": "10",
              "CentroOpcional": [{ "Centro": "3011" }]
            }
          ]
        }
        """;

    [Fact]
    public void T01_JsonAutoritativoDesserializa()
    {
        var dto = JsonSerializer.Deserialize<SapContractInboundHttpDto>(JsonAutoritativo);
        var roundTrip = JsonSerializer.Serialize(dto);

        Assert.NotNull(dto);
        Assert.Single(dto.Contrato!);
        Assert.Contains("\"Contrato\"", roundTrip, StringComparison.Ordinal);
        Assert.Contains("\"NumeroContrato\"", roundTrip, StringComparison.Ordinal);
        Assert.Contains("\"CentroOpcional\"", roundTrip, StringComparison.Ordinal);
        Assert.DoesNotContain("NRCONTRATO", roundTrip, StringComparison.Ordinal);
    }

    [Fact]
    public void T02_NumeroContratoPreservaZerosAEsquerda()
    {
        Assert.Equal("0000001008", MapearAutoritativo().NumeroContrato);
    }

    [Fact]
    public void T03_MaterialPreservaZerosAEsquerda()
    {
        Assert.Equal("000000000007000001", MapearAutoritativo().Material);
    }

    [Fact]
    public void T04_OperacaoContratoPreservaTexto()
    {
        Assert.Equal("10", MapearAutoritativo().OperacaoContrato);
    }

    [Fact]
    public void T05_CentroOpcionalAceitaZeroItens()
    {
        var dto = DtoComCentros([]);

        Assert.Empty(SapContractInboundMapper.Mapear(dto).Single().CentroOpcional!);
    }

    [Fact]
    public void T06_CentroOpcionalAceitaUmItem()
    {
        var contrato = SapContractInboundMapper.Mapear(DtoComCentros(["3011"])).Single();

        Assert.Equal("3011", Assert.Single(contrato.CentroOpcional!).Centro);
    }

    [Fact]
    public void T07_CentroOpcionalPreservaTresItens()
    {
        var contrato = SapContractInboundMapper.Mapear(DtoComCentros(["3011", "3012", "3013"])).Single();

        Assert.Equal(3, contrato.CentroOpcional!.Count);
    }

    [Fact]
    public void T08_BatchComDoisContratosMapeiaAmbos()
    {
        var primeiro = Item("0001", "MAT-1", []);
        var segundo = Item("0002", "MAT-2", []);

        var contratos = SapContractInboundMapper.Mapear(new([primeiro, segundo]));

        Assert.Equal(["0001", "0002"], contratos.Select(item => item.NumeroContrato));
    }

    [Fact]
    public void T09_DatasPermanecemTextoSemConversaoCultural()
    {
        var culturaAnterior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var contrato = MapearAutoritativo();

            Assert.Equal("11/09/2026", contrato.DataEmissao);
            Assert.Equal("31/12/2026", contrato.DataEntregaFim);
        }
        finally
        {
            CultureInfo.CurrentCulture = culturaAnterior;
        }
    }

    [Fact]
    public void T10_DecimaisSaoMapeadosComCulturaInvariante()
    {
        var contrato = MapearAutoritativo();

        Assert.Equal("100000", contrato.QuantidadeContrato);
        Assert.Equal("10000", contrato.QuantidadeSaldoDevolucao);
        Assert.Equal("1.5", contrato.ValorUnitario);
    }

    [Fact]
    public async Task T11_BodyNuloEhRejeitado()
    {
        var (context, _) = ContextoHttp("null");

        await SapContractInboundEndpoint.HandleAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public void T12_ContratoNuloEhRejeitado()
    {
        Assert.Throws<ArgumentException>(() => SapContractInboundMapper.Mapear(new(null)));
    }

    [Fact]
    public void T13_ContratoVazioEhRejeitado()
    {
        Assert.Throws<ArgumentException>(() => SapContractInboundMapper.Mapear(new([])));
    }

    [Fact]
    public void T14_MapperNaoCriaContratoAcm()
    {
        var contrato = MapearAutoritativo();

        Assert.Equal("0000001008", contrato.NumeroContrato);
        Assert.DoesNotContain("ACM", contrato.NumeroContrato, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T15_MapperNaoDerivaPedidoCompras()
    {
        var source = File.ReadAllText(Path.Combine(RaizRepositorio(), "src", "FugaTES.Callback.Http", "SapContractInboundMapper.cs"));

        Assert.DoesNotContain("PedidoCompras", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task T16_EndpointUsaCasoDeUsoInc003EAtingeGateway()
    {
        var (context, gateway) = ContextoHttp(JsonAutoritativo);

        await SapContractInboundEndpoint.HandleAsync(context);

        Assert.Equal(StatusCodes.Status201Created, context.Response.StatusCode);
        Assert.Single(gateway.Recebidos);
    }

    [Fact]
    public async Task T17_TesteUsaSomenteGatewayDoubleSemToledoReal()
    {
        var (context, gateway) = ContextoHttp(JsonAutoritativo);

        await SapContractInboundEndpoint.HandleAsync(context);

        Assert.IsType<RecordingContratoGateway>(gateway);
        Assert.Equal(1, gateway.Chamadas);
    }

    [Fact]
    public void T18_EndpointEMapperNaoReferenciamF3()
    {
        var pasta = Path.Combine(RaizRepositorio(), "src", "FugaTES.Callback.Http");
        var source = string.Join("\n", Directory.GetFiles(pasta, "SapContractInbound*.cs").Select(File.ReadAllText));

        Assert.DoesNotContain("CorrelacaoGuardianSap", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CallbackFingerprint", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T19_CentroOpcionalPreservaCardinalidadeEOrdem()
    {
        var contrato = SapContractInboundMapper.Mapear(DtoComCentros(["A", "B", "C"])).Single();

        Assert.Equal(["A", "B", "C"], contrato.CentroOpcional!.Select(item => item.Centro));
    }

    [Fact]
    public async Task T20_ResponseSegueContratoFinalDeSucesso()
    {
        var (context, _) = ContextoHttp(JsonAutoritativo);

        await SapContractInboundEndpoint.HandleAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();

        Assert.Equal("{\"mensagem\":\"Sucesso.\"}", body);
    }

    [Fact]
    public async Task T22_ReplayConcluidoRetorna201SemSegundoEfeitoToledo()
    {
        var (context, gateway) = ContextoHttp(JsonAutoritativo);
        await SapContractInboundEndpoint.HandleAsync(context);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonAutoritativo));
        context.Response.Body = new MemoryStream();
        await SapContractInboundEndpoint.HandleAsync(context);
        Assert.Equal(StatusCodes.Status201Created, context.Response.StatusCode);
        Assert.Equal(1, gateway.Chamadas);
    }

    [Fact]
    public void T21_ItemNuloNoBatchEhRejeitado()
    {
        Assert.Throws<ArgumentException>(() =>
            SapContractInboundMapper.Mapear(new SapContractInboundHttpDto([null])));
    }

    private static ContratoToledo MapearAutoritativo()
    {
        var dto = JsonSerializer.Deserialize<SapContractInboundHttpDto>(JsonAutoritativo)!;
        return SapContractInboundMapper.Mapear(dto).Single();
    }

    private static SapContractInboundHttpDto DtoComCentros(IReadOnlyCollection<string> centros) =>
        new([Item("0000001008", "000000000007000001", centros)]);

    private static SapContractInboundItemHttpDto Item(
        string numeroContrato,
        string material,
        IReadOnlyCollection<string> centros) =>
        new(
            "Toledo", numeroContrato, "AC3P", "Descricao", "11/09/2026", "11/09/2026",
            "31/12/2026", 10, "31/12/2026", "T", 100000m, 10000m, "0020000051",
            material, "E", "0000000042", "001", "001", "teste", 1.5m, "3011", "10",
            centros.Select(centro => new SapContractInboundCentroOpcionalHttpDto(centro)).ToArray());

    private static (DefaultHttpContext Context, RecordingContratoGateway Gateway) ContextoHttp(string json)
    {
        var gateway = new RecordingContratoGateway();
        var username = Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SapInbound:BasicAuth:Username"] = username,
            ["SapInbound:BasicAuth:Password"] = password,
            ["SapContractInbound:OfflineEnabled"] = "true"
        }).Build();
        var collection = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        SapContractInboundOfflineComposition.ConfigurarServicos(collection, configuration);
        var services = collection
            .AddSingleton<IContratoToledoGateway>(gateway)
            .AddScoped<EnviarContratoToledoCasoDeUso>()
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() }
        };
        context.Request.ContentType = "application/json";
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (context, gateway);
    }

    private static string RaizRepositorio()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (diretorio is not null && !File.Exists(Path.Combine(diretorio.FullName, "FugaTES.slnx")))
            diretorio = diretorio.Parent;
        return diretorio?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed class RecordingContratoGateway : IContratoToledoGateway
    {
        public List<ContratoToledo> Recebidos { get; } = [];
        public int Chamadas => Recebidos.Count;

        public Task<ResultadoOperacaoToledo<ContratoToledoEnviado>> EnviarAsync(
            ContratoToledo contrato,
            CancellationToken cancellationToken)
        {
            Recebidos.Add(contrato);
            return Task.FromResult(ResultadoOperacaoToledo<ContratoToledoEnviado>.Sucesso(new(contrato)));
        }
    }
}
