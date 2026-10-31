using System.Net;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Integracao.Guardian;
using Xunit;

public sealed class GuardianRestEndpointTests
{
    [Fact]
    public async Task Root_relative_token_path_reaches_synthetic_transport_on_every_platform()
    {
        using var wire = new SyntheticWire();
        using var http = new HttpClient(wire);
        var credentials = new SyntheticCredentials();
        var client = new GuardianTokenClient(http, Options(), credentials);

        Assert.Equal("SYNTHETIC_TOKEN", await client.ObterTokenAsync(default));
        Assert.Equal("https://guardian.invalid:8443/api/v2/CreateToken",
            Assert.Single(wire.Requests).AbsoluteUri);
        Assert.Equal(1, credentials.Calls);
    }

    [Theory]
    [InlineData("https://guardian.invalid:8443/base/", "/api/v2/Carrier", "https://guardian.invalid:8443/api/v2/Carrier")]
    [InlineData("https://guardian.invalid:8443/base/", "api/v2/Carrier", "https://guardian.invalid:8443/base/api/v2/Carrier")]
    [InlineData("http://guardian.invalid:8080/base/", "/api/v2/Carrier", "http://guardian.invalid:8080/api/v2/Carrier")]
    [InlineData("https://guardian.invalid:8443/base/", "/api/v2/Carrier?next=https://other.invalid/", "https://guardian.invalid:8443/api/v2/Carrier?next=https://other.invalid/")]
    public async Task Relative_rest_paths_preserve_configured_scheme_host_and_port(
        string baseUrl, string endpoint, string expected)
    {
        using var wire = new SyntheticWire();
        using var http = new HttpClient(wire);
        var options = Options() with { BaseUrl = new Uri(baseUrl), TransportadoraEndpoint = endpoint };
        var client = new GuardianSet26HttpClient(http, options,
            new GuardianTokenClient(http, options, new SyntheticCredentials()));

        await client.EnviarAsync(new TransportadoraGuardianSet26("SYNTHETIC", "Synthetic", "Synthetic"), default);

        Assert.Equal(2, wire.Requests.Count);
        Assert.Equal(expected, wire.Requests[1].AbsoluteUri);
        Assert.All(wire.Requests, uri => {
            Assert.Equal(options.BaseUrl.Scheme, uri.Scheme);
            Assert.Equal(options.BaseUrl.Host, uri.Host);
            Assert.Equal(options.BaseUrl.Port, uri.Port);
        });
    }

    [Theory]
    [InlineData("http://other.invalid/token")]
    [InlineData("https://other.invalid/token")]
    [InlineData("https://guardian.invalid:8443/token")]
    [InlineData("https://guardian.invalid:9443/token")]
    [InlineData("file:///tmp/token")]
    [InlineData("file://other.invalid/token")]
    [InlineData("ftp://other.invalid/token")]
    [InlineData("//other.invalid/token")]
    [InlineData("//guardian.invalid:8443/token")]
    [InlineData(" \t//guardian.invalid:8443/token")]
    [InlineData("/\\other.invalid/token")]
    [InlineData("\\\\other.invalid\\token")]
    [InlineData("\\other.invalid\\token")]
    [InlineData("api\\v2\\Carrier")]
    [InlineData("C:\\temp\\token")]
    [InlineData(" \t//user@guardian.invalid:8443/token")]
    public async Task Absolute_network_and_backslash_paths_are_rejected_before_credentials_or_transport(string endpoint)
    {
        using var wire = new SyntheticWire();
        using var http = new HttpClient(wire);
        var credentials = new SyntheticCredentials();
        var options = Options() with { TransportadoraEndpoint = endpoint };
        var client = new GuardianSet26HttpClient(http, options,
            new GuardianTokenClient(http, options, credentials));

        var error = await Assert.ThrowsAsync<GuardianHttpException>(() =>
            client.EnviarAsync(new TransportadoraGuardianSet26("SYNTHETIC", "Synthetic", "Synthetic"), default));

        Assert.Equal("Guardian setting TransportadoraEndpoint must be a relative endpoint.", error.Message);
        Assert.Empty(wire.Requests);
        Assert.Equal(0, credentials.Calls);
    }

    private static GuardianHttpOptions Options() => new()
    {
        BaseUrl = new Uri("https://guardian.invalid:8443/base/"),
        Timeout = TimeSpan.FromSeconds(5)
    };

    private sealed class SyntheticCredentials : ICredenciaisGuardianSet26Provider
    {
        public int Calls { get; private set; }
        public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new CredenciaisGuardianSet26("SYNTHETIC", "SYNTHETIC", "SYNTHETIC"));
        }
    }

    // No socket handler or real authentication is composed by these tests.
    private sealed class SyntheticWire : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath == "/api/v2/CreateToken"
                    ? "{\"token\":\"SYNTHETIC_TOKEN\"}" : "{}")
            });
        }
    }
}
