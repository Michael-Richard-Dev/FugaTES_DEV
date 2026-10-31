using System.Net;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Callback.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Xunit;

public sealed class R165AGInboundWindowTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    internal static IConfiguration Config(Dictionary<string, string?>? changes = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["SapContractInbound:SecurityMode"] = "LoopbackBasicHttps",
            ["MotorIntegracao:AcmPilotNumeroContrato"] = null,
            ["MotorIntegracao:AcmPilotCentro"] = null,
            ["MotorIntegracao:AcmPilotItem"] = null,
            ["MotorIntegracao:AcmInboundWindow:Enabled"] = "true",
            ["MotorIntegracao:AcmInboundWindow:WindowId"] = "bbf7ba13eb30412d9638008048c02a31",
            ["MotorIntegracao:AcmInboundWindow:AuthorizationReference"] = "SYNTHETIC_AUTHORIZED_WINDOW",
            ["MotorIntegracao:AcmInboundWindow:NotBeforeUtc"] = Now.AddMinutes(-5).ToString("O"),
            ["MotorIntegracao:AcmInboundWindow:UntilUtc"] = Now.AddMinutes(5).ToString("O"),
            ["MotorIntegracao:AcmInboundWindow:Centro"] = "3011",
            ["MotorIntegracao:AcmInboundWindow:AllowedSapPartners:0"] = "0020000051",
            ["MotorIntegracao:AcmInboundWindow:AllowedSapMaterials:0"] = "7000000"
        };
        if (changes is not null) foreach (var pair in changes) values[pair.Key] = pair.Value;
        return R165AGInboundAuthScopeTests.Config(values);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http")]
    [InlineData("HTTPS")]
    [InlineData("https,http")]
    public async Task Secure_proxy_requires_exactly_one_https_header_before_parsing(string? proto)
    {
        using var h = new Harness(proto: proto, raw: "not-json");
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(403, h.Context.Response.StatusCode);
        Assert.Equal(0, h.Store.Calls);
    }

    [Fact]
    public async Task Duplicate_https_header_is_rejected()
    {
        using var h = new Harness(raw: "not-json");
        h.Context.Request.Headers["X-Forwarded-Proto"] = new StringValues(new[] { "https", "https" });
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(403, h.Context.Response.StatusCode);
        Assert.Equal(0, h.Store.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Basic d3Jvbmc6d3Jvbmc=")]
    public async Task Window_requires_basic_before_json_parse(string? auth)
    {
        using var h = new Harness(auth: auth, raw: "not-json");
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(401, h.Context.Response.StatusCode);
        Assert.Equal(0, h.Store.Calls);
    }

    [Fact]
    public async Task Forwarded_https_never_trusts_non_loopback_peer()
    {
        using var h = new Harness();
        h.Context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.25");
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(403, h.Context.Response.StatusCode);
        Assert.Equal(0, h.Store.Calls);
    }

    [Theory]
    [InlineData("MotorIntegracao:AcmInboundWindow:WindowId", null)]
    [InlineData("MotorIntegracao:AcmInboundWindow:AuthorizationReference", null)]
    [InlineData("MotorIntegracao:AcmInboundWindow:NotBeforeUtc", "invalid")]
    [InlineData("MotorIntegracao:AcmInboundWindow:Enabled", "invalid")]
    public async Task Malformed_window_fails_closed_without_store(string key, string? value)
    {
        using var h = new Harness(config: Config(new() { [key] = value }));
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(503, h.Context.Response.StatusCode);
        Assert.Equal(0, h.Store.Calls);
        Assert.DoesNotContain("invalid", h.Body);
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(5)]
    public async Task Before_or_at_expiry_is_forbidden_before_store(int minutes)
    {
        using var h = new Harness(now: Now.AddMinutes(minutes));
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(403, h.Context.Response.StatusCode);
        Assert.Equal(0, h.Store.Calls);
    }

    [Theory]
    [InlineData("Centro", "3012")]
    [InlineData("Item", "10")]
    [InlineData("TipoContrato", "OTHER")]
    [InlineData("TipoPesagem", "S")]
    // R170: a operacao do SAP saiu da regra de escopo - varia para o mesmo sentido de negocio.
    // Quem decide compra x venda e o par TipoContrato + TipoPesagem, coberto na linha acima.
    [InlineData("StatusContrato", "I")]
    [InlineData("IdParceiro", "OTHER")]
    [InlineData("Material", "OTHER")]
    [InlineData("NumeroContrato", "0000001138")]
    public async Task Unauthorized_business_scope_never_reaches_store(string key, string value)
    {
        using var h = new Harness(payloadChanges: new() { [key] = value });
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(422, h.Context.Response.StatusCode);
        Assert.Equal(0, h.Store.Calls);
    }

    [Theory]
    [InlineData("0000999001")]
    [InlineData("0000999002")]
    public async Task New_contract_uses_server_window_admission_without_nominal_history(string number)
    {
        using var h = new Harness(payloadChanges: new() { ["NumeroContrato"] = number });
        h.Context.Request.Headers["X-Admission-Correlation"] = "CLIENT_UNTRUSTED";
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(201, h.Context.Response.StatusCode);
        Assert.Equal(1, h.Store.Calls);
        Assert.NotNull(h.Store.Admission);
        Assert.Equal(number, h.Store.Snapshot!.NumeroContrato);
        Assert.StartsWith(h.Store.Admission!.CorrelationPrefix, h.Store.Correlation);
        Assert.DoesNotContain("CLIENT_UNTRUSTED", h.Body);
    }

    [Fact]
    public async Task Admission_scope_conflict_is_sanitized_and_never_acknowledged()
    {
        using var h = new Harness();
        h.Store.Conflict = true;
        await SapContractInboundEndpoint.HandleDurableAsync(h.Context);
        Assert.Equal(409, h.Context.Response.StatusCode);
        Assert.DoesNotContain(nameof(AcmInboundScopeConflictException), h.Body);
        Assert.DoesNotContain("stack", h.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SapInbound:BasicAuth:Username")]
    [InlineData("SapInbound:BasicAuth:Password")]
    public void Missing_basic_configuration_fails_productive_startup(string key)
    {
        var error = Assert.Throws<InvalidOperationException>(() => SapContractInboundProductiveComposition.ConfigurarServicos(
            new ServiceCollection(), R165AGInboundAuthScopeTests.Config(new() { [key] = null })));
        Assert.Equal("SAP_INBOUND_BASIC_CONFIGURATION_REQUIRED", error.Message);
        Assert.DoesNotContain(R165AGInboundAuthScopeTests.Password, error.ToString());
    }

    [Fact]
    public void Valid_window_composes_without_a_nominal_contract_or_database_connection()
    {
        var services = new ServiceCollection();
        Assert.True(SapContractInboundProductiveComposition.ConfigurarServicos(services, Config()));
        Assert.Single(services, s => s.ServiceType == typeof(TimeProvider));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider services;
        public Store Store { get; } = new();
        public DefaultHttpContext Context { get; } = new();
        public string Body => Encoding.UTF8.GetString(((MemoryStream)Context.Response.Body).ToArray());
        public Harness(string? proto = "https", string? auth = "valid", string? raw = null,
            DateTimeOffset? now = null, IConfiguration? config = null, Dictionary<string, object?>? payloadChanges = null)
        {
            var registrations = new ServiceCollection().AddLogging();
            registrations.AddSingleton(config ?? Config());
            registrations.AddSingleton<TimeProvider>(new FixedClock(now ?? Now));
            registrations.AddSingleton<IContratoSapToledoVersaoStore>(Store);
            registrations.AddSingleton<ContratoSapToledoCanonicalizer>();
            registrations.AddScoped<AceitarContratoSapToledoDuravelCasoDeUso>();
            services = registrations.BuildServiceProvider();
            Context.RequestServices = services;
            Context.Connection.RemoteIpAddress = IPAddress.Loopback;
            Context.Request.ContentType = "application/json";
            if (proto is not null) Context.Request.Headers["X-Forwarded-Proto"] = proto;
            if (auth is not null) Context.Request.Headers.Authorization = auth == "valid" ? R165AGInboundAuthScopeTests.Basic : auth;
            var payload = new Dictionary<string, object?> { ["NumeroContrato"] = "0000999001", ["Centro"] = "3011",
                ["Item"] = null, ["TipoContrato"] = "AC3P", ["DescricaoTipoContrato"] = "SYNTHETIC ONLY",
                ["StatusContrato"] = "T", ["OperacaoContrato"] = "10", ["TipoPesagem"] = "E",
                ["IdParceiro"] = "0020000051", ["Material"] = "7000000", ["QuantidadeContrato"] = 10000m };
            if (payloadChanges is not null) foreach (var pair in payloadChanges) payload[pair.Key] = pair.Value;
            raw ??= JsonSerializer.Serialize(new { Contrato = new[] { payload } });
            Context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(raw));
            Context.Response.Body = new MemoryStream();
        }
        public void Dispose() => services.Dispose();
    }

    private sealed class Store : IContratoSapToledoAdmissionStore
    {
        public int Calls;
        public bool Conflict;
        public string? Correlation;
        public AcmInboundAdmission? Admission;
        public ContratoSapToledoSnapshot? Snapshot;
        public Task<VersaoContratoSapToledo> RegistrarPrimeiroOuObterReentregaAsync(ContratoSapToledoSnapshot snapshot,
            string fingerprint, string payload, string correlation, AcmInboundAdmission admission, CancellationToken ct)
        {
            Admission = admission;
            return RegistrarOuObterAsync(snapshot, fingerprint, payload, correlation, ct);
        }
        public Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot snapshot,
            string fingerprint, string payload, string correlation, CancellationToken ct)
        {
            Calls++;
            if (Conflict) throw new AcmInboundScopeConflictException();
            Snapshot = snapshot;
            Correlation = correlation;
            return Task.FromResult(new VersaoContratoSapToledo(ResultadoRegistroVersaoContrato.Criada,
                snapshot.NumeroContrato!, 1, fingerprint, Guid.NewGuid(), snapshot) { AdmissionCorrelationId = correlation });
        }
        public Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id, CancellationToken ct) => Task.FromResult<VersaoContratoSapToledo?>(null);
        public Task<bool> TemPredecessorNaoTerminalAsync(string number, long version, CancellationToken ct) => Task.FromResult(false);
    }
}
