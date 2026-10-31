using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Integracao.Guardian;
using Xunit;

public sealed class ProtocolPreviewParityTests
{
    [Fact]
    public async Task Preview_serializer_and_automatic_adapter_use_identical_bytes_and_expose_safe_exchange()
    {
        var serializer = typeof(GuardianSet26HttpClient).GetMethod("SerializarPayloadR57");
        Assert.NotNull(serializer);
        var request = Request();
        var preview = Assert.IsType<byte[]>(serializer.Invoke(null, [request]));
        using var wire = new Capture(200);
        using var http = new HttpClient(wire);
        var client = Client(http);
        var result = await client.EnviarMinimoAcmAsync(request, default);
        Assert.Equal(preview, wire.Body);
        Assert.Equal(1, wire.ProtocolCalls);
        Assert.Equal(1, wire.AuthCalls);
        var diagnostic = typeof(GuardianSet26HttpClient).GetProperty("LastR57Diagnostic")?.GetValue(client);
        Assert.NotNull(diagnostic);
        var json = JsonSerializer.SerializeToElement(diagnostic);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(preview)), json.GetProperty("PayloadSha256").GetString());
        Assert.Equal(200, json.GetProperty("HttpStatus").GetInt32());
        Assert.Equal(result.Id, json.GetProperty("ProtocolId").GetInt64());
        Assert.DoesNotContain("SYNTH_PRIVATE_TOKEN", JsonSerializer.Serialize(diagnostic));
    }

    [Fact]
    public async Task Known_error_has_bounded_sanitized_body_for_manual_and_automatic_diagnostics_without_retry()
    {
        using var wire = new Capture(400);
        using var http = new HttpClient(wire);
        var client = Client(http);
        var failure = await Assert.ThrowsAsync<GuardianRestR57HttpException>(() => client.EnviarMinimoAcmAsync(Request(), default));
        var diagnostic = typeof(GuardianSet26HttpClient).GetProperty("LastR57Diagnostic")?.GetValue(client);
        Assert.NotNull(diagnostic);
        var json = JsonSerializer.SerializeToElement(diagnostic);
        Assert.Equal(400, json.GetProperty("HttpStatus").GetInt32());
        Assert.Contains("Item inexistente", json.GetProperty("SafeResponseBody").GetString());
        Assert.DoesNotContain("SYNTH_PRIVATE_TOKEN", JsonSerializer.Serialize(diagnostic));
        Assert.False(failure.ResultadoIndeterminado);
        Assert.Equal(1, wire.ProtocolCalls);
    }

    private static PreCadastroAcmR57 Request() => new("https://callback.example.invalid/receive",
        new(0, "SYNTH-ITEM", "SYNTH-EMITTER", "0000001139", null, "OC1139",
            [new(1000, "AC3P"), new(1002, "3011"), new(1003, "10")]) { IdAgenda = 1 })
        { CriacaoInicialSemDadosPesagem = true, CallbackHomologado = true };

    private static GuardianSet26HttpClient Client(HttpClient http)
    {
        var options = new GuardianHttpOptions { BaseUrl = new("https://guardian.example.invalid"),
            PreCadastroEndpoint = "/api/v1/Guardian/ProtocoloPreCadastro", Timeout = TimeSpan.FromSeconds(5) };
        return new(http, options, new GuardianTokenClient(http, options, new Credentials()));
    }

    private sealed class Credentials : ICredenciaisGuardianSet26Provider
    {
        public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct) => Task.FromResult(
            new CredenciaisGuardianSet26("synthetic@example.invalid", "SYNTH_PASSWORD", "SYNTH_SIGNATURE"));
    }

    private sealed class Capture(int status) : HttpMessageHandler
    {
        public byte[]? Body { get; private set; }
        public int ProtocolCalls { get; private set; }
        public int AuthCalls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v2/CreateToken")
            {
                AuthCalls++;
                return Reply(200, "{\"token\":\"SYNTH_PRIVATE_TOKEN\"}");
            }
            ProtocolCalls++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("SYNTH_PRIVATE_TOKEN", request.Headers.Authorization?.Parameter);
            Body = await request.Content!.ReadAsByteArrayAsync(ct);
            Assert.Equal(Body.Length, request.Content.Headers.ContentLength);
            Assert.Equal("application/json", request.Content.Headers.ContentType!.ToString());
            Assert.Equal("*/*", request.Headers.Accept.Single().ToString());
            return status == 200 ? Reply(200, "{\"id\":9139,\"codigoContrato\":\"0000001139\",\"codigoItem\":\"SYNTH-ITEM\",\"codigoEmissor\":\"SYNTH-EMITTER\",\"numeroReferencia\":\"OC1139\"}")
                : Reply(status, "{\"message\":\"Item inexistente\",\"token\":\"SYNTH_PRIVATE_TOKEN\"}");
        }
        private static HttpResponseMessage Reply(int code, string body) => new((HttpStatusCode)code)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
