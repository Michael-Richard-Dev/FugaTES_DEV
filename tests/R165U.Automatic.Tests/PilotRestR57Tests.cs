using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Integracao.Guardian;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class PilotRestR57Tests
{
    private const string Token = "SYNTHETIC_PRIVATE_TOKEN";
    private const string ValidResponse = """
        {"id":805,"codigoContrato":"SYNTH-CONTRACT","codigoItem":"SYNTH-ITEM",
         "codigoEmissor":"SYNTH-EMITTER","numeroReferencia":"OC-SYNTH"}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Initial_payload_has_explicit_null_flow_and_no_weighing_requirements(bool ignoreNulls)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = ignoreNulls ? JsonIgnoreCondition.WhenWritingNull : JsonIgnoreCondition.Never
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            GuardianSet26HttpClient.CriarPayloadR57(Request()), options));
        AssertInitialPayload(json.RootElement);
    }

    [Fact]
    public void Legacy_payload_preserves_supplied_flow_and_carrier_verbatim()
    {
        var request = Request();
        request = request with
        {
            CriacaoInicialSemDadosPesagem = false,
            Dados = request.Dados with { CodigoFluxo = "LEGACY_001", CodigoTransportadora = "00002", IdAgenda = null }
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            GuardianSet26HttpClient.CriarPayloadR57(request), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var data = json.RootElement.GetProperty("dadosProtocoloPreCadastro");
        Assert.Equal("LEGACY_001", data.GetProperty("codigoFluxo").GetString());
        Assert.Equal("00002", data.GetProperty("codigoTransportadora").GetString());
    }

    [Theory]
    [InlineData(200, null)]
    [InlineData(201, null)]
    [InlineData(200, "SYNTH-TICKET")]
    public async Task Success_requires_matching_identity_but_not_a_ticket(int status, string? ticket)
    {
        using var wire = new Wire((_, _) => Reply(status, ValidResponse[..^1] +
            ",\"numeroTicket\":" + JsonSerializer.Serialize(ticket) + "}"));
        using var http = new HttpClient(wire);
        var result = await Client(http).EnviarMinimoAcmAsync(Request(), default);

        Assert.Equal(805, result.Id);
        Assert.Equal(ticket, result.NumeroTicket);
        Assert.Equal(1, wire.Posts);
        Assert.Equal(1, wire.TokenRequests);
        using var sent = JsonDocument.Parse(wire.SentBody!);
        AssertInitialPayload(sent.RootElement);
        Assert.Equal("https://guardian.invalid:8443/api/v1/Guardian/ProtocoloPreCadastro", wire.SentUri);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(408)]
    [InlineData(409)]
    [InlineData(422)]
    [InlineData(429)]
    [InlineData(499)]
    public async Task Http_4xx_is_a_known_rejection_without_retry(int status)
    {
        using var wire = new Wire((_, _) => Reply(status, "{\"message\":\"Item inexistente\"}"));
        using var http = new HttpClient(wire);
        var error = await Failure(Client(http));
        AssertFailure(error, status, $"GUARDIAN_R57_HTTP_{status}", false);
        Assert.Equal("Item inexistente", Property<string>(error, "SafeBusinessMessage"));
        Assert.Equal(1, wire.Posts);
        Assert.Equal(1, wire.TokenRequests);
    }

    [Theory]
    [InlineData(400, "not-json")]
    [InlineData(422, "{\"message\":\"one\",\"message\":\"two\"}")]
    [InlineData(409, "[]")]
    [InlineData(403, "{\"token\":\"PRIVATE_SECRET\"}")]
    public async Task Malformed_or_unrecognized_error_body_does_not_hide_known_http_rejection(int status, string body)
    {
        using var wire = new Wire((_, _) => Reply(status, body));
        using var http = new HttpClient(wire);
        var error = await Failure(Client(http));
        AssertFailure(error, status, $"GUARDIAN_R57_HTTP_{status}", false);
        Assert.Null(Property<string>(error, "SafeBusinessMessage"));
        Assert.DoesNotContain(body, error.ToString());
        Assert.Equal(1, wire.Posts);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(302)]
    public async Task Non_4xx_http_failure_remains_unknown_without_retry(int status)
    {
        using var wire = new Wire((_, _) => Reply(status, "{\"message\":\"Temporary failure\"}"));
        using var http = new HttpClient(wire);
        AssertFailure(await Failure(Client(http)), status, $"GUARDIAN_R57_HTTP_{status}", true);
        Assert.Equal(1, wire.Posts);
    }

    [Theory]
    [InlineData("{\"id\":0}")]
    [InlineData("{\"id\":-1}")]
    [InlineData("{\"id\":\"805\"}")]
    [InlineData("{\"id\":null}")]
    [InlineData("{\"id\":1.5}")]
    [InlineData("{\"id\":9223372036854775808}")]
    [InlineData("{\"id\":805}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not-json PRIVATE_BODY")]
    [InlineData("")]
    public async Task A_2xx_response_without_valid_id_and_identities_is_unknown(string body)
    {
        using var wire = new Wire((_, _) => Reply(200, body));
        using var http = new HttpClient(wire);
        var error = await Failure(Client(http));
        AssertFailure(error, 200, "GUARDIAN_R57_RESPONSE_INVALID", true);
        Assert.Null(Property<string>(error, "SafeBusinessMessage"));
        Assert.DoesNotContain("PRIVATE_BODY", error.ToString());
        Assert.Equal(1, wire.Posts);
    }

    [Theory]
    [InlineData("codigoContrato")]
    [InlineData("codigoItem")]
    [InlineData("codigoEmissor")]
    [InlineData("numeroReferencia")]
    public async Task Every_echoed_identity_must_match_exactly(string field)
    {
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ValidResponse)!;
        body[field] = JsonSerializer.SerializeToElement("WRONG");
        using var wire = new Wire((_, _) => Reply(201, JsonSerializer.Serialize(body)));
        using var http = new HttpClient(wire);
        AssertFailure(await Failure(Client(http)), 201, "GUARDIAN_R57_RESPONSE_INVALID", true);
        Assert.Equal(1, wire.Posts);
    }

    [Theory]
    [InlineData(",\"id\":806")]
    [InlineData(",\"numeroTicket\":\" \"")]
    [InlineData(",\"numeroTicket\":45")]
    public async Task Ambiguous_identity_or_malformed_ticket_is_not_success(string extra)
    {
        using var wire = new Wire((_, _) => Reply(200, ValidResponse[..^1] + extra + "}"));
        using var http = new HttpClient(wire);
        AssertFailure(await Failure(Client(http)), 200, "GUARDIAN_R57_RESPONSE_INVALID", true);
    }

    [Theory]
    [InlineData(202)]
    [InlineData(204)]
    [InlineData(206)]
    public async Task Arbitrary_2xx_is_not_success_without_a_valid_protocol(int status)
    {
        using var wire = new Wire((_, _) => Reply(status, "{}"));
        using var http = new HttpClient(wire);
        AssertFailure(await Failure(Client(http)), status, "GUARDIAN_R57_RESPONSE_INVALID", true);
    }

    [Fact]
    public async Task Timeout_after_dispatch_is_unknown_and_does_not_retry()
    {
        using var wire = new Wire(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(wire);
        AssertFailure(await Failure(Client(http, TimeSpan.FromMilliseconds(100))), 0, "GUARDIAN_R57_TIMEOUT", true);
        Assert.Equal(1, wire.Posts);
    }

    [Fact]
    public async Task Transport_failure_is_unknown_and_does_not_expose_inner_exception()
    {
        using var wire = new Wire((_, _) => throw new HttpRequestException("PRIVATE_TOKEN PRIVATE_EMAIL"));
        using var http = new HttpClient(wire);
        var error = await Failure(Client(http));
        AssertFailure(error, 0, "GUARDIAN_R57_TRANSPORT_FAILURE", true);
        Assert.DoesNotContain("PRIVATE_", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(1, wire.Posts);
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(503, true)]
    [InlineData(200, true)]
    public async Task Body_read_failure_preserves_received_status_and_never_retries(int status, bool unknown)
    {
        using var wire = new Wire((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StreamContent(new BrokenStream())
        }));
        using var http = new HttpClient(wire);
        var error = await Failure(Client(http));
        AssertFailure(error, status, status == 200 ? "GUARDIAN_R57_RESPONSE_INVALID" : $"GUARDIAN_R57_HTTP_{status}", unknown);
        Assert.DoesNotContain("PRIVATE_BODY", error.ToString());
        Assert.Equal(1, wire.Posts);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(503)]
    public async Task Oversized_error_body_is_not_retained(int status)
    {
        using var wire = new Wire((_, _) => Reply(status,
            "{\"message\":\"" + new string('x', 10000) + "PRIVATE_SECRET\"}"));
        using var http = new HttpClient(wire);
        var error = await Failure(Client(http));
        AssertFailure(error, status, $"GUARDIAN_R57_HTTP_{status}", status >= 500);
        Assert.Null(Property<string>(error, "SafeBusinessMessage"));
        Assert.DoesNotContain("PRIVATE_SECRET", error.ToString());
        Assert.Equal(1, wire.Posts);
    }

    [Fact]
    public async Task Business_message_is_bounded_redacted_and_not_the_response_body()
    {
        var privateValues = new[] { "12.345.678/0001-90", "12345678000190", "person@example.invalid",
            Token, "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJwcml2YXRlIn0.signature1234567890", "PRIVATE_PASSWORD", "PRIVATE_SIGNATURE" };
        var message = "Item inexistente\r\n" + string.Join(" ", privateValues[..5]) +
            " senha=PRIVATE_PASSWORD assinatura=PRIVATE_SIGNATURE " + new string('x', 700);
        using var wire = new Wire((_, _) => Reply(422, JsonSerializer.Serialize(new
        {
            message, password = "NEVER_COPY_THE_BODY", token = Token
        })));
        using var http = new HttpClient(wire);
        var error = await Failure(Client(http));
        AssertFailure(error, 422, "GUARDIAN_R57_HTTP_422", false);
        var safe = Property<string>(error, "SafeBusinessMessage");
        Assert.NotNull(safe);
        Assert.StartsWith("Item inexistente", safe);
        Assert.InRange(safe.Length, 1, 256);
        Assert.DoesNotContain(safe, c => char.IsControl(c));
        foreach (var value in privateValues)
        {
            Assert.DoesNotContain(value, safe);
            Assert.DoesNotContain(value, error.ToString());
        }
        Assert.DoesNotContain("NEVER_COPY_THE_BODY", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("email=PRIVATE_EMAIL")]
    [InlineData("token: PRIVATE_TOKEN")]
    [InlineData("Bearer PRIVATE_BEARER")]
    [InlineData("password = \"PRIVATE_PASSWORD WITH SPACES\"")]
    [InlineData("assinatura: PRIVATE_SIGNATURE")]
    [InlineData("accessToken: PRIVATE_ACCESS")]
    [InlineData("refresh-token: PRIVATE_REFRESH")]
    [InlineData("api_key: PRIVATE_KEY")]
    [InlineData("clientSecret: PRIVATE_SECRET")]
    public async Task Credential_labels_redact_the_entire_remaining_value(string sensitive)
    {
        using var wire = new Wire((_, _) => Reply(400, JsonSerializer.Serialize(new { message = "Rejeitado; " + sensitive })));
        using var http = new HttpClient(wire);
        var error = await Failure(Client(http));
        var safe = Property<string>(error, "SafeBusinessMessage");
        Assert.NotNull(safe);
        Assert.DoesNotContain("PRIVATE_", safe);
    }

    [Fact]
    public async Task Business_message_length_is_limited_even_without_sensitive_content()
    {
        using var wire = new Wire((_, _) => Reply(422, JsonSerializer.Serialize(new { message = string.Concat(Enumerable.Repeat("Item inexistente. ", 100)) })));
        using var http = new HttpClient(wire);
        var safe = Property<string>(await Failure(Client(http)), "SafeBusinessMessage");
        Assert.NotNull(safe);
        Assert.Equal(256, safe.Length);
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(503, true)]
    [InlineData(200, true)]
    public async Task Timeout_while_reading_body_preserves_known_rejection_or_unknown_effect(int status, bool unknown)
    {
        using var wire = new Wire((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StreamContent(new SlowStream())
        }));
        using var http = new HttpClient(wire);
        var error = await Failure(Client(http, TimeSpan.FromMilliseconds(100)));
        AssertFailure(error, status, status == 200 ? "GUARDIAN_R57_TIMEOUT" : $"GUARDIAN_R57_HTTP_{status}", unknown);
        Assert.Equal(1, wire.Posts);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Authorization_rejection_invalidates_cached_token_but_does_not_retry(int status)
    {
        using var wire = new Wire((_, _) => Reply(status, "{}"));
        using var http = new HttpClient(wire);
        var client = Client(http);
        await Failure(client);
        Assert.Equal(1, wire.Posts);
        Assert.Equal(1, wire.TokenRequests);
        // A separate caller invocation is not an internal POST retry.
        await Failure(client);
        Assert.Equal(2, wire.Posts);
        Assert.Equal(2, wire.TokenRequests);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Auth_http_failure_is_known_before_protocol_dispatch_and_never_retried(int status)
    {
        using var wire = new Wire((_, _) => Reply(200, ValidResponse),
            _ => Reply(status, "{\"message\":\"SECRET_EMAIL person@example.invalid password=SECRET_PASSWORD token=SECRET_TOKEN\"}"));
        using var http = new HttpClient(wire);
        AssertSafeAuthFailure(await Failure(Client(http)), status, $"GUARDIAN_R57_AUTH_HTTP_{status}");
        Assert.Equal(1, wire.TokenRequests);
        Assert.Equal(0, wire.Posts);
        Assert.Null(wire.SentBody);
    }

    [Theory]
    [InlineData(200, "not-json SECRET_BODY")]
    [InlineData(200, "{}")]
    [InlineData(200, "[]")]
    [InlineData(204, "")]
    [InlineData(200, "{\"token\":\" \"}")]
    [InlineData(200, "{\"token\":123}")]
    public async Task Auth_invalid_response_is_known_before_protocol_dispatch(int status, string body)
    {
        using var wire = new Wire((_, _) => Reply(200, ValidResponse), _ => Reply(status, body));
        using var http = new HttpClient(wire);
        AssertSafeAuthFailure(await Failure(Client(http)), status, "GUARDIAN_R57_AUTH_PRE_SEND_FAILED");
        Assert.Equal(1, wire.TokenRequests);
        Assert.Equal(0, wire.Posts);
    }

    [Fact]
    public async Task Auth_timeout_does_not_become_an_unknown_protocol_outcome_or_retry()
    {
        using var wire = new Wire((_, _) => Reply(200, ValidResponse), async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return await Reply(200, "{}");
        });
        using var http = new HttpClient(wire);
        AssertSafeAuthFailure(await Failure(Client(http, TimeSpan.FromMilliseconds(100))), 0, "GUARDIAN_R57_AUTH_PRE_SEND_FAILED");
        Assert.Equal(1, wire.TokenRequests);
        Assert.Equal(0, wire.Posts);
    }

    [Fact]
    public async Task Auth_transport_error_does_not_retain_raw_exception_or_dispatch_protocol()
    {
        using var wire = new Wire((_, _) => Reply(200, ValidResponse),
            _ => throw new HttpRequestException("SECRET_TOKEN person@example.invalid"));
        using var http = new HttpClient(wire);
        AssertSafeAuthFailure(await Failure(Client(http)), 0, "GUARDIAN_R57_AUTH_PRE_SEND_FAILED");
        Assert.Equal(1, wire.TokenRequests);
        Assert.Equal(0, wire.Posts);
    }

    [Fact]
    public async Task Auth_provider_error_is_sanitized_before_any_http_request()
    {
        using var wire = new Wire((_, _) => Reply(200, ValidResponse));
        using var http = new HttpClient(wire);
        AssertSafeAuthFailure(await Failure(Client(http, credentials: new FaultingCredentials())),
            0, "GUARDIAN_R57_AUTH_PRE_SEND_FAILED");
        Assert.Equal(0, wire.TokenRequests);
        Assert.Equal(0, wire.Posts);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(500)]
    public async Task Auth_failure_is_saved_as_known_nonretryable_by_existing_pipeline_catch(int status)
    {
        using var wire = new Wire((_, _) => Reply(200, ValidResponse), _ => Reply(status, "SECRET_BODY"));
        using var http = new HttpClient(wire);
        var fixture = new AutomaticFixture();
        var (version, claim) = fixture.Add("880099");
        var options = AutomaticFixture.Options();
        var pipeline = new AcmR57Pipeline(fixture.Versions, new AcmR57NormalPreparation(options),
            new AcmR57NormalRuntimeGate(options), fixture.Soap, Client(http), fixture.Journal, fixture.H4);

        var result = await pipeline.ExecutarAsync(claim, default);

        Assert.Equal(StatusFluxoMvp.FalhaNaoReprocessavel, result.Status);
        Assert.Equal("R165Y_REST_REJECTED", result.Etapa);
        Assert.Equal($"GUARDIAN_R57_AUTH_HTTP_{status}", result.Erro);
        Assert.False(result.ErroResultadoIndeterminado);
        Assert.Equal(false, result.ErroReprocessavel);
        Assert.Equal(result, fixture.Journal.Results[version.IntegracaoId]);
        Assert.Null(fixture.H4.Items[version.IntegracaoId].ProtocoloIdGuardian);
        Assert.Equal(1, wire.TokenRequests);
        Assert.Equal(0, wire.Posts);

        Assert.Equal(result, await pipeline.ExecutarAsync(claim, default));
        Assert.Equal(1, wire.TokenRequests);
        Assert.Equal(0, wire.Posts);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task Auth_legacy_client_keeps_original_exception_type_with_optional_http_status(int status)
    {
        using var wire = new Wire((_, _) => Reply(200, ValidResponse), _ => Reply(status, "SECRET_BODY"));
        using var http = new HttpClient(wire);
        var client = new GuardianTokenClient(http, new GuardianHttpOptions
        {
            BaseUrl = new Uri("https://guardian.invalid/"), Timeout = TimeSpan.FromSeconds(5)
        }, new Credentials());
        var error = await Assert.ThrowsAsync<GuardianHttpException>(() => client.ObterTokenAsync(default));
        Assert.Equal($"Guardian token request failed with HTTP {status}.", error.Message);
        Assert.Equal(status, Property<int?>(error, "HttpStatusCode"));
        Assert.Null(error.BusinessErrorCode);
        Assert.Equal(1, wire.TokenRequests);
        Assert.Equal(0, wire.Posts);
    }

    [Fact]
    public async Task Auth_caller_cancellation_remains_cancellation_without_protocol_dispatch()
    {
        using var wire = new Wire((_, _) => Reply(200, ValidResponse), async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return await Reply(200, "{}");
        });
        using var http = new HttpClient(wire);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).EnviarMinimoAcmAsync(Request(), cancellation.Token));
        Assert.Equal(1, wire.TokenRequests);
        Assert.Equal(0, wire.Posts);
    }

    [Fact]
    public async Task Diagnostic_di_emits_one_warning_with_only_safe_fields_and_reference()
    {
        var message = "Item inexistente; 12.345.678/0001-90 person@example.invalid " + Token + " senha=PRIVATE_PASSWORD";
        using var wire = new Wire((_, _) => Reply(400, JsonSerializer.Serialize(new { message, raw = "PRIVATE_BODY" })));
        using var http = new HttpClient(wire);
        using var recording = new DiagnosticLoggerProvider();
        using var services = DiagnosticServices(http, recording);
        using var scope = services.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IAcmPreCadastroMinimoGatewayR57>();
        var request = Request();
        request = request with { Dados = request.Dados with { NumeroReferencia = "OC1144" } };

        var error = await Assert.ThrowsAsync<GuardianRestR57HttpException>(() => gateway.EnviarMinimoAcmAsync(request, default));

        AssertFailure(error, 400, "GUARDIAN_R57_HTTP_400", false);
        var entry = Assert.Single(recording.Events);
        Assert.Equal(typeof(GuardianSet26HttpClient).FullName, entry.Category);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("GuardianR57Rejected", entry.Event.Name);
        Assert.Null(entry.Exception);
        var values = entry.State.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(new[] { "AuthenticationContext", "CorrelationId", "GuardianErrorCode", "HttpStatusCode", "Reference", "RequestAccept", "RequestContentType", "RequestHost", "RequestId", "RequestMethod", "RequestPayloadFingerprint", "RequestTransferMode", "RequestUrl", "ResponseBodyFingerprint", "ResponseBodyKind", "ResponseContentType", "ResponseRequestId", "SafeBusinessMessage", "SafeCode", "SafeResponseBody", "{OriginalFormat}" }, values.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(400, values["HttpStatusCode"]);
        Assert.Equal("OC1144", values["Reference"]);
        Assert.Equal("GUARDIAN_R57_HTTP_400", values["SafeCode"]);
        Assert.Equal(error.SafeBusinessMessage, values["SafeBusinessMessage"]);
        Assert.Equal(error.SafeResponseBody, values["SafeResponseBody"]);
        Assert.Equal("FIXED_CONTENT_LENGTH", values["RequestTransferMode"]);
        Assert.NotNull(error.SafeBusinessMessage);
        Assert.InRange(error.SafeBusinessMessage.Length, 1, 256);
        foreach (var secret in new[] { "12.345.678/0001-90", "person@example.invalid", Token, "PRIVATE_PASSWORD", "PRIVATE_BODY" })
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(entry.State) + entry.Message);
        Assert.Equal(1, wire.Posts);
        Assert.Equal(1, wire.TokenRequests);
    }

    [Theory]
    [InlineData(400, "GuardianR57Rejected", 16557, LogLevel.Warning)]
    [InlineData(200, "GuardianR57Accepted", 16558, LogLevel.Information)]
    public async Task Diagnostic_framework_json_formatter_preserves_structured_fields_without_exception_or_payload(
        int status, string eventName, int eventId, LogLevel level)
    {
        using var wire = new Wire((_, _) => Reply(status, status == 400
            ? "{\"message\":\"Item inexistente\",\"raw\":\"PRIVATE_BODY\"}" : ValidResponse[..^1] + ",\"raw\":\"PRIVATE_BODY\"}"));
        using var http = new HttpClient(wire);
        using var recording = new DiagnosticLoggerProvider();
        using var services = DiagnosticServices(http, recording, LogLevel.Information);
        using var scope = services.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IAcmPreCadastroMinimoGatewayR57>();
        if (status == 400)
            await Assert.ThrowsAsync<GuardianRestR57HttpException>(() => gateway.EnviarMinimoAcmAsync(Request(), default));
        else
            Assert.Equal(805, (await gateway.EnviarMinimoAcmAsync(Request(), default)).Id);
        var entry = Assert.Single(recording.Events);
        Assert.Equal(eventName, entry.Event.Name);
        Assert.Equal(eventId, entry.Event.Id);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:Console:FormatterName"] = "json"
        }).Build();
        using var formatters = new ServiceCollection().AddLogging(logging =>
            logging.AddConfiguration(config.GetSection("Logging")).AddConsole()).BuildServiceProvider();
        var selected = formatters.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName;
        Assert.Equal("json", selected);
        var formatter = Assert.Single(formatters.GetServices<ConsoleFormatter>(), candidate => candidate.Name == selected);
        using var output = new StringWriter();
        var log = new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(entry.Level, entry.Category,
            entry.Event, entry.State, entry.Exception, (_, _) => entry.Message);
        formatter.Write(in log, null, output);

        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal(level.ToString(), root.GetProperty("LogLevel").GetString());
        Assert.Equal(eventId, root.GetProperty("EventId").GetInt32());
        var state = root.GetProperty("State");
        Assert.Equal(status, state.GetProperty("HttpStatusCode").GetInt32());
        Assert.StartsWith(eventName, root.GetProperty("Message").GetString());
        if (status == 400)
        {
            Assert.Equal("GUARDIAN_R57_HTTP_400", state.GetProperty("SafeCode").GetString());
            Assert.Equal("Item inexistente", state.GetProperty("SafeBusinessMessage").GetString());
        }
        else
        {
            Assert.Equal(805, state.GetProperty("ProtocolId").GetInt64());
            Assert.False(state.TryGetProperty("SafeBusinessMessage", out _));
        }
        Assert.Equal("OC-SYNTH", state.GetProperty("Reference").GetString());
        Assert.False(root.TryGetProperty("Exception", out _));
        Assert.DoesNotContain("PRIVATE_BODY", output.ToString());
        Assert.Equal(1, wire.Posts);
    }

    [Fact]
    public async Task Diagnostic_logger_failure_cannot_mask_known_http_rejection_or_trigger_retry()
    {
        using var wire = new Wire((_, _) => Reply(400, "{\"message\":\"Item inexistente\"}"));
        using var http = new HttpClient(wire);
        using var recording = new DiagnosticLoggerProvider(throwOnLog: true);
        using var services = DiagnosticServices(http, recording);
        using var scope = services.CreateScope();
        var error = await Assert.ThrowsAsync<GuardianRestR57HttpException>(() => scope.ServiceProvider
            .GetRequiredService<IAcmPreCadastroMinimoGatewayR57>().EnviarMinimoAcmAsync(Request(), default));
        AssertFailure(error, 400, "GUARDIAN_R57_HTTP_400", false);
        Assert.Equal("Item inexistente", error.SafeBusinessMessage);
        Assert.Single(recording.Events);
        Assert.DoesNotContain("PRIVATE_LOGGER_FAILURE", error.ToString());
        Assert.Equal(1, wire.Posts);
        Assert.Equal(1, wire.TokenRequests);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(422)]
    [InlineData(500)]
    public async Task Diagnostic_is_scoped_to_http_400(int status)
    {
        using var wire = new Wire((_, _) => Reply(status, "{\"message\":\"Other result\"}"));
        using var http = new HttpClient(wire);
        using var recording = new DiagnosticLoggerProvider();
        using var services = DiagnosticServices(http, recording);
        using var scope = services.CreateScope();
        var error = await Assert.ThrowsAsync<GuardianRestR57HttpException>(() => scope.ServiceProvider
            .GetRequiredService<IAcmPreCadastroMinimoGatewayR57>().EnviarMinimoAcmAsync(Request(), default));
        AssertFailure(error, status, $"GUARDIAN_R57_HTTP_{status}", status >= 500);
        Assert.Empty(recording.Events);
        Assert.Equal(1, wire.Posts);
    }

    [Fact]
    public async Task Diagnostic_is_not_emitted_for_auth_failure_before_protocol_post()
    {
        using var wire = new Wire((_, _) => Reply(200, ValidResponse), _ => Reply(400, "PRIVATE_AUTH_BODY"));
        using var http = new HttpClient(wire);
        using var recording = new DiagnosticLoggerProvider();
        using var services = DiagnosticServices(http, recording);
        using var scope = services.CreateScope();
        var error = await Assert.ThrowsAsync<GuardianRestR57HttpException>(() => scope.ServiceProvider
            .GetRequiredService<IAcmPreCadastroMinimoGatewayR57>().EnviarMinimoAcmAsync(Request(), default));
        AssertFailure(error, 400, "GUARDIAN_R57_AUTH_HTTP_400", false);
        Assert.Empty(recording.Events);
        Assert.Equal(0, wire.Posts);
    }

    [Theory]
    [InlineData(LogLevel.Warning, 1)]
    [InlineData(LogLevel.Error, 0)]
    public async Task Diagnostic_respects_provider_log_level_and_keeps_known_status_with_unreadable_body(LogLevel minimum, int events)
    {
        using var wire = new Wire((_, _) => Reply(400, "not-json PRIVATE_BODY"));
        using var http = new HttpClient(wire);
        using var recording = new DiagnosticLoggerProvider();
        using var services = DiagnosticServices(http, recording, minimum);
        using var scope = services.CreateScope();
        var error = await Assert.ThrowsAsync<GuardianRestR57HttpException>(() => scope.ServiceProvider
            .GetRequiredService<IAcmPreCadastroMinimoGatewayR57>().EnviarMinimoAcmAsync(Request(), default));
        AssertFailure(error, 400, "GUARDIAN_R57_HTTP_400", false);
        Assert.Null(error.SafeBusinessMessage);
        Assert.Equal(events, recording.Events.Count);
        if (events == 1) Assert.Null(Assert.Single(recording.Events).State.Single(pair => pair.Key == "SafeBusinessMessage").Value);
        Assert.Equal(1, wire.Posts);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    public async Task Diagnostic_accepted_is_information_only_after_valid_identity_with_minimal_state(int status)
    {
        using var wire = new Wire((_, _) => Reply(status, ValidResponse.Replace("OC-SYNTH", "OC1144", StringComparison.Ordinal)));
        using var http = new HttpClient(wire);
        using var recording = new DiagnosticLoggerProvider();
        using var services = DiagnosticServices(http, recording, LogLevel.Information);
        using var scope = services.CreateScope();
        var request = Request();
        request = request with { Dados = request.Dados with { NumeroReferencia = "OC1144" } };
        var result = await scope.ServiceProvider.GetRequiredService<IAcmPreCadastroMinimoGatewayR57>().EnviarMinimoAcmAsync(request, default);
        Assert.Equal(805, result.Id);
        var entry = Assert.Single(recording.Events);
        Assert.Equal("GuardianR57Accepted", entry.Event.Name);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        var values = entry.State.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(new[] { "HttpStatusCode", "ProtocolId", "Reference", "{OriginalFormat}" }, values.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(status, values["HttpStatusCode"]);
        Assert.Equal(805L, values["ProtocolId"]);
        Assert.Equal("OC1144", values["Reference"]);
        Assert.Equal(1, wire.Posts);
        Assert.Equal(1, wire.TokenRequests);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    public async Task Diagnostic_invalid_2xx_never_emits_accepted(int status)
    {
        using var wire = new Wire((_, _) => Reply(status, "{}"));
        using var http = new HttpClient(wire);
        using var recording = new DiagnosticLoggerProvider();
        using var services = DiagnosticServices(http, recording, LogLevel.Information);
        using var scope = services.CreateScope();
        var error = await Assert.ThrowsAsync<GuardianRestR57HttpException>(() => scope.ServiceProvider
            .GetRequiredService<IAcmPreCadastroMinimoGatewayR57>().EnviarMinimoAcmAsync(Request(), default));
        AssertFailure(error, status, "GUARDIAN_R57_RESPONSE_INVALID", true);
        Assert.Empty(recording.Events);
        Assert.Equal(1, wire.Posts);
    }

    [Fact]
    public async Task Diagnostic_accepted_logger_failure_does_not_lose_valid_protocol_or_retry()
    {
        using var wire = new Wire((_, _) => Reply(200, ValidResponse));
        using var http = new HttpClient(wire);
        using var recording = new DiagnosticLoggerProvider(throwOnLog: true);
        using var services = DiagnosticServices(http, recording, LogLevel.Information);
        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IAcmPreCadastroMinimoGatewayR57>().EnviarMinimoAcmAsync(Request(), default);
        Assert.Equal(805, result.Id);
        Assert.Equal("GuardianR57Accepted", Assert.Single(recording.Events).Event.Name);
        Assert.Equal(1, wire.Posts);
        Assert.Equal(1, wire.TokenRequests);
    }

    [Fact]
    public async Task Diagnostic_accepted_respects_warning_threshold_without_changing_success()
    {
        using var wire = new Wire((_, _) => Reply(200, ValidResponse));
        using var http = new HttpClient(wire);
        using var recording = new DiagnosticLoggerProvider();
        using var services = DiagnosticServices(http, recording);
        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IAcmPreCadastroMinimoGatewayR57>().EnviarMinimoAcmAsync(Request(), default);
        Assert.Equal(805, result.Id);
        Assert.Empty(recording.Events);
        Assert.Equal(1, wire.Posts);
    }

    [Fact]
    public async Task Parity_diagnostic_preserves_correlations_actual_payload_fingerprint_and_safe_Guardian_code()
    {
        const string responseId = "20c5e513-f02f-4771-96cf-befcb21611dd";
        const string body = "{\"errorCode\":309851,\"Message\":\"Dado rejeitado; senha=PRIVATE_PASSWORD\",\"token\":\"PRIVATE_BODY\"}";
        using var wire = new Wire(async (_, _) =>
        {
            var response = await Reply(400, body);
            response.Headers.TryAddWithoutValidation("X-Request-ID", responseId);
            return response;
        });
        using var http = new HttpClient(wire);
        using var recording = new DiagnosticLoggerProvider();
        using var services = DiagnosticServices(http, recording);
        using var scope = services.CreateScope();
        using var activity = new Activity("synthetic-parity").SetIdFormat(ActivityIdFormat.W3C).Start();
        var error = await Assert.ThrowsAsync<GuardianRestR57HttpException>(() => scope.ServiceProvider
            .GetRequiredService<IAcmPreCadastroMinimoGatewayR57>().EnviarMinimoAcmAsync(Request(), default));
        var values = Assert.Single(recording.Events).State.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("309851", values.GetValueOrDefault("GuardianErrorCode"));
        Assert.Equal(responseId, values.GetValueOrDefault("ResponseRequestId"));
        Assert.Equal(activity.TraceId.ToHexString(), values.GetValueOrDefault("CorrelationId"));
        Assert.True(Guid.TryParseExact(values.GetValueOrDefault("RequestId") as string, "N", out _));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(wire.SentBody!))), values.GetValueOrDefault("RequestPayloadFingerprint"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))), values.GetValueOrDefault("ResponseBodyFingerprint"));
        Assert.Equal("JSON_OBJECT", values.GetValueOrDefault("ResponseBodyKind"));
        Assert.Equal("POST", values.GetValueOrDefault("RequestMethod"));
        Assert.Equal("https://guardian.invalid:8443/api/v1/Guardian/ProtocoloPreCadastro", values.GetValueOrDefault("RequestUrl"));
        Assert.Equal("application/json", values.GetValueOrDefault("RequestContentType"));
        Assert.Equal("*/*", values.GetValueOrDefault("RequestAccept"));
        Assert.StartsWith("Dado rejeitado;", error.SafeBusinessMessage);
        var diagnostics = JsonSerializer.Serialize(values) + error.ToString();
        foreach (var secret in new[] { "PRIVATE_PASSWORD", "PRIVATE_BODY", Token }) Assert.DoesNotContain(secret, diagnostics);
        Assert.Equal(1, wire.Posts);
        Assert.Equal(1, wire.TokenRequests);
    }

    [Fact]
    public async Task Parity_unknown_HTML_body_and_untrusted_request_id_are_not_logged_or_retried()
    {
        using var wire = new Wire(async (_, _) =>
        {
            var response = await Reply(400, "<html>PRIVATE_BODY " + Token + "</html>");
            response.Content.Headers.ContentType = new("text/html");
            response.Headers.TryAddWithoutValidation("X-Request-ID", Token);
            return response;
        });
        using var http = new HttpClient(wire);
        using var recording = new DiagnosticLoggerProvider();
        using var services = DiagnosticServices(http, recording);
        using var scope = services.CreateScope();
        await Assert.ThrowsAsync<GuardianRestR57HttpException>(() => scope.ServiceProvider
            .GetRequiredService<IAcmPreCadastroMinimoGatewayR57>().EnviarMinimoAcmAsync(Request(), default));
        var values = Assert.Single(recording.Events).State.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("NON_JSON", values.GetValueOrDefault("ResponseBodyKind"));
        Assert.Equal("text/html", values.GetValueOrDefault("ResponseContentType"));
        Assert.Null(values.GetValueOrDefault("ResponseRequestId"));
        Assert.Null(values.GetValueOrDefault("GuardianErrorCode"));
        Assert.Null(values["SafeBusinessMessage"]);
        Assert.DoesNotContain("PRIVATE_BODY", JsonSerializer.Serialize(values));
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(values));
        Assert.Equal(1, wire.Posts);
    }

    private static ServiceProvider DiagnosticServices(HttpClient http, ILoggerProvider provider, LogLevel minimum = LogLevel.Warning)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Guardian:BaseUrl"] = "https://guardian.invalid:8443/base/",
            ["Guardian:Timeout"] = "00:00:05", ["Guardian:Email"] = "synthetic@example.invalid",
            ["Guardian:Password"] = "SYNTHETIC_PASSWORD", ["Guardian:Signature"] = "SYNTHETIC_SIGNATURE"
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(logging => logging.SetMinimumLevel(minimum).AddProvider(provider));
        services.AddGuardianRuntime(config);
        services.AddSingleton(http);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private sealed record DiagnosticEvent(string Category, LogLevel Level, EventId Event,
        IReadOnlyList<KeyValuePair<string, object?>> State, Exception? Exception, string Message);

    private sealed class DiagnosticLoggerProvider(bool throwOnLog = false) : ILoggerProvider
    {
        public List<DiagnosticEvent> Events { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName, throwOnLog);
        public void Dispose() { }
        private sealed class Capture(DiagnosticLoggerProvider owner, string category, bool fail) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state).ToArray();
                owner.Events.Add(new(category, level, id, values, exception, formatter(state, exception)));
                if (fail) throw new InvalidOperationException("PRIVATE_LOGGER_FAILURE");
            }
        }
    }

    private static void AssertSafeAuthFailure(Exception error, int status, string code)
    {
        AssertFailure(error, status, code, false);
        Assert.Null(Property<string>(error, "SafeBusinessMessage"));
        Assert.DoesNotContain("SECRET", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("person@example.invalid", error.ToString());
        var state = new ResultadoFluxoMvp(StatusFluxoMvp.FalhaNaoReprocessavel, "SYNTHETIC",
            "R165Y_REST_REJECTED", Erro: code).Normalizar();
        Assert.False(state.ErroResultadoIndeterminado);
    }

    private static void AssertInitialPayload(JsonElement root)
    {
        var data = root.GetProperty("dadosProtocoloPreCadastro");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("codigoFluxo").ValueKind);
        Assert.Equal(0, data.GetProperty("idAgenda").GetInt32());
        Assert.Equal(0, data.GetProperty("id").GetInt32());
        Assert.Equal(8, data.EnumerateObject().Count());
        foreach (var key in new[] { "codigoTransportadora", "documentos", "placaVeiculo", "codigoMotorista", "pesoOrigem", "numeroTicket" })
            Assert.False(data.TryGetProperty(key, out _), key);
        Assert.False(root.TryGetProperty("criacaoInicialSemDadosPesagem", out _));
    }

    private static PreCadastroAcmR57 Request() => new("https://callback.example.invalid/receive",
        new(0, "SYNTH-ITEM", "SYNTH-EMITTER", "SYNTH-CONTRACT", null, "OC-SYNTH",
            [new(1000, "AC3P"), new(1002, "3011"), new(1003, "10")]) { IdAgenda = 0 })
        { CriacaoInicialSemDadosPesagem = true, CallbackHomologado = true };

    private static GuardianSet26HttpClient Client(HttpClient http, TimeSpan? timeout = null,
        ICredenciaisGuardianSet26Provider? credentials = null)
    {
        var options = new GuardianHttpOptions
        {
            BaseUrl = new Uri("https://guardian.invalid:8443/base/"),
            PreCadastroEndpoint = "/api/v1/Guardian/ProtocoloPreCadastro",
            Timeout = timeout ?? TimeSpan.FromSeconds(5)
        };
        return new(http, options, new GuardianTokenClient(http, options, credentials ?? new Credentials()));
    }

    private static Task<HttpResponseMessage> Reply(int status, string body) => Task.FromResult(
        new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    private static async Task<Exception> Failure(GuardianSet26HttpClient client) =>
        await Assert.ThrowsAnyAsync<Exception>(() => client.EnviarMinimoAcmAsync(Request(), default));

    private static T? Property<T>(Exception error, string name) =>
        (T?)error.GetType().GetProperty(name)?.GetValue(error);

    private static void AssertFailure(Exception error, int status, string safeCode, bool unknown)
    {
        // This boundary check also compiles against the baseline, so RED is behavioral, not a compiler failure.
        var contract = typeof(AcmR57Mapping).Assembly.GetType("FugaTES.Aplicacao.Servicos.Mvp.IAcmGuardianHttpFailure");
        Assert.NotNull(contract);
        Assert.True(contract.IsInstanceOfType(error));
        Assert.Equal(status, Property<int>(error, "HttpStatusCode"));
        Assert.Equal(safeCode, Property<string>(error, "SafeCode"));
        Assert.Equal(unknown, Property<bool>(error, "ResultadoIndeterminado"));
        Assert.Equal(safeCode, error.Message);
        Assert.Null(error.InnerException);
    }

    private sealed class Credentials : ICredenciaisGuardianSet26Provider
    {
        public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct) => Task.FromResult(
            new CredenciaisGuardianSet26("synthetic@example.invalid", "SYNTHETIC_PASSWORD", "SYNTHETIC_SIGNATURE"));
    }

    private sealed class FaultingCredentials : ICredenciaisGuardianSet26Provider
    {
        public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct) =>
            Task.FromException<CredenciaisGuardianSet26>(new InvalidOperationException("SECRET_PASSWORD person@example.invalid"));
    }

    private sealed class Wire(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply,
        Func<CancellationToken, Task<HttpResponseMessage>>? authReply = null) : HttpMessageHandler
    {
        public int Posts { get; private set; }
        public int TokenRequests { get; private set; }
        public string? SentBody { get; private set; }
        public string? SentUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v2/CreateToken")
            {
                TokenRequests++;
                return authReply is null ? await Reply(200, JsonSerializer.Serialize(new { token = Token })) : await authReply(ct);
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Posts++;
            SentBody = await request.Content!.ReadAsStringAsync(ct);
            SentUri = request.RequestUri.AbsoluteUri;
            return await reply(request, ct);
        }
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromException<int>(new IOException("PRIVATE_BODY"));
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("PRIVATE_BODY");
        public override int Read(Span<byte> buffer) => throw new IOException("PRIVATE_BODY");
    }

    private sealed class SlowStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
    }
}
