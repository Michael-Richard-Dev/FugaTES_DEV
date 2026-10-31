using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Callback.Http;
using FugaTES.Callback.Http.ManualRetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

public sealed class AcmDedicatedCommandSecurityTests
{
    const string Sid = "S-1-5-21-1-2-3-1001";
    const string Key = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    static AcmManualRetryCommandProfile Profile() => new()
    {
        BaseConfigurationFile = Path.GetFullPath("base.json"), CredentialFile = Path.GetFullPath("command.key"),
        ApprovalsFile = Path.GetFullPath("approvals.json"), Passfile = Path.GetFullPath("pgpass"),
        OperatorSid = Sid, WorkerIdentity = "dedicated-offline-worker"
    };
    static AcmRetryApproval Approval() => new()
    {
        IntegrationId = Guid.NewGuid(), Contract = "1234", Item = "10", OperatorSid = Sid,
        Fingerprint = new('A', 64), ObservedSnapshotDigest = new('B', 64), ContractMappingHash = new('C', 64),
        Homologated = true, ExecutionAuthorized = true, ExclusiveWorker = true, AuthorizationReference = "offline approval",
        BusinessOriginEvidence = "offline business evidence", AllowedStage = AcmRetryStage.RestOnly,
        NotBeforeUtc = DateTimeOffset.UtcNow.AddMinutes(-1), UntilUtc = DateTimeOffset.UtcNow.AddMinutes(1)
    };

    [Fact]
    public async Task Empty_server_approvals_block_preview_and_post_without_touching_store_or_worker()
    {
        var profile = Profile(); var store = new AdmissionStore();
        var service = new AcmManualRetryService(store, new StaticAcmRetryApprovals(profile, []), TimeProvider.System);
        var actor = new AcmRetryActor(true, Sid); var id = Guid.NewGuid();
        Assert.False((await service.PreviewAsync(id, actor, default)).Decision.Eligible);
        var result = await service.RequestAsync(new(Guid.NewGuid(), id, new('A', 64), "Justificativa offline validada", true), actor, default);
        Assert.Equal("COMMAND_NOT_AUTHORIZED", result.Code);
        Assert.Equal(0, store.AdmissionCalls); Assert.Equal(0, store.ReadCalls);
    }

    [Fact]
    public void Server_rejects_duplicate_targets_or_approvals_outside_exact_scope_and_sid()
    {
        var approval = Approval(); var profile = Profile() with { Contract = "1234", Item = "10", NormalConfigurationFile = Path.GetFullPath("normal.json") };
        profile.Validate([approval]);
        Assert.Throws<InvalidOperationException>(() => profile.Validate([approval, approval]));
        Assert.Throws<InvalidOperationException>(() => profile.Validate([approval with { Contract = "9999" }]));
        Assert.Throws<InvalidOperationException>(() => profile.Validate([approval with { OperatorSid = "S-1-5-21-1-2-3-1002" }]));
        Assert.Throws<InvalidOperationException>(() => (profile with { Contract = null, Item = null }).Validate([approval]));
    }

    [Fact]
    public void Runtime_overrides_legacy_pilot_and_global_worker_without_changing_baseline()
    {
        var original = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FUGATES_INTEGRATION_WORKER_ENABLED"] = "true", ["MotorIntegracao:AcmPilotNumeroContrato"] = "1139",
            ["MotorIntegracao:AcmPilotItem"] = "old", ["MotorIntegracao:AcmInboundWindow:Enabled"] = "true",
            ["AcmR57:Normal:ExecutionAuthorized"] = "false", ["ConnectionStrings:FugaTES"] = "Host=unsafe;Password=never-copy"
        }).Build();
        var profile = Profile() with { Contract = "1234", Item = "10" };
        var runtime = AcmManualRetryCommandHost.BuildRuntimeConfiguration(profile, original);
        var connection = new NpgsqlConnectionStringBuilder(runtime.GetConnectionString("FugaTES"));
        Assert.Equal(5433, connection.Port); Assert.Equal("127.0.0.1", connection.Host); Assert.Equal("fugates_dev", connection.Database);
        Assert.Equal("fugates", connection.SearchPath); Assert.Equal("fugates_acm_hml_app", connection.Username); Assert.Null(connection.Password);
        Assert.Equal(profile.Passfile, connection.Passfile); Assert.False(connection.Enlist);
        Assert.Equal("false", runtime["FUGATES_INTEGRATION_WORKER_ENABLED"]);
        Assert.Equal("1234", runtime["MotorIntegracao:AcmPilotNumeroContrato"]); Assert.Equal("10", runtime["MotorIntegracao:AcmPilotItem"]);
        Assert.Equal("3011", runtime["MotorIntegracao:AcmPilotCentro"]); Assert.Equal("false", runtime["MotorIntegracao:AcmInboundWindow:Enabled"]);
        Assert.Equal("1139", original["MotorIntegracao:AcmPilotNumeroContrato"]); Assert.Equal("true", original["FUGATES_INTEGRATION_WORKER_ENABLED"]);
    }

    [Fact]
    public async Task Dedicated_Bearer_maps_only_to_server_sid_and_ignores_read_key_and_forged_sid()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        AcmCommandAuthenticationHandler.Configure(builder.Services, new(Key, Sid));
        var store = new AdmissionStore();
        builder.Services.AddSingleton(new AcmManualRetryService(store, new StaticAcmRetryApprovals(Profile(), []), TimeProvider.System));
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); AcmManualRetryEndpoint.MapCandidate(app);
        await app.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var route = "/api/v1/acm/manual-retry/" + Guid.NewGuid().ToString("D");
        http.DefaultRequestHeaders.Add("X-R165AL-Read-Key", Key); http.DefaultRequestHeaders.Add("X-FugaTES-Operator-Sid", "S-1-5-21-9-9-9-9999");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(route + "/preview")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('F', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(route + "/preview")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        var preview = await http.GetFromJsonAsync<AcmRetryPreview>(route + "/preview");
        Assert.NotNull(preview); Assert.Equal(Sid, preview.OperatorSid); Assert.False(preview.Decision.Eligible);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/dev/read/identity")).StatusCode);
        using var forged = new StringContent("{\"operatorSid\":\"S-1-5-21-9-9-9-9999\"}", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync(route, forged)).StatusCode);
        Assert.Equal(0, store.AdmissionCalls); Assert.Equal(0, store.ReadCalls);
        await app.StopAsync();
    }

    [Fact]
    public async Task Worker_is_idle_until_matching_committed_receipt_then_executes_once_without_polling()
    {
        var approval = Approval(); var queue = new AcmRetryDispatchQueue(); var store = new AdmissionStore { HoldAdmission = true };
        var committed = new CommittedAcmRetryStore(store, queue); var executor = new RecordingExecutor();
        using var worker = new AcmRetryCommandWorker(queue, executor, NullLogger<AcmRetryCommandWorker>.Instance);
        await worker.StartAsync(default);
        var request = new AcmRetryRequest(Guid.NewGuid(), approval.IntegrationId, approval.ObservedSnapshotDigest, "Justificativa offline validada", true);
        var admission = committed.AdmitAsync(request, new(true, Sid), approval, DateTimeOffset.UtcNow, default);
        await store.AdmissionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(executor.Called.Task.IsCompleted);
        store.Commit.TrySetResult();
        Assert.Equal("QUEUED", (await admission).State);
        var received = await executor.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(request.CommandId, received.CommandId); Assert.Equal(approval, received.Approval);
        await worker.StopAsync(default);
        Assert.Equal(1, executor.Calls); Assert.Equal(0, store.Polls);
    }

    [Theory]
    [InlineData("BLOCKED", false)] [InlineData("UNKNOWN", false)] [InlineData("ALREADY_RECORDED", false)] [InlineData("QUEUED", true)]
    public async Task Unverified_or_nonqueued_admission_never_wakes_worker(string state, bool mismatchedReceipt)
    {
        var approval = Approval(); var queue = new AcmRetryDispatchQueue(); var store = new AdmissionStore { State = state, MismatchedReceipt = mismatchedReceipt };
        var committed = new CommittedAcmRetryStore(store, queue); var executor = new RecordingExecutor();
        using var worker = new AcmRetryCommandWorker(queue, executor, NullLogger<AcmRetryCommandWorker>.Instance);
        await committed.AdmitAsync(new(Guid.NewGuid(), approval.IntegrationId, new('A', 64), "Justificativa offline validada", true), new(true, Sid), approval, DateTimeOffset.UtcNow, default);
        var sentinel = new CommittedAcmRetry(Guid.NewGuid(), approval);
        queue.Schedule(sentinel); // A leaked wake occupies the only slot and makes this fail.
        await worker.StartAsync(default);
        Assert.Equal(sentinel, await executor.Called.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await worker.StopAsync(default); Assert.Equal(1, executor.Calls); Assert.Equal(0, store.Polls);
    }

    sealed class RecordingExecutor : IAcmApprovedRetryExecutor
    {
        public int Calls;
        public TaskCompletionSource<CommittedAcmRetry> Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> ExecuteAsync(CommittedAcmRetry command, CancellationToken ct) { Interlocked.Increment(ref Calls); Called.TrySetResult(command); return Task.FromResult("UNKNOWN"); }
    }
    sealed class AdmissionStore : IAcmManualRetryStore
    {
        public int AdmissionCalls, ReadCalls, Polls; public bool HoldAdmission, MismatchedReceipt; public string State = "QUEUED";
        public TaskCompletionSource AdmissionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), Commit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AcmRetryAdmission> AdmitAsync(AcmRetryRequest r, AcmRetryActor a, AcmRetryApproval p, DateTimeOffset n, CancellationToken ct)
        { AdmissionCalls++; AdmissionEntered.TrySetResult(); if (HoldAdmission) await Commit.Task.WaitAsync(ct); return new(State, "offline", MismatchedReceipt ? Guid.NewGuid() : r.CommandId, r.IntegrationId); }
        public Task<AcmRetrySnapshot?> ReadAsync(Guid id, CancellationToken ct) { ReadCalls++; throw new InvalidOperationException("Unexpected read"); }
        public Task<Guid?> PendingCommandAsync(AcmRetryApproval a, CancellationToken ct) { Polls++; throw new InvalidOperationException("Unexpected poll"); }
        public Task<AcmRetryWork?> StartAsync(Guid id, AcmRetryApproval a, string w, DateTimeOffset n, CancellationToken ct) => throw new InvalidOperationException();
        public Task CompleteAsync(AcmRetryWork w, string r, DateTimeOffset n, CancellationToken ct) => throw new InvalidOperationException();
    }
}

