using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.WinForms.Monitor;

namespace FugaTES.Testes;

public sealed class GuardianProtocolClientTests
{
    internal static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");
    internal const string Payload = "{\n  \"Contrato\": \"SYNTHETIC-ONLY\", \"Valor\": 1.00\n}";
    internal const string Sid = "S-1-5-21-111-222-333-1001";
    internal static readonly string Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Payload))).ToLowerInvariant();
    internal static readonly string PreviewId = new('B', 64);
    internal static GuardianProtocolProfile Profile() => new()
    {
        Enabled = true, BackendBaseUrl = "https://backend.invalid/",
        CommandCredentialTarget = "SYNTHETIC/BackendCommand", OperatorSid = Sid, CommandCredentialUsername = Sid
    };
    internal static GuardianProtocolSnapshot Snapshot() => new()
    {
        IntegrationId = Id, Contract = "SYNTHETIC-ONLY", Version = 1, SnapshotFingerprint = new string('a', 64),
        PayloadJson = Payload, PayloadSha256 = Hash, PreviewId = PreviewId, Endpoint = "https://guardian.invalid/protocolo",
        Method = "POST", ContentType = "application/json", Accept = "application/json", TransferMode = "Buffered",
        AuthenticationContext = "GUARDIAN_HML_BACKEND", CanSend = true, State = "READY"
    };

    [Fact]
    public async Task Default_profile_is_disabled_with_empty_target_and_never_reads_credentials_or_sends()
    {
        var profile = new GuardianProtocolProfile();
        Assert.False(profile.Enabled);
        Assert.Equal("", profile.CommandCredentialTarget);
        using var session = GuardianProtocolSession.Create(profile, (_, _) => throw new Exception("MUST_NOT_READ"),
            new Handler(_ => throw new Exception("MUST_NOT_SEND")));
        Assert.False(session.Client.Configured);
        var ex = await Assert.ThrowsAsync<GuardianProtocolException>(() => session.Client.PreviewAsync(Id, default));
        Assert.Equal("BACKEND_COMMAND_DISABLED", ex.Message);
    }

    [Theory]
    [InlineData("https://backend.invalid/", false, "", true)]
    [InlineData("http://backend.invalid/", true, "approval", false)]
    [InlineData("http://127.0.0.1:18080/", false, "approval", false)]
    [InlineData("http://127.0.0.1:18080/", true, "", false)]
    [InlineData("http://127.0.0.1:18080/", true, "approval", true)]
    [InlineData("http://[::1]:18080/", true, "approval", true)]
    [InlineData("http://localhost:18080/", true, "approval", true)]
    [InlineData("https://user:pass@backend.invalid/", false, "", false)]
    [InlineData("https://backend.invalid/?token=no", false, "", false)]
    [InlineData("https://backend.invalid/#secret", false, "", false)]
    [InlineData("file:///private", true, "approval", false)]
    public void Only_https_or_explicitly_approved_loopback_ssh_tunnel_is_accepted(string url, bool tunnel, string approval, bool accepted)
    {
        var reads = 0;
        using var session = GuardianProtocolSession.Create(Profile() with
        {
            BackendBaseUrl = url, ApprovedSshTunnel = tunnel, SshTunnelApprovalReference = approval
        }, (_, _) => { reads++; return "command-test-secret"; }, new Handler(_ => throw new Exception("NO_IO")), getOperatorSid: () => Sid);
        Assert.Equal(accepted, session.Client.Configured);
        Assert.Equal(accepted ? 1 : 0, reads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("FugaTES/HML/Guardian")]
    [InlineData("FugaTES/HML/ACM/PostgreSQL")]
    public void Missing_or_Guardian_or_database_credential_target_is_never_read(string target)
    {
        using var session = GuardianProtocolSession.Create(Profile() with { CommandCredentialTarget = target },
            (_, _) => throw new Exception("MUST_NOT_READ"), new Handler(_ => throw new Exception("NO_IO")));
        Assert.False(session.Client.Configured);
    }

    [Fact]
    public void Dedicated_credential_uses_only_configured_target_and_username_and_failure_is_closed()
    {
        using var session = GuardianProtocolSession.Create(Profile(), (target, user) =>
        {
            Assert.Equal("SYNTHETIC/BackendCommand", target);
            Assert.Equal(Sid, user);
            throw new Exception("private vault error");
        }, getOperatorSid: () => Sid);
        Assert.False(session.Client.Configured);
        Assert.DoesNotContain("private", session.Client.LockReason);
        using var empty = GuardianProtocolSession.Create(Profile(), (_, _) => "", getOperatorSid: () => Sid);
        Assert.False(empty.Client.Configured);
    }

    [Fact]
    public void Real_transport_cannot_redirect_or_use_ambient_Windows_authentication()
    {
        using var handler = GuardianProtocolClient.CreateTransport();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseDefaultCredentials);
        Assert.Null(handler.Credentials);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void Command_http_budget_is_90_seconds_for_Guardian_and_durable_capture()
    {
        using var client = new GuardianProtocolClient(Profile(), "command-test",
            new Handler(_ => throw new Exception("NO_IO")), () => Sid);
        var field = typeof(GuardianProtocolClient).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(HttpClient));
        var http = Assert.IsType<HttpClient>(field.GetValue(client));
        Assert.Equal(TimeSpan.FromSeconds(90), http.Timeout);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Preview_preserves_exact_payload_and_reads_web_json_case_insensitively(bool camel)
    {
        using var client = new GuardianProtocolClient(Profile(), "dedicated-command-test", new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"https://backend.invalid/api/v1/acm/guardian/protocolo/{Id}/preview", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("dedicated-command-test", request.Headers.Authorization.Parameter);
            Assert.Equal(Sid, Assert.Single(request.Headers.GetValues("X-FugaTES-Operator-Sid")));
            Assert.Null(request.Content);
            return Reply(Snapshot(), camel: camel);
        }), getOperatorSid: () => Sid);
        var result = await client.PreviewAsync(Id, default);
        Assert.Equal(Payload, result.PayloadJson);
        Assert.Equal(Hash, result.PayloadSha256);
        Assert.Equal(PreviewId, result.PreviewId);
        Assert.Equal("GUARDIAN_HML_BACKEND", result.AuthenticationContext);
    }

    [Fact]
    public async Task Send_posts_only_bound_hash_and_confirmation_to_the_same_backend_once()
    {
        var calls = 0;
        using var client = new GuardianProtocolClient(Profile(), "dedicated-command-test", new AsyncHandler(async request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"https://backend.invalid/api/v1/acm/guardian/protocolo/{Id}/send", request.RequestUri!.AbsoluteUri);
            Assert.Equal("dedicated-command-test", request.Headers.Authorization!.Parameter);
            Assert.Equal(Sid, Assert.Single(request.Headers.GetValues("X-FugaTES-Operator-Sid")));
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
            Assert.Equal(3, body.RootElement.EnumerateObject().Count());
            Assert.Equal(Hash, body.RootElement.GetProperty("payloadSha256").GetString());
            Assert.Equal(PreviewId, body.RootElement.GetProperty("previewId").GetString());
            Assert.True(body.RootElement.GetProperty("confirm").GetBoolean());
            return Reply(Snapshot() with { CanSend = false, State = "SUCCEEDED", HttpStatus = 201,
                SafeBusinessMessage = "Protocolo aceito", SafeResponseBody = "{\"ok\":true}", ProtocolId = 12345678901L });
        }), getOperatorSid: () => Sid);
        var result = await client.SendAsync(Id, Hash, PreviewId, true, default);
        Assert.Equal(1, calls);
        Assert.Equal(201, result.HttpStatus);
        Assert.Equal(12345678901L, result.ProtocolId);
        Assert.Equal("Protocolo aceito", result.SafeBusinessMessage);
        Assert.Equal("{\"ok\":true}", result.SafeResponseBody);
    }

    [Fact]
    public async Task Send_without_confirmation_or_valid_hash_has_no_http_side_effect()
    {
        using var client = new GuardianProtocolClient(Profile(), "dedicated-command-test", new Handler(_ => throw new Exception("NO_IO")), () => Sid);
        await Assert.ThrowsAsync<GuardianProtocolException>(() => client.SendAsync(Id, Hash, PreviewId, false, default));
        await Assert.ThrowsAsync<GuardianProtocolException>(() => client.SendAsync(Id, "invalid", PreviewId, true, default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("legacy-no-preview-id")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public async Task Missing_or_invalid_preview_id_cannot_use_a_legacy_send_fallback(string? previewId)
    {
        var calls = 0;
        using var client = new GuardianProtocolClient(Profile(), "command-test", new Handler(_ =>
        {
            calls++;
            return Reply(Snapshot());
        }), () => Sid);
        await Assert.ThrowsAsync<GuardianProtocolException>(() => client.SendAsync(Id, Hash, previewId!, true, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Redirect_and_raw_error_bodies_are_not_followed_or_exposed_and_no_retry_occurs()
    {
        var calls = 0;
        using var client = new GuardianProtocolClient(Profile(), "dedicated-command-test", new Handler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new StringContent("Bearer PRIVATE") };
            response.Headers.Location = new Uri("https://guardian.invalid/unsafe");
            return response;
        }), getOperatorSid: () => Sid);
        var ex = await Assert.ThrowsAsync<GuardianProtocolException>(() => client.SendAsync(Id, Hash, PreviewId, true, default));
        Assert.Equal("BACKEND_REDIRECT_BLOCKED", ex.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Mismatched_identity_and_malformed_json_fail_closed_without_leaking_backend_content()
    {
        using var wrong = new GuardianProtocolClient(Profile(), "secret-test", new Handler(_ => Reply(Snapshot() with { IntegrationId = Guid.NewGuid() })), () => Sid);
        var ex = await Assert.ThrowsAsync<GuardianProtocolException>(() => wrong.PreviewAsync(Id, default));
        Assert.Equal("BACKEND_IDENTITY_MISMATCH", ex.Message);
        using var malformed = new GuardianProtocolClient(Profile(), "secret-test", new Handler(_ =>
            new(HttpStatusCode.InternalServerError) { Content = new StringContent("Bearer PRIVATE stacktrace") }), () => Sid);
        ex = await Assert.ThrowsAsync<GuardianProtocolException>(() => malformed.PreviewAsync(Id, default));
        Assert.DoesNotContain("PRIVATE", ex.Message);
    }

    [Fact]
    public async Task Transport_failure_is_not_retried_and_safe_response_fields_cannot_echo_credentials()
    {
        var calls = 0;
        using var failed = new GuardianProtocolClient(Profile(), "secret-test", new Handler(_ =>
        { calls++; throw new HttpRequestException("Bearer PRIVATE"); }), () => Sid);
        var ex = await Assert.ThrowsAsync<GuardianProtocolException>(() => failed.SendAsync(Id, Hash, PreviewId, true, default));
        Assert.Equal("BACKEND_COMMAND_UNAVAILABLE", ex.Message);
        Assert.Equal(1, calls);
        using var unsafeReply = new GuardianProtocolClient(Profile(), "secret-test", new Handler(_ => Reply(Snapshot() with
        { SafeBusinessMessage = "secret-test", SafeResponseBody = "{\"token\":\"PRIVATE\"}" })), () => Sid);
        var result = await unsafeReply.PreviewAsync(Id, default);
        Assert.DoesNotContain("secret-test", result.SafeBusinessMessage);
        Assert.DoesNotContain("PRIVATE", result.SafeResponseBody);
    }

    [Theory]
    [InlineData("")]
    [InlineData("S-1-5-21-111-222-333-9999")]
    public void Missing_or_mismatched_actual_windows_sid_never_reads_the_command_credential(string actual)
    {
        using var session = GuardianProtocolSession.Create(Profile(), (_, _) => throw new Exception("NO_VAULT"),
            new Handler(_ => throw new Exception("NO_IO")), getOperatorSid: () => actual);
        Assert.False(session.Client.Configured);
        Assert.Contains("SID", session.Client.LockReason);
    }

    [Fact]
    public void Missing_configured_sid_is_not_replaced_with_actual_sid()
    {
        using var session = GuardianProtocolSession.Create(Profile() with { OperatorSid = "" },
            (_, _) => throw new Exception("NO_VAULT"), getOperatorSid: () => Sid);
        Assert.False(session.Client.Configured);
    }

    [Fact]
    public void Empty_expected_credential_username_binds_to_the_verified_windows_sid()
    {
        using var session = GuardianProtocolSession.Create(Profile() with { CommandCredentialUsername = "" }, (target, username) =>
        {
            Assert.Equal(Sid, username);
            return "dedicated-command-test";
        }, new Handler(_ => throw new Exception("NO_IO")), getOperatorSid: () => Sid);
        Assert.True(session.Client.Configured);
    }

    internal static HttpResponseMessage Reply(GuardianProtocolSnapshot snapshot, bool camel = true)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(snapshot,
            new JsonSerializerOptions { PropertyNamingPolicy = camel ? JsonNamingPolicy.CamelCase : null }), Encoding.UTF8, "application/json") };

    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
    internal sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
