using System.Net.Http;
using System.Text.Json;
using FugaTES.WinForms.Monitor;
using static FugaTES.Testes.GuardianProtocolClientTests;

namespace FugaTES.Testes;

public sealed class GuardianProtocolControllerTests
{
    [Fact]
    public async Task Selection_never_loads_or_sends_until_explicit_preview_and_confirmation()
    {
        var gets = 0;
        var posts = 0;
        using var client = new GuardianProtocolClient(Profile(), "command-test", new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) gets++; else posts++;
            return Reply(Snapshot());
        }), () => Sid);
        var controller = new GuardianProtocolController(client);
        controller.Select(Id, "SYNTHETIC-ONLY");
        Assert.False(controller.CanSend);
        Assert.Equal(0, gets);
        await controller.SendAsync(false, default);
        Assert.Equal(0, posts);
        await controller.GeneratePreviewAsync(default);
        Assert.Equal(1, gets);
        Assert.True(controller.CanSend);
        Assert.Equal(Payload, controller.Snapshot!.PayloadJson);
        await controller.SendAsync(false, default);
        Assert.Equal(0, posts);
        Assert.True(controller.CanSend);
    }

    [Theory]
    [InlineData("OPERATION_PENDING")]
    [InlineData("SUCCEEDED")]
    [InlineData("SUCCESS")]
    [InlineData("SENT")]
    [InlineData("CONCLUIDA")]
    [InlineData("UNKNOWN")]
    [InlineData("RESULTADO_INDETERMINADO")]
    [InlineData("EM_PROCESSAMENTO")]
    [InlineData("COMPLETED")]
    [InlineData("IN_FLIGHT")]
    [InlineData("SENDING")]
    [InlineData("IN_PROGRESS")]
    [InlineData("SUCESSO")]
    [InlineData("ENVIADO")]
    [InlineData("FUTURE_STATE")]
    [InlineData("PREPARED")]
    [InlineData("ready")]
    [InlineData("READY ")]
    [InlineData("")]
    public async Task Backend_CanSend_cannot_override_any_state_other_than_exact_READY(string state)
    {
        using var client = new GuardianProtocolClient(Profile(), "command-test", new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Reply(Snapshot() with { State = state });
        }), () => Sid);
        var controller = new GuardianProtocolController(client);
        controller.Select(Id, "SYNTHETIC-ONLY");
        await controller.GeneratePreviewAsync(default);
        Assert.False(controller.CanSend);
        Assert.NotEmpty(controller.LockReason);
        await controller.SendAsync(true, default);
    }

    [Fact]
    public async Task Payload_hash_contract_auth_context_and_backend_gate_are_required()
    {
        var invalid = new[]
        {
            Snapshot() with { PayloadSha256 = new string('0', 64) }, Snapshot() with { PayloadJson = null },
            Snapshot() with { Contract = "OTHER" }, Snapshot() with { Version = 0 },
            Snapshot() with { SnapshotFingerprint = "" }, Snapshot() with { AuthenticationContext = "WINDOWS" },
            Snapshot() with { CanSend = false, BlockCode = "BACKEND_DISABLED" }, Snapshot() with { State = "" },
            Snapshot() with { ProtocolId = 123 }, Snapshot() with { BlockCode = "BACKEND_BLOCKED" }
        };
        foreach (var snapshot in invalid)
        {
            using var client = new GuardianProtocolClient(Profile(), "command-test", new Handler(request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return Reply(snapshot);
            }), () => Sid);
            var controller = new GuardianProtocolController(client);
            controller.Select(Id, "SYNTHETIC-ONLY");
            await controller.GeneratePreviewAsync(default);
            Assert.False(controller.CanSend);
            await controller.SendAsync(true, default);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task Preview_id_must_be_64_hex_before_confirmation_can_enable_send(string? previewId)
    {
        var posts = 0;
        using var client = new GuardianProtocolClient(Profile(), "command-test", new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) posts++;
            return Reply(Snapshot() with { PreviewId = previewId });
        }), () => Sid);
        var controller = new GuardianProtocolController(client);
        controller.Select(Id, "SYNTHETIC-ONLY");
        await controller.GeneratePreviewAsync(default);
        Assert.False(controller.CanSend);
        Assert.Equal("PREVIEW_ID_INVALID", controller.LockReason);
        await controller.SendAsync(true, default);
        Assert.Equal(0, posts);
    }

    [Fact]
    public async Task Confirmed_command_forwards_the_preview_id_unchanged()
    {
        string? sentPreviewId = null;
        var posts = 0;
        using var client = new GuardianProtocolClient(Profile(), "command-test", new AsyncHandler(async request =>
        {
            if (request.Method == HttpMethod.Get) return Reply(Snapshot());
            posts++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            sentPreviewId = body.RootElement.GetProperty("previewId").GetString();
            return Reply(Snapshot() with { CanSend = false, State = "SUCCEEDED", ProtocolId = 12 });
        }), () => Sid);
        var controller = new GuardianProtocolController(client);
        controller.Select(Id, "SYNTHETIC-ONLY");
        await controller.GeneratePreviewAsync(default);
        Assert.True(controller.CanSend);
        await controller.SendAsync(true, default);
        Assert.Equal(PreviewId, sentPreviewId);
        Assert.Equal(1, posts);
        Assert.False(controller.CanSend);
    }

    [Fact]
    public async Task Selection_change_invalidates_preview_and_late_preview_cannot_rebind_even_after_switching_back()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new GuardianProtocolClient(Profile(), "command-test", new AsyncHandler(_ => reply.Task), () => Sid);
        var controller = new GuardianProtocolController(client);
        controller.Select(Id, "SYNTHETIC-ONLY");
        var pending = controller.GeneratePreviewAsync(default);
        Assert.False(controller.CanSend);
        controller.Select(Guid.NewGuid(), "OTHER");
        controller.Select(Id, "SYNTHETIC-ONLY");
        reply.SetResult(Reply(Snapshot()));
        await pending;
        Assert.Null(controller.Snapshot);
        Assert.False(controller.CanSend);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_confirmed_click_blocks_double_click_reselection_and_refresh_even_after_failure(bool failure)
    {
        var posts = 0;
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new GuardianProtocolClient(Profile(), "command-test", new AsyncHandler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Reply(Snapshot()));
            posts++;
            return reply.Task;
        }), () => Sid);
        var controller = new GuardianProtocolController(client);
        controller.Select(Id, "SYNTHETIC-ONLY");
        await controller.GeneratePreviewAsync(default);
        var send = controller.SendAsync(true, default);
        Assert.True(controller.OperationPending);
        Assert.False(controller.CanSend);
        await controller.SendAsync(true, default);
        await controller.GeneratePreviewAsync(default);
        Assert.Equal(1, posts);
        if (failure) reply.SetException(new HttpRequestException("private network error"));
        else reply.SetResult(Reply(Snapshot() with { State = "SUCCEEDED", CanSend = false, ProtocolId = 12 }));
        await send;
        Assert.False(controller.OperationPending);
        Assert.False(controller.CanSend);
        if (failure) Assert.Contains("UNKNOWN", controller.LockReason);
        controller.Select(Guid.NewGuid(), "OTHER");
        controller.Select(Id, "SYNTHETIC-ONLY");
        await controller.GeneratePreviewAsync(default);
        await controller.SendAsync(true, default);
        Assert.False(controller.CanSend);
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task Cancelled_send_remains_unknown_and_preview_error_does_not_leave_stale_send_enabled()
    {
        var getCount = 0;
        using var client = new GuardianProtocolClient(Profile(), "command-test", new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) throw new TaskCanceledException("PRIVATE");
            if (++getCount > 1) throw new HttpRequestException("PRIVATE");
            return Reply(Snapshot());
        }), () => Sid);
        var controller = new GuardianProtocolController(client);
        controller.Select(Id, "SYNTHETIC-ONLY");
        await controller.GeneratePreviewAsync(default);
        await controller.SendAsync(true, default);
        Assert.Contains("UNKNOWN", controller.LockReason);
        await controller.GeneratePreviewAsync(default);
        Assert.False(controller.CanSend);
        Assert.Null(controller.Snapshot);
        Assert.DoesNotContain("PRIVATE", controller.LockReason);
    }
}
