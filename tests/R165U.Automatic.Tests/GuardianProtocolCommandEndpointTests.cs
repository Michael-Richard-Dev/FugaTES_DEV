using FugaTES.Callback.Http;
using FugaTES.HostIntegracao;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class GuardianProtocolCommandEndpointTests
{
    private const string Secret = "SYNTHETIC_COMMAND_ONLY_01234567890123456789";
    private const string Sid = "S-1-5-21-111111111-222222222-333333333-1001";
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");
    [Fact]
    public async Task Missing_command_configuration_denies_before_any_business_service_is_resolved()
    {
        var endpoint = typeof(SapContractInboundEndpoint).Assembly.GetType("FugaTES.Callback.Http.GuardianProtocolCommandEndpoint");
        Assert.NotNull(endpoint);
        var method = endpoint.GetMethod("HandlePreviewAsync");
        Assert.NotNull(method);
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        await Assert.IsAssignableFrom<Task>(method.Invoke(null, [context, Guid.NewGuid()]));
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("192.0.2.1", Sid, Secret, true)]
    [InlineData("127.0.0.1", "S-1-5-21-OTHER", Secret, true)]
    [InlineData("127.0.0.1", Sid, "wrong", true)]
    [InlineData("127.0.0.1", Sid, Secret, false)]
    public async Task Command_denies_external_origin_wrong_operator_secret_or_disabled_profile(string address, string sid, string secret, bool enabled)
    {
        var fake = new FakeService();
        var context = Context(fake, enabled);
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Request.Headers["X-FugaTES-Operator-Sid"] = sid;
        context.Request.Headers.Authorization = "Bearer " + secret;
        await GuardianProtocolCommandEndpoint.HandlePreviewAsync(context, Id);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Equal(0, fake.Previews);
        Assert.Equal(0, fake.Sends);
    }

    [Fact]
    public async Task Authenticated_preview_is_read_only_and_scoped_to_the_selected_integration()
    {
        var fake = new FakeService();
        var context = Context(fake);
        await GuardianProtocolCommandEndpoint.HandlePreviewAsync(context, Id);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(1, fake.Previews);
        Assert.Equal(0, fake.Sends);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        var other = Context(fake);
        await GuardianProtocolCommandEndpoint.HandlePreviewAsync(other, Guid.NewGuid());
        Assert.Equal(403, other.Response.StatusCode);
        Assert.Equal(1, fake.Previews);
    }

    [Theory]
    [InlineData("{\"payloadSha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"confirm\":false}")]
    [InlineData("{\"payloadSha256\":\"bad\",\"confirm\":true}")]
    [InlineData("{\"payloadSha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"confirm\":true,\"json\":{\"id\":0}}")]
    [InlineData("{\"payloadSha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"confirm\":true}")]
    public async Task Send_rejects_missing_confirmation_stale_shape_or_arbitrary_edited_payload(string body)
    {
        var fake = new FakeService();
        var context = Context(fake);
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        await GuardianProtocolCommandEndpoint.HandleSendAsync(context, Id);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(0, fake.Sends);
    }

    [Fact]
    public async Task Confirmed_command_calls_existing_backend_service_exactly_once_without_retry()
    {
        var fake = new FakeService();
        var context = Context(fake);
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"payloadSha256\":\"" + new string('A', 64) + "\",\"previewId\":\"" + new string('B', 64) + "\",\"confirm\":true}"));
        await GuardianProtocolCommandEndpoint.HandleSendAsync(context, Id);
        Assert.Equal(1, fake.Sends);
        Assert.Equal(0, fake.Previews);
    }

    private static DefaultHttpContext Context(FakeService fake, bool enabled = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new GuardianProtocolCommandOptions { Enabled = enabled, Environment = "HML", IntegrationId = Id, Contract = "SYNTHETIC_ONLY", OperatorSid = Sid });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AcmProtocolCommand:BearerSecret"] = Secret }).Build());
        services.AddSingleton<IGuardianProtocolCommandService>(fake);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers.Authorization = "Bearer " + Secret;
        context.Request.Headers["X-FugaTES-Operator-Sid"] = Sid;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FakeService : IGuardianProtocolCommandService
    {
        public int Previews, Sends;
        public Task<GuardianProtocolCommandView> PreviewAsync(Guid id, CancellationToken ct)
        { Previews++; return Task.FromResult(new GuardianProtocolCommandView { IntegrationId = id }); }
        public Task<GuardianProtocolCommandView> SendAsync(Guid id, string hash, string previewId, bool confirm, CancellationToken ct)
        { Sends++; return Task.FromResult(new GuardianProtocolCommandView { IntegrationId = id }); }
    }
}
