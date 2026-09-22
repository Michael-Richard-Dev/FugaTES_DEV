using System.Text;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.CasosDeUso.Toledo;
using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;
using FugaTES.Callback.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FugaTES.Testes;

public sealed class Inc006H3SapContractHttpTests
{
    private const string Username = "test-only-user";
    private const string Password = "test-only-password";

    [Fact] public async Task T01_SemAuthorizationRetorna401() => Assert.Equal(401, (await SendAsync()).Status);
    [Fact] public async Task T02_BearerRetorna401() => Assert.Equal(401, (await SendAsync(authorization: "Bearer token")).Status);
    [Fact] public async Task T03_BasicBase64InvalidoRetorna401() => Assert.Equal(401, (await SendAsync(authorization: "Basic !!!")).Status);
    [Fact] public async Task T04_UsuarioIncorretoRetorna401() => Assert.Equal(401, (await SendAsync(authorization: Basic("wrong", Password))).Status);
    [Fact] public async Task T05_SenhaIncorretaRetorna401() => Assert.Equal(401, (await SendAsync(authorization: Basic(Username, "wrong"))).Status);

    [Theory]
    [InlineData(null, Password)]
    [InlineData(Username, null)]
    public async Task T06_CredencialRuntimeAusenteFalhaFechado(string? username, string? password)
    {
        var response = await SendAsync(Basic(Username, Password), username: username, password: password);
        Assert.Equal(401, response.Status);
    }

    [Fact]
    public async Task T07_BasicCorretoProcessaUmaVez()
    {
        var gateway = new RecordingGateway();
        var response = await SendAsync(Basic(Username, Password), gateway: gateway);
        Assert.Equal(201, response.Status);
        Assert.Equal(1, gateway.Calls);
    }

    [Fact] public async Task T08_ContratoAusenteRetorna400() => Assert.Equal(400, (await SendAsync(Basic(Username, Password), "{}")).Status);
    [Fact] public async Task T09_ContratoVazioRetorna400() => Assert.Equal(400, (await SendAsync(Basic(Username, Password), "{\"Contrato\":[]}")).Status);

    [Fact]
    public async Task T10_DoisContratosRetornam400()
    {
        var item = ItemJson("0001");
        Assert.Equal(400, (await SendAsync(Basic(Username, Password), $"{{\"Contrato\":[{item},{item}]}}")).Status);
    }

    [Fact] public async Task T11_ExatamenteUmContratoEhPermitido() => Assert.Equal(201, (await SendAsync(Basic(Username, Password))).Status);
    [Fact] public async Task T12_SucessoRetorna201() => Assert.Equal(StatusCodes.Status201Created, (await SendAsync(Basic(Username, Password))).Status);

    [Fact]
    public async Task T13_BodyDeSucessoEhExato()
    {
        var response = await SendAsync(Basic(Username, Password));
        Assert.Equal("Sucesso.", (string?)response.Body["mensagem"]);
        Assert.Single(response.Body);
    }

    [Fact]
    public async Task T14_FalhaFuncionalConhecidaRetorna400()
    {
        var response = await SendAsync(Basic(Username, Password), gateway: new RecordingGateway(StatusOperacaoToledo.FalhaContrato));
        Assert.Equal(400, response.Status);
    }

    [Fact]
    public async Task T15_BodyDeErroFuncionalComecaComErro()
    {
        var response = await SendAsync(Basic(Username, Password), gateway: new RecordingGateway(StatusOperacaoToledo.FalhaContrato));
        Assert.StartsWith("Erro:", (string?)response.Body["mensagem"]);
    }

    [Fact]
    public async Task T16_ErroTecnicoInesperadoRetorna500Sanitizado()
    {
        var response = await SendAsync(Basic(Username, Password), gateway: new ThrowingGateway());
        Assert.Equal(500, response.Status);
        Assert.Equal("Erro interno.", (string?)response.Body["mensagem"]);
    }

    [Fact]
    public async Task T17_ResponseNuncaContemSenha()
    {
        var response = await SendAsync(Basic(Username, Password), gateway: new ThrowingGateway());
        Assert.DoesNotContain(Password, response.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T18_ResponseNuncaContemStackTrace()
    {
        var response = await SendAsync(Basic(Username, Password), gateway: new ThrowingGateway());
        Assert.DoesNotContain("System.", response.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", response.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T19_NumeroContratoPreservaZeros()
    {
        var gateway = new RecordingGateway();
        await SendAsync(Basic(Username, Password), gateway: gateway);
        Assert.Equal("0000001008", Assert.Single(gateway.Received).NumeroContrato);
    }

    [Fact]
    public async Task T20_NaoHaRetryAutomatico()
    {
        var gateway = new RecordingGateway(StatusOperacaoToledo.FalhaContrato);
        await SendAsync(Basic(Username, Password), gateway: gateway);
        Assert.Equal(1, gateway.Calls);
    }

    [Fact]
    public async Task T21_ReutilizaEnviarContratoToledoCasoDeUso()
    {
        var gateway = new RecordingGateway();
        var response = await SendAsync(Basic(Username, Password), gateway: gateway);
        Assert.Equal(201, response.Status);
        Assert.Single(gateway.Received);
    }

    [Fact]
    public async Task T22_ReutilizaIContratoToledoGateway()
    {
        IContratoToledoGateway gateway = new RecordingGateway();
        var response = await SendAsync(Basic(Username, Password), gateway: gateway);
        Assert.Equal(201, response.Status);
    }

    [Fact]
    public async Task T23_NaoCriaFluxoParalelo()
    {
        var gateway = new RecordingGateway();
        await SendAsync(Basic(Username, Password), gateway: gateway);
        Assert.Equal(1, gateway.Calls);
    }

    [Fact]
    public async Task T24_NaoExigeServicosF3()
    {
        var response = await SendAsync(Basic(Username, Password));
        Assert.Equal(201, response.Status);
    }

    private static async Task<Response> SendAsync(
        string? authorization = null,
        string? json = null,
        string? username = Username,
        string? password = Password,
        IContratoToledoGateway? gateway = null)
    {
        gateway ??= new RecordingGateway();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SapInbound:BasicAuth:Username"] = username,
            ["SapInbound:BasicAuth:Password"] = password,
            ["SapContractInbound:OfflineEnabled"] = "true"
        }).Build();
        var collection = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        SapContractInboundOfflineComposition.ConfigurarServicos(collection, configuration);
        var services = collection
            .AddSingleton(gateway)
            .AddSingleton<IContratoToledoGateway>(gateway)
            .AddScoped<EnviarContratoToledoCasoDeUso>()
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, Response = { Body = new MemoryStream() } };
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json ?? $"{{\"Contrato\":[{ItemJson("0000001008")}]}}"));
        if (authorization is not null) context.Request.Headers.Authorization = authorization;

        await SapContractInboundEndpoint.HandleAsync(context);
        context.Response.Body.Position = 0;
        var raw = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return new(context.Response.StatusCode, JsonNode.Parse(raw)?.AsObject() ?? [], raw);
    }

    private static string Basic(string username, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

    private static string ItemJson(string numeroContrato) => $$"""
        {"SistemaBalanca":"Toledo","NumeroContrato":"{{numeroContrato}}","Material":"MAT-1","OperacaoContrato":"10"}
        """;

    private sealed record Response(int Status, JsonObject Body, string Raw);

    private sealed class RecordingGateway(StatusOperacaoToledo? failure = null) : IContratoToledoGateway
    {
        public List<ContratoToledo> Received { get; } = [];
        public int Calls { get; private set; }

        public Task<ResultadoOperacaoToledo<ContratoToledoEnviado>> EnviarAsync(ContratoToledo contrato, CancellationToken cancellationToken)
        {
            Calls++;
            Received.Add(contrato);
            return Task.FromResult(failure is null
                ? ResultadoOperacaoToledo<ContratoToledoEnviado>.Sucesso(new(contrato))
                : ResultadoOperacaoToledo<ContratoToledoEnviado>.Falha(failure.Value));
        }
    }

    private sealed class ThrowingGateway : IContratoToledoGateway
    {
        public Task<ResultadoOperacaoToledo<ContratoToledoEnviado>> EnviarAsync(ContratoToledo contrato, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"technical secret {Password}");
    }
}
