using System.Net;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.WinForms.Monitor;
using R165AL;
using R165AL.Desktop;

public sealed class PhysicalMonitorTests
{
    private static PhysicalFrame Frame(PilotPlan plan, Guid id) => new() {
        RunId = plan.RunId, IntegrationId = id, Contract = plan.Contract, Description = plan.Description,
        Fingerprint = new string('A', 64), H4Version = 1, CorrelationId = "r165ag:cpi:" + plan.RunId.ToString("N") + ":" + Guid.NewGuid().ToString("N"),
        Cluster = "18/fugatesacmdev", Port = 5433, Database = "fugates_dev", Role = "fugates_acm_hml_app",
        ReadOnly = true, CapturedUtc = DateTimeOffset.UtcNow, CreatedUtc = DateTimeOffset.UtcNow,
        State = "CONCLUIDA", Stage = "R165U_CONFIRMED", Protocol = "986801", H4Protocol = "986801", RestartVerified = true,
        Attempts = 1, Fencing = 1, ConfirmedCode = plan.Contract, Reference = plan.Reference
    };

    [Fact]
    public async Task Existing_monitor_receives_only_physical_backend_data_and_never_a_send_command()
    {
        var plan = PilotPlan.Create(); var id = Guid.NewGuid(); var wire = new Capture(Frame(plan, id));
        using var reader = new PhysicalMonitorClient(new("http://127.0.0.1:18761"), "READ_ONLY_TEST_KEY", plan, id, wire);
        var rows = await reader.ConsultarAsync(new(), default);
        Assert.Equal(plan.Contract, Assert.Single(rows.Integracoes).Documento);
        Assert.Contains("SIMULADO", rows.Integracoes[0].Fluxo);
        var preview = await reader.PreviewAsync(id, default);
        Assert.False(preview.CanSend); Assert.False(reader.Configured);
        Assert.Contains("POSTGRESQL REAL", preview.LocalSimulationDetails);
        Assert.Contains("GUARDIAN SIMULADO", preview.LocalSimulationDetails);
        await Assert.ThrowsAsync<GuardianProtocolException>(() => reader.SendAsync(id, "", "", true, default));
        Assert.All(wire.Requests, r => { Assert.Equal("GET", r.Method); Assert.Equal("127.0.0.1", r.Host); Assert.Equal("READ_ONLY_TEST_KEY", r.Key); });
    }

    [Theory]
    [InlineData("stale")][InlineData("foreign")][InlineData("memory")][InlineData("role")]
    public async Task Stale_foreign_memory_or_wrong_identity_is_never_presented_as_physical(string change)
    {
        var plan = PilotPlan.Create(); var id = Guid.NewGuid(); var frame = Frame(plan, id);
        frame = change switch {
            "stale" => frame with { CapturedUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
            "foreign" => frame with { IntegrationId = Guid.NewGuid() },
            "memory" => frame with { Schema = "R165AJ_LOCAL_SIMULATION_V1" },
            _ => frame with { Role = "fugates_dev_app" }
        };
        using var reader = new PhysicalMonitorClient(new("http://127.0.0.1:18761"), "READ_ONLY_TEST_KEY", plan, id, new Capture(frame));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ConsultarAsync(new(), default));
    }

    [Fact]
    public void Corporate_or_non_loopback_url_is_rejected()
    {
        var plan = PilotPlan.Create();
        Assert.Throws<InvalidOperationException>(() => new PhysicalMonitorClient(new("https://guardian.cloudprix.com.br"), "key", plan, Guid.NewGuid(), new Capture(Frame(plan, Guid.NewGuid()))));
    }

    private sealed class Capture(PhysicalFrame frame) : HttpMessageHandler
    {
        public List<(string Method, string Host, string Key)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.Host, request.Headers.GetValues("X-R165AL-Read-Key").Single()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(frame, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") });
        }
    }
}
