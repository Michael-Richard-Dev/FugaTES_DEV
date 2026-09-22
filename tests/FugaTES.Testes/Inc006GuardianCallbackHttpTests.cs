using System.Text;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Callback.Http;
using FugaTES.Infraestrutura.Recebimento;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FugaTES.Testes;

public sealed class Inc006GuardianCallbackHttpTests
{
    [Fact]
    public async Task Post_valido_mapeia_sequencial_pesos_e_chega_ao_sap_fake()
    {
        var fixture = await Fixture.CreateAsync();

        var response = await fixture.PostAsync(ValidJson());

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("PROCESSADO", (string?)response.Body["status"]);
        var pesagem = Assert.Single(fixture.Sap.Recebidas);
        Assert.Equal("TICKET-001", pesagem.IdPesagem);
        Assert.Equal(1200m, pesagem.PesoBruto);
        Assert.Equal(200m, pesagem.PesoTara);
        Assert.Equal(1000m, pesagem.PesoAntesDesconto);
    }

    [Fact]
    public async Task Campos_json_extras_sao_tolerados()
    {
        var fixture = await Fixture.CreateAsync();
        var json = ValidJson()[..^1] + ",\"voTicketNaoMapeado\":{\"campo\":123}}";

        var response = await fixture.PostAsync(json);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Single(fixture.Sap.Recebidas);
    }

    [Theory]
    [InlineData("protocoloGuardian")]
    [InlineData("sequencial")]
    [InlineData("pesoBruto")]
    [InlineData("tara")]
    [InlineData("pesoLiquido")]
    [InlineData("estado")]
    public async Task Campo_mvp_ausente_retorna_bad_request(string field)
    {
        var fixture = await Fixture.CreateAsync();
        var json = JsonNode.Parse(ValidJson())!.AsObject();
        json.Remove(field);

        var response = await fixture.PostAsync(json.ToJsonString());

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Empty(fixture.Sap.Recebidas);
    }

    [Fact]
    public async Task Json_invalido_retorna_bad_request()
    {
        var fixture = await Fixture.CreateAsync();

        var response = await fixture.PostAsync("{invalid-json");

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Empty(fixture.Sap.Recebidas);
    }

    [Fact]
    public async Task Callback_duplicado_identico_retorna_idempotente_e_nao_reenvia_sap()
    {
        var fixture = await Fixture.CreateAsync();
        await fixture.PostAsync(ValidJson());

        var response = await fixture.PostAsync(ValidJson());

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("IDEMPOTENTE", (string?)response.Body["status"]);
        Assert.Single(fixture.Sap.Recebidas);
    }

    [Fact]
    public async Task Callback_duplicado_conflitante_retorna_conflict_e_nao_reenvia_sap()
    {
        var fixture = await Fixture.CreateAsync();
        await fixture.PostAsync(ValidJson());

        var response = await fixture.PostAsync(ValidJson().Replace("1200", "1300", StringComparison.Ordinal));

        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Single(fixture.Sap.Recebidas);
    }

    [Fact]
    public async Task CancellationToken_da_requisicao_e_propagado()
    {
        var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.PostAsync(ValidJson(), cancellation.Token));
        Assert.Empty(fixture.Sap.Recebidas);
    }

    private static string ValidJson() => """
        {"protocoloGuardian":"PROTOCOLO-001","sequencial":"TICKET-001","pesoBruto":1200,"tara":200,"pesoLiquido":1000,"estado":"CONCLUIDO"}
        """;

    private sealed class Fixture(ServiceProvider provider, PesagemSapMvpGatewayFake sap)
    {
        public PesagemSapMvpGatewayFake Sap { get; } = sap;

        public static async Task<Fixture> CreateAsync()
        {
            var services = new ServiceCollection();
            GuardianCallbackHttpComposition.ConfigurarServicos(
                services,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Callback:Enabled"] = "true",
                    ["Callback:Route"] = GuardianCallbackEndpoint.Route,
                    ["Correlation:Provider"] = CallbackHttpOptions.InMemoryImplementation
                }).Build());
            var provider = services.BuildServiceProvider();
            var repository = provider.GetRequiredService<IRepositorioCorrelacaoRecebimento>();
            await repository.RegistrarSnapshotAsync(
                new RecebimentoSapCpiMvp("RECEBIMENTO", "FLX_RECEBIMENTO", "MAT-001", "BP-EMISSOR", "BP-TRANSPORTADORA", "CONTRATO-001", "DOC-001", "COMPRA", "CENTRO-001", "ENTRADA", "NF", "PO-001", 7, "ACM-001"),
                CancellationToken.None);
            await repository.AssociarContratoAcmAsync("DOC-001", "ACM-001", CancellationToken.None);
            await repository.AssociarProtocoloAsync("DOC-001", "PROTOCOLO-001", CancellationToken.None);
            return new Fixture(provider, provider.GetRequiredService<PesagemSapMvpGatewayFake>());
        }

        public async Task<HttpResponse> PostAsync(string json, CancellationToken cancellationToken = default)
        {
            var context = new DefaultHttpContext
            {
                RequestServices = provider,
                RequestAborted = cancellationToken
            };
            context.Request.Method = HttpMethods.Post;
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
            context.Response.Body = new MemoryStream();

            await GuardianCallbackEndpoint.HandleAsync(context);
            context.Response.Body.Position = 0;
            var body = await JsonNode.ParseAsync(context.Response.Body, cancellationToken: CancellationToken.None);
            return new HttpResponse(context.Response.StatusCode, body?.AsObject() ?? new JsonObject());
        }
    }

    private sealed record HttpResponse(int StatusCode, JsonObject Body);
}
