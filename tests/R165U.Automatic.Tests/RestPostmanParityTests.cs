using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Integracao.Guardian;
using Xunit;

public sealed class RestPostmanParityTests
{
    private const string SyntheticToken = "SYNTHETIC_LOCAL_TOKEN_NOT_A_CREDENTIAL";
    private const string FrozenSha = "C5149CEF1609FE1EDC26FA7698A467738D2D5455A8323B149539F0AED7315EAB";
    private const string Expected = """
        {"configuracao":{"urlParaRetorno":"http://192.168.17.40/api/guardian/set26/callback/recebimento"},
        "dadosProtocoloPreCadastro":{"id":0,"codigoFluxo":null,"codigoItem":"7000000","codigoEmissor":"20000051",
        "codigoContrato":"0000001144","idAgenda":1,"numeroReferencia":"OC1144",
        "camposAdicionais":[{"numero":1000,"valor":"AC3P"},{"numero":1002,"valor":"3011"},{"numero":1003,"valor":"10"}]}}
        """;

    [Fact]
    public async Task Frozen_installed_binary_captures_real_HTTP_framing_without_contacting_Guardian()
    {
        var root = FindRoot();
        var binary = Path.Combine(root, "evidence/R165Y_AUTO_1144/delivery_R1/app/FugaTES.Integracao.Guardian.dll");
        Assert.Equal(FrozenSha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary))));
        var capture = await Capture(frozenBinary: binary);
        AssertPayload(capture);
        Assert.Equal("chunked", capture.Headers["Transfer-Encoding"]);
        Assert.False(capture.Headers.ContainsKey("Content-Length"));
        Assert.Equal("application/json; charset=utf-8", capture.Headers["Content-Type"]);
        Assert.False(capture.Headers.ContainsKey("Accept"));
        SaveCapture("INSTALLED", capture, FrozenSha);
    }

    [Fact]
    public async Task Candidate_matches_Postman_headers_and_sends_fixed_length_UTF8_JSON()
    {
        var capture = await Capture();
        AssertPayload(capture);
        Assert.False(capture.Headers.ContainsKey("Transfer-Encoding"));
        Assert.Equal(capture.Body.Length.ToString(), capture.Headers["Content-Length"]);
        Assert.Equal("application/json", capture.Headers["Content-Type"]);
        Assert.Equal("*/*", capture.Headers["Accept"]);
        Assert.False(capture.Body.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        SaveCapture("CANDIDATE", capture, Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(typeof(GuardianSet26HttpClient).Assembly.Location))));
    }

    private static void AssertPayload(WireCapture capture)
    {
        Assert.Equal("POST /api/v1/Guardian/ProtocoloPreCadastro HTTP/1.1", capture.RequestLine);
        Assert.Equal("guardian.cloudprix.com.br", capture.Headers["Host"]);
        Assert.True(capture.BearerMatchesSyntheticIssuedToken);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Expected), JsonNode.Parse(capture.Body)));
    }

    private static void SaveCapture(string variant, WireCapture capture, string assemblySha)
    {
        var allowedHeaders = new[] { "Host", "Content-Type", "Accept", "Content-Length", "Transfer-Encoding", "User-Agent" };
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"REST_PARITY_CAPTURE_{variant}.json"),
            JsonSerializer.Serialize(new
            {
                mode = "LOCAL_SYNTHETIC_HTTP_ONLY_URL_SCHEME_REWRITTEN_IN_TEST_NO_GUARDIAN_CALLS", assemblySha,
                endpoint = "https://guardian.cloudprix.com.br/api/v1/Guardian/ProtocoloPreCadastro",
                capture.RequestLine, headers = capture.Headers.Where(p => allowedHeaders.Contains(p.Key, StringComparer.OrdinalIgnoreCase)),
                authorization = new { scheme = "Bearer", matchesFreshSyntheticToken = capture.BearerMatchesSyntheticIssuedToken },
                requestPayloadSha256 = Convert.ToHexString(SHA256.HashData(capture.Body)),
                payload = JsonNode.Parse(capture.Body), guardianBusinessPosts = 0
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(Environment.GetEnvironmentVariable("FUGATES_TEST_EVIDENCE_ROOT") ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "evidence/R165Y_AUTO_1144/PILOT_1144_RESULT.json"))) return dir.FullName;
        throw new InvalidOperationException("Frozen approved evidence not found.");
    }

    private static async Task<WireCapture> Capture(string? frozenBinary = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            WireCapture? captured = null;
            for (var i = 0; i < 2; i++)
            {
                using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
                await using var stream = peer.GetStream();
                var wire = await ReadRequest(stream, deadline.Token);
                var body = i == 0 ? JsonSerializer.Serialize(new { token = SyntheticToken }) :
                    "{\"id\":805,\"codigoContrato\":\"0000001144\",\"codigoItem\":\"7000000\",\"codigoEmissor\":\"20000051\",\"numeroReferencia\":\"OC1144\"}";
                if (i == 0) Assert.StartsWith("POST /api/v2/CreateToken ", wire.RequestLine);
                else captured = wire;
                var bytes = Encoding.UTF8.GetBytes(body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), deadline.Token);
                await stream.WriteAsync(bytes, deadline.Token);
                await stream.FlushAsync(deadline.Token);
            }
            return captured!;
        }, deadline.Token);
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = async (ctx, ct) =>
            {
                Assert.Equal("guardian.cloudprix.com.br", ctx.DnsEndPoint.Host);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(IPAddress.Loopback, port, ct); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        };
        using var http = new HttpClient(new LocalOnlyTransport(handler));
        var options = new GuardianHttpOptions { BaseUrl = new("https://guardian.cloudprix.com.br"),
            Timeout = TimeSpan.FromSeconds(10), TokenCacheLifetime = TimeSpan.FromMinutes(10),
            PreCadastroEndpoint = "/api/v1/Guardian/ProtocoloPreCadastro" };
        var request = new PreCadastroAcmR57("http://192.168.17.40/api/guardian/set26/callback/recebimento",
            new(0, "7000000", "20000051", "0000001144", null, "OC1144",
                [new(1000, "AC3P"), new(1002, "3011"), new(1003, "10")]) { IdAgenda = 1 })
            { CriacaoInicialSemDadosPesagem = true, CallbackHomologado = true,
                CallbackBinding = new("HML", "http://192.168.17.40/api/guardian/set26/callback/recebimento",
                    "192.168.17.91/32", true, true, "SYNTHETIC_LOOPBACK_TEST_ONLY") };
        if (frozenBinary is null)
        {
            var tokens = new GuardianTokenClient(http, options, new FakeCredentials());
            await tokens.ObterTokenAsync(deadline.Token);
            var result = await new GuardianSet26HttpClient(http, options, tokens)
                .EnviarMinimoAcmAsync(request, deadline.Token);
            Assert.Equal(805, result.Id);
        }
        else
        {
            var context = new AssemblyLoadContext("FrozenGuardianParity", isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(frozenBinary);
                var optionType = assembly.GetType(typeof(GuardianHttpOptions).FullName!)!;
                var frozenOptions = Activator.CreateInstance(optionType)!;
                foreach (var property in typeof(GuardianHttpOptions).GetProperties().Where(p => p.CanWrite))
                    optionType.GetProperty(property.Name)!.SetValue(frozenOptions, property.GetValue(options));
                var tokenType = assembly.GetType(typeof(GuardianTokenClient).FullName!)!;
                var tokens = Activator.CreateInstance(tokenType, http, frozenOptions, new FakeCredentials())!;
                await (Task<string>)tokenType.GetMethod("ObterTokenAsync")!.Invoke(tokens, [deadline.Token])!;
                var clientType = assembly.GetType(typeof(GuardianSet26HttpClient).FullName!)!;
                var client = Activator.CreateInstance(clientType, http, frozenOptions, tokens)!;
                var task = (Task<ProtocoloAcmR57>)clientType.GetMethod("EnviarMinimoAcmAsync")!.Invoke(client, [request, deadline.Token])!;
                Assert.Equal(805, (await task).Id);
            }
            finally { context.Unload(); }
        }
        return await server.WaitAsync(deadline.Token);
    }

    private sealed record WireCapture(string RequestLine, Dictionary<string, string> Headers, byte[] Body, bool BearerMatchesSyntheticIssuedToken);

    // Only this test transport changes HTTPS to local HTTP. Production guards and TLS remain unchanged.
    private sealed class LocalOnlyTransport(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("guardian.cloudprix.com.br", request.RequestUri.Host);
            request.RequestUri = new UriBuilder(request.RequestUri) { Scheme = "http", Port = 80 }.Uri;
            return base.SendAsync(request, ct);
        }
    }

    private static async Task<WireCapture> ReadRequest(Stream stream, CancellationToken ct)
    {
        async Task<string> Line()
        {
            using var line = new MemoryStream();
            var one = new byte[1];
            while (line.Length < 16384)
            {
                await stream.ReadExactlyAsync(one, ct);
                line.WriteByte(one[0]);
                if (one[0] == '\n') return Encoding.ASCII.GetString(line.ToArray()).TrimEnd('\r', '\n');
            }
            throw new InvalidDataException("Synthetic header limit exceeded.");
        }
        var first = await Line();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var line = await Line(); line.Length > 0; line = await Line())
        {
            var split = line.IndexOf(':');
            headers.Add(line[..split], line[(split + 1)..].Trim());
        }
        using var body = new MemoryStream();
        if (headers.GetValueOrDefault("Transfer-Encoding") == "chunked")
        {
            while (true)
            {
                var length = Convert.ToInt32((await Line()).Split(';')[0], 16);
                Assert.InRange(length, 0, 65536);
                if (length == 0) { Assert.Equal("", await Line()); break; }
                var bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, ct);
                body.Write(bytes);
                Assert.Equal("", await Line());
            }
        }
        else if (headers.TryGetValue("Content-Length", out var size))
        {
            var bytes = new byte[int.Parse(size)];
            Assert.InRange(bytes.Length, 0, 65536);
            await stream.ReadExactlyAsync(bytes, ct);
            body.Write(bytes);
        }
        var matches = headers.Remove("Authorization", out var auth) && auth == "Bearer " + SyntheticToken;
        return new(first, headers, body.ToArray(), matches);
    }

    private sealed class FakeCredentials : ICredenciaisGuardianSet26Provider
    {
        public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct) => Task.FromResult(
            new CredenciaisGuardianSet26("synthetic@example.invalid", "SYNTHETIC_PASSWORD", "SYNTHETIC_SIGNATURE"));
    }
}
