using System.Net;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Callback.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class R165AGInboundAuthScopeTests
{
    internal const string Number = "0000999001";
    internal const string User = "SYNTHETIC_CPI_USER";
    internal const string Password = "SYNTHETIC_CPI_PASSWORD_NOT_A_CREDENTIAL";
    internal static string Basic => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(User + ":" + Password));

    internal static IConfiguration Config(Dictionary<string, string?>? changes = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["SapInbound:BasicAuth:Username"] = User,
            ["SapInbound:BasicAuth:Password"] = Password,
            ["MotorIntegracao:AcmPilotNumeroContrato"] = Number,
            ["MotorIntegracao:AcmPilotCentro"] = "3011",
            ["MotorIntegracao:AcmPilotItem"] = null,
            ["HostIntegracao:PollInterval"] = "00:00:02",
            ["AcmProtocolCommand:Enabled"] = "false"
        };
        if (changes is not null) foreach (var pair in changes) values[pair.Key] = pair.Value;
        return PilotInboundCompositionTests.Config(values);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer SYNTHETIC_WRONG_SCHEME")]
    [InlineData("Basic invalid-base64")]
    [InlineData("Basic d3Jvbmc6d3Jvbmc=")]
    public async Task Durable_inbound_denies_bad_auth_before_parsing_or_persistence(string? authorization)
    {
        using var h = new Harness(authorization: authorization, raw: "not-json");
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(401, h.Context.Response.StatusCode);
        Assert.Equal(0, h.Store.Calls);
        Assert.DoesNotContain(Password, h.Body);
        Assert.DoesNotContain("invalid-base64", h.Body);
    }

    [Fact]
    public async Task Missing_backend_auth_configuration_does_not_accept_a_request()
    {
        using var h = new Harness(config: Config(new() { ["SapInbound:BasicAuth:Password"] = null }));
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(401, h.Context.Response.StatusCode);
        Assert.Equal(0, h.Store.Calls);
    }

    [Fact]
    public async Task Non_loopback_cannot_bypass_the_existing_transport_boundary()
    {
        using var h = new Harness();
        h.Context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(403, h.Context.Response.StatusCode);
        Assert.Equal(0, h.Store.Calls);
    }

    [Theory]
    [InlineData("0000999002", "3011", null)]
    [InlineData("999001", "3011", null)]
    [InlineData(Number, "3012", null)]
    [InlineData(Number, "3011", "7000000")]
    [InlineData(Number, "3011", "10")]
    public async Task Wrong_literal_contract_center_or_item_is_rejected_before_store(string number, string center, string? item)
    {
        using var h = new Harness(number: number, center: center, item: item);
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(422, h.Context.Response.StatusCode);
        Assert.Equal(0, h.Store.Calls);
    }

    [Theory]
    [InlineData("MotorIntegracao:AcmPilotNumeroContrato", null)]
    [InlineData("MotorIntegracao:AcmPilotCentro", null)]
    [InlineData("MotorIntegracao:LeaseDuration", "00:00:00")]
    [InlineData("MotorIntegracao:LeaseDuration", "not-a-timespan")]
    [InlineData("SapContractInbound:AutomaticProcessingEnabled", "not-a-boolean")]
    public async Task Invalid_automatic_scope_fails_closed_without_persistence(string key, string? value)
    {
        using var h = new Harness(config: Config(new() { [key] = value }));
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(503, h.Context.Response.StatusCode);
        Assert.Equal(0, h.Store.Calls);
    }

    [Theory]
    [InlineData("0000999001")]
    [InlineData("0000999002")]
    public async Task Authorized_new_nominal_number_is_not_bound_to_any_historical_contract(string number)
    {
        using var h = new Harness(number: number, config: Config(new() { ["MotorIntegracao:AcmPilotNumeroContrato"] = number }));
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(201, h.Context.Response.StatusCode);
        Assert.True(h.Store.Committed);
        Assert.Equal(number, h.Store.Snapshot!.NumeroContrato);
        Assert.Null(h.Store.Snapshot.Item);
        Assert.Equal("7000000", h.Store.Snapshot.Material);
    }

    [Theory]
    [InlineData("000010", "000010", 201)]
    [InlineData("000010", "10", 422)]
    [InlineData("000010", null, 422)]
    [InlineData("10", "10", 201)]
    [InlineData("10", "000010", 422)]
    public async Task Non_null_item_requires_its_exact_configured_representation(string selected, string? received, int status)
    {
        using var h = new Harness(item: received, config: Config(new() { ["MotorIntegracao:AcmPilotItem"] = selected }));
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(status, h.Context.Response.StatusCode);
        Assert.Equal(status == 201 ? 1 : 0, h.Store.Calls);
        if (status == 201) Assert.Equal(selected, h.Store.Snapshot!.Item);
    }

    [Fact]
    public async Task Commit_failure_is_never_acknowledged_as_201()
    {
        using var h = new Harness();
        h.Store.FailCommit = true;
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(500, h.Context.Response.StatusCode);
        Assert.False(h.Store.Committed);
        Assert.DoesNotContain("SYNTHETIC_COMMIT_ERROR", h.Body);
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider services;
        public readonly Store Store = new();
        public readonly DefaultHttpContext Context = new();
        public string Body => Encoding.UTF8.GetString(((MemoryStream)Context.Response.Body).ToArray());
        public Harness(string? authorization = "valid", string? raw = null, string number = Number,
            string center = "3011", string? item = null, IConfiguration? config = null)
        {
            var registrations = new ServiceCollection().AddLogging();
            registrations.AddSingleton(config ?? Config());
            registrations.AddSingleton<IContratoSapToledoVersaoStore>(Store);
            registrations.AddSingleton<ContratoSapToledoCanonicalizer>();
            registrations.AddScoped<AceitarContratoSapToledoDuravelCasoDeUso>();
            services = registrations.BuildServiceProvider();
            Context.RequestServices = services;
            Context.Connection.RemoteIpAddress = IPAddress.Loopback;
            Context.Request.ContentType = "application/json";
            Context.Request.Method = "POST";
            if (authorization is not null) Context.Request.Headers.Authorization = authorization == "valid" ? Basic : authorization;
            raw ??= JsonSerializer.Serialize(new { Contrato = new[] { new { NumeroContrato = number,
                Centro = center, Item = item, TipoContrato = "AC3P", DescricaoTipoContrato = "SYNTHETIC ONLY",
                StatusContrato = "T", OperacaoContrato = "10", TipoPesagem = "E", IdParceiro = "0020000051",
                Material = "7000000", QuantidadeContrato = 10000m } } });
            Context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(raw));
            Context.Response.Body = new MemoryStream();
        }
        public void Dispose() => services.Dispose();
    }

    private sealed class Store : IContratoSapToledoVersaoStore
    {
        public int Calls;
        public bool FailCommit, Committed;
        public ContratoSapToledoSnapshot? Snapshot;
        public Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot snapshot,
            string fingerprint, string payload, string correlation, CancellationToken ct)
        {
            Calls++;
            if (FailCommit) throw new InvalidOperationException("SYNTHETIC_COMMIT_ERROR");
            Snapshot = snapshot;
            Committed = true;
            return Task.FromResult(new VersaoContratoSapToledo(ResultadoRegistroVersaoContrato.Criada,
                snapshot.NumeroContrato!, 1, fingerprint, Guid.NewGuid(), snapshot));
        }
        public Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id, CancellationToken ct) => Task.FromResult<VersaoContratoSapToledo?>(null);
        public Task<bool> TemPredecessorNaoTerminalAsync(string number, long version, CancellationToken ct) => Task.FromResult(false);
    }
}
