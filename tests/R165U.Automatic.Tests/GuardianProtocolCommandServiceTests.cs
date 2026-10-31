using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.HostIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Integracao.Guardian;
using Xunit;

public sealed class GuardianProtocolCommandServiceTests
{
    [Fact]
    public void Public_command_service_contract_is_available_in_existing_host()
    {
        Assert.NotNull(typeof(HostIntegracaoComposition).Assembly.GetType(
            "FugaTES.HostIntegracao.GuardianProtocolCommandService"));
    }

    [Fact]
    public void Journal_exposes_read_only_lookup_without_a_fabricated_claim()
    {
        Assert.NotNull(typeof(IResultadoFluxoMvpStore).GetMethod("ObterPorIntegracaoAsync",
            [typeof(Guid), typeof(string), typeof(string), typeof(CancellationToken)]));
    }

    [Fact]
    public async Task Preview_id_is_deterministic_and_binds_canonical_identity_endpoint_operator_and_window()
    {
        using var h = await Harness.Create();
        var preview = await h.Service().PreviewAsync(h.Id, default);
        var id = preview.PreviewId;
        Assert.NotNull(id);
        Assert.Matches("^[A-F0-9]{64}$", id);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            preview.IntegrationId, preview.Contract, preview.Version, preview.SnapshotFingerprint,
            preview.PayloadSha256, preview.Endpoint, preview.Method,
            h.Options.OperatorSid, h.Options.AuthorizationReference,
            UntilUtc = h.Options.UntilUtc!.Value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        });
        Assert.Equal(Convert.ToHexString(SHA256.HashData(canonical)), id);
        Assert.Equal(id, (await h.Service().PreviewAsync(h.Id, default)).PreviewId);
        var sameInstant = h.Service(h.Options with { UntilUtc = h.Options.UntilUtc.Value.ToOffset(TimeSpan.FromHours(3)) });
        Assert.Equal(id, (await sameInstant.PreviewAsync(h.Id, default)).PreviewId);
        h.AssertNoDispatch();
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("window")]
    [InlineData("operator")]
    [InlineData("authorization-reference")]
    public async Task Stale_preview_id_blocks_reconfigured_context_even_when_payload_hash_is_unchanged(string changed)
    {
        using var h = await Harness.Create();
        var preview = await h.Service().PreviewAsync(h.Id, default);
        var nextOptions = changed switch
        {
            "window" => h.Options with { UntilUtc = Harness.Now.AddMinutes(2) },
            "operator" => h.Options with { OperatorSid = "S-1-5-21-123-456-789-1002" },
            "authorization-reference" => h.Options with { AuthorizationReference = "SYNTHETIC_NEW_APPROVAL" },
            _ => h.Options
        };
        var nextHttp = changed == "endpoint" ? h.HttpOptions with { BaseUrl = new Uri("https://other-guardian.example.invalid/") } : h.HttpOptions;
        var restarted = h.Service(nextOptions, httpOptions: nextHttp);
        var refreshed = await restarted.PreviewAsync(h.Id, default);
        Assert.True(refreshed.CanSend, refreshed.BlockCode);
        Assert.Equal(preview.PayloadSha256, refreshed.PayloadSha256);
        Assert.NotEqual(preview.PreviewId, refreshed.PreviewId);

        var result = await restarted.SendAsync(h.Id, preview.PayloadSha256!, preview.PreviewId!, true, default);

        Assert.False(result.CanSend);
        Assert.Equal("GUARDIAN_COMMAND_PREVIEW_CHANGED", result.BlockCode);
        h.AssertNoDispatch();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-digest")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public async Task Malformed_preview_id_is_not_a_legacy_confirmation_fallback(string? id)
    {
        using var h = await Harness.Create();
        var service = h.Service();
        var preview = await service.PreviewAsync(h.Id, default);
        var result = await service.SendAsync(h.Id, preview.PayloadSha256!, id!, true, default);
        Assert.False(result.CanSend);
        Assert.Equal("GUARDIAN_COMMAND_PREVIEW_ID_INVALID", result.BlockCode);
        h.AssertNoDispatch();
    }

    [Fact]
    public void Only_context_bound_send_is_exposed_without_a_legacy_overload()
    {
        foreach (var type in new[] { typeof(IGuardianProtocolCommandService), typeof(GuardianProtocolCommandService) })
        {
            var method = Assert.Single(type.GetMethods(), m => m.Name == "SendAsync");
            Assert.Equal(new[] { typeof(Guid), typeof(string), typeof(string), typeof(bool), typeof(CancellationToken) },
                method.GetParameters().Select(p => p.ParameterType));
        }
    }

    [Fact]
    public async Task Preview_is_read_only_and_uses_the_exact_automatic_UTF8_payload()
    {
        using var h = await Harness.Create();
        var history = h.Fixture.Journal.History.ToArray();
        var preview = await h.Service().PreviewAsync(h.Id, default);
        var normal = new AcmR57NormalPreparation(AutomaticFixture.Options());
        var request = normal.PrepararProtocolo(h.Version, normal.PrepararContrato(h.Version));
        var bytes = GuardianSet26HttpClient.SerializarPayloadR57(request);

        Assert.True(preview.CanSend, preview.BlockCode);
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(preview.PayloadJson!));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), preview.PayloadSha256);
        Assert.Equal(h.Version.FingerprintSha256, preview.SnapshotFingerprint);
        Assert.Equal("https://guardian.example.invalid/api/v1/Guardian/ProtocoloPreCadastro", preview.Endpoint);
        Assert.Equal("POST", preview.Method);
        Assert.Equal("application/json", preview.ContentType);
        Assert.Equal("*/*", preview.Accept);
        Assert.Equal("FIXED_CONTENT_LENGTH", preview.TransferMode);
        Assert.Equal("GUARDIAN_HML_BACKEND", preview.AuthenticationContext);
        Assert.DoesNotContain("SYNTHETIC_TOKEN", JsonSerializer.Serialize(preview));
        Assert.Equal(history, h.Fixture.Journal.History);
        Assert.Empty(h.Repository.Tentativas);
        Assert.Equal(0, h.Credentials.Calls);
        Assert.Equal(0, h.Wire.Requests);
        Assert.Equal(1, h.Fixture.Soap.Creates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_unconfirmed_link_still_shows_real_H4_payload_without_dispatch(bool unconfirmed)
    {
        using var h = await Harness.Create();
        if (unconfirmed) h.Fixture.H4.Items[h.Id] = h.Fixture.H4.Items[h.Id] with { CodigoContratoConfirmado = null };
        else h.Fixture.H4.Items.Remove(h.Id);
        var history = h.Fixture.Journal.History.ToArray();
        var normal = new AcmR57NormalPreparation(AutomaticFixture.Options());
        var bytes = GuardianSet26HttpClient.SerializarPayloadR57(normal.PrepararProtocolo(h.Version, normal.PrepararContrato(h.Version)));
        var service = h.Service();

        var preview = await service.PreviewAsync(h.Id, default);

        Assert.False(preview.CanSend);
        Assert.Equal("GUARDIAN_COMMAND_CONFIRMED_CONTRACT_REQUIRED", preview.BlockCode);
        Assert.Equal(Encoding.UTF8.GetString(bytes), preview.PayloadJson);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), preview.PayloadSha256);
        Assert.Equal(h.Version.FingerprintSha256, preview.SnapshotFingerprint);
        Assert.Equal("FIXED_CONTENT_LENGTH", preview.TransferMode);
        Assert.Equal("GUARDIAN_HML_BACKEND", preview.AuthenticationContext);
        Assert.Matches("^[A-F0-9]{64}$", preview.PreviewId!);
        Assert.False((await service.SendAsync(h.Id, preview.PayloadSha256!, preview.PreviewId!, true, default)).CanSend);
        Assert.Equal(history, h.Fixture.Journal.History);
        h.AssertNoDispatch();
    }

    [Fact]
    public async Task Authorization_deadline_cancels_motor_preflight_before_external_dispatch_without_resubmitting()
    {
        using var h = await Harness.Create();
        var clock = new DeadlineClock(Harness.Now);
        var until = Harness.Now.AddMilliseconds(25);
        var service = h.Service(h.Options with { UntilUtc = until }, clock: clock);
        var preview = await service.PreviewAsync(h.Id, default);
        Assert.True(preview.CanSend, preview.BlockCode);
        var cancelledAtDeadline = false;
        h.Processor.Before = ct =>
        {
            clock.Advance(until - clock.GetUtcNow());
            cancelledAtDeadline = ct.IsCancellationRequested;
        };

        var result = await service.SendAsync(h.Id, preview.PayloadSha256!, preview.PreviewId!, true, default);

        Assert.True(cancelledAtDeadline);
        Assert.False(result.CanSend);
        Assert.Equal("UNKNOWN", result.State);
        Assert.Equal(0, h.Credentials.Calls);
        Assert.Equal(0, h.Wire.Requests);
        Assert.Equal("R165U_PROTOCOL_BLOCKED", h.Fixture.Journal.Results[h.Id].Etapa);
        Assert.False((await service.SendAsync(h.Id, preview.PayloadSha256!, preview.PreviewId!, true, default)).CanSend);
        Assert.Single(h.Processor.Claims);
        Assert.Single(h.Repository.Tentativas);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("unauthorized")]
    [InlineData("missing-reference")]
    [InlineData("missing-until")]
    [InlineData("expired")]
    [InlineData("exact-expiry")]
    [InlineData("environment")]
    [InlineData("wrong-integration")]
    [InlineData("wrong-contract")]
    [InlineData("missing-operator")]
    public async Task Command_authorization_is_fail_closed(string scenario)
    {
        using var h = await Harness.Create();
        var options = scenario switch
        {
            "disabled" => h.Options with { Enabled = false },
            "unauthorized" => h.Options with { ExecutionAuthorized = false },
            "missing-reference" => h.Options with { AuthorizationReference = " " },
            "missing-until" => h.Options with { UntilUtc = null },
            "expired" => h.Options with { UntilUtc = Harness.Now.AddSeconds(-1) },
            "exact-expiry" => h.Options with { UntilUtc = Harness.Now },
            "environment" => h.Options with { Environment = "PRD" },
            "wrong-integration" => h.Options with { IntegrationId = Guid.NewGuid() },
            "wrong-contract" => h.Options with { Contract = "0000001155" },
            _ => h.Options with { OperatorSid = "" }
        };
        var service = h.Service(options);
        var preview = await service.PreviewAsync(h.Id, default);
        var result = await service.SendAsync(h.Id, preview.PayloadSha256 ?? new string('A', 64), preview.PreviewId ?? new string('A', 64), true, default);

        Assert.False(preview.CanSend);
        Assert.False(result.CanSend);
        Assert.NotNull(result.BlockCode);
        h.AssertNoDispatch();
    }

    [Theory]
    [InlineData(EstadoIntegracao.EmProcessamento)]
    [InlineData(EstadoIntegracao.Concluida)]
    [InlineData(EstadoIntegracao.FalhaDefinitiva)]
    [InlineData(EstadoIntegracao.ResultadoIndeterminado)]
    [InlineData(EstadoIntegracao.Cancelada)]
    [InlineData(EstadoIntegracao.AguardandoReprocessamento)]
    public async Task Unsafe_motor_states_never_dispatch(EstadoIntegracao state)
    {
        using var h = await Harness.Create(i => i with { Estado = state });
        var result = await h.Service().SendAsync(h.Id, new string('A', 64), new string('A', 64), true, default);
        Assert.False(result.CanSend);
        h.AssertNoDispatch();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("failed")]
    [InlineData("unknown")]
    [InlineData("armed")]
    [InlineData("callback")]
    [InlineData("wrong-correlation")]
    [InlineData("changed-mapping")]
    [InlineData("invalid-sequence")]
    [InlineData("protocol-digest")]
    [InlineData("wrong-status")]
    public async Task Only_known_safe_confirmed_SOAP_journal_can_be_resumed(string scenario)
    {
        using var h = await Harness.Create();
        var previous = h.Fixture.Journal.Results[h.Id];
        if (scenario == "missing") h.Fixture.Journal.Results.Remove(h.Id);
        else h.Fixture.Journal.Results[h.Id] = scenario switch
        {
            "failed" => previous with { Status = StatusFluxoMvp.FalhaNaoReprocessavel },
            "unknown" => previous with { Status = StatusFluxoMvp.ResultadoIndeterminado, ErroResultadoIndeterminado = true },
            "armed" => previous with { Etapa = "R165U_REST_ARMED" },
            "callback" => previous with { Status = StatusFluxoMvp.AguardandoCallback, ProtocoloGuardian = "900" },
            "wrong-correlation" => previous with { Correlacao = "OTHER" },
            "changed-mapping" => previous with { ResultadoFuncional = "U:" + new string('A', 64) + "::900" },
            "invalid-sequence" => previous with { ResultadoFuncional = previous.ResultadoFuncional![..^3] + "0" },
            "protocol-digest" => previous with { ResultadoFuncional = previous.ResultadoFuncional!.Replace("::", ":ABC:") },
            _ => previous with { Status = StatusFluxoMvp.Recebido }
        };
        var result = await h.Service().SendAsync(h.Id, new string('A', 64), new string('A', 64), true, default);
        Assert.False(result.CanSend);
        h.AssertNoDispatch();
        Assert.Equal(1, h.Fixture.Soap.Creates);
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("ticket")]
    [InlineData("callback")]
    [InlineData("unconfirmed")]
    [InlineData("wrong-code")]
    [InlineData("wrong-integration")]
    [InlineData("wrong-version")]
    public async Task Existing_external_state_or_unproven_H4_identity_blocks_send(string scenario)
    {
        using var h = await Harness.Create();
        var link = h.Fixture.H4.Items[h.Id];
        h.Fixture.H4.Items[h.Id] = scenario switch
        {
            "protocol" => link with { ProtocoloIdGuardian = 900 },
            "ticket" => link with { NumeroTicketGuardian = "SYNTHETIC-TICKET" },
            "callback" => link with { CallbackFingerprint = "SYNTHETIC" },
            "unconfirmed" => link with { CodigoContratoConfirmado = null },
            "wrong-code" => link with { CodigoContratoConfirmado = "OTHER" },
            "wrong-integration" => link with { IntegracaoIdOrigem = Guid.NewGuid() },
            _ => link with { VersaoH4 = link.VersaoH4 + 1 }
        };
        Assert.False((await h.Service().SendAsync(h.Id, new string('A', 64), new string('A', 64), true, default)).CanSend);
        h.AssertNoDispatch();
    }

    [Fact]
    public async Task Tampered_H4_snapshot_is_rejected_before_preparing_a_payload()
    {
        using var h = await Harness.Create();
        h.Fixture.Versions.Items[h.Id] = h.Version with { Snapshot = h.Version.Snapshot with { QuantidadeContrato = 1m } };
        var preview = await h.Service().PreviewAsync(h.Id, default);
        Assert.False(preview.CanSend);
        Assert.Null(preview.PayloadJson);
        h.AssertNoDispatch();
    }

    [Fact]
    public async Task Unsupported_journal_read_and_runtime_gate_fail_closed()
    {
        using var h = await Harness.Create();
        IResultadoFluxoMvpStore unsupportedRead = h.Fixture.Journal;
        await Assert.ThrowsAsync<NotSupportedException>(async () => await unsupportedRead.ObterPorIntegracaoAsync(
            h.Id, h.Fixture.Journal.Claims[h.Id].CorrelationId, "SAP_CONTRATO_TOLEDO", default));
        var unsupported = h.Service(journal: h.Fixture.Journal);
        Assert.False((await unsupported.PreviewAsync(h.Id, default)).CanSend);
        var blocked = h.Service(gate: new AcmR57NormalRuntimeGate(AutomaticFixture.Options() with { Enabled = false }));
        Assert.False((await blocked.PreviewAsync(h.Id, default)).CanSend);
        h.AssertNoDispatch();
    }

    [Fact]
    public async Task Confirmed_SOAP_stage_is_accepted_but_a_pending_predecessor_blocks_it()
    {
        using var h = await Harness.Create();
        h.Fixture.Journal.Results[h.Id] = h.Fixture.Journal.Results[h.Id] with
        {
            Etapa = "R165U_SOAP_CONFIRMED", Status = StatusFluxoMvp.EmProcessamento,
            Erro = null, ErroCodigo = null, ErroEtapa = null
        };
        Assert.True((await h.Service().PreviewAsync(h.Id, default)).CanSend);
        h.Fixture.Versions.Predecessor = true;
        Assert.False((await h.Service().PreviewAsync(h.Id, default)).CanSend);
        h.AssertNoDispatch();
    }

    [Fact]
    public async Task Send_never_adopts_a_previous_exchange_diagnostic_when_motor_does_not_claim()
    {
        using var h = await Harness.Create();
        var normal = new AcmR57NormalPreparation(AutomaticFixture.Options());
        await h.Guardian.EnviarMinimoAcmAsync(normal.PrepararProtocolo(h.Version, normal.PrepararContrato(h.Version)), default);
        Assert.NotNull(h.Guardian.LastR57Diagnostic);
        h.Repository.FalharAntesCommitClaim = true;
        var service = h.Service();
        var preview = await service.PreviewAsync(h.Id, default);
        var result = await service.SendAsync(h.Id, preview.PayloadSha256!, preview.PreviewId!, true, default);
        Assert.Null(result.HttpStatus);
        Assert.Null(result.ProtocolId);
        Assert.Null(result.SafeBusinessMessage);
        Assert.Null(result.SafeResponseBody);
        Assert.Empty(h.Processor.Claims);
        Assert.Empty(h.Repository.Tentativas);
        Assert.Equal(1, h.Wire.ProtocolRequests);
    }

    [Fact]
    public async Task Conflicting_durable_receipt_never_claims_a_confirmed_command_outcome()
    {
        using var h = await Harness.Create();
        h.Processor.After = () => h.Fixture.Journal.Results[h.Id] = h.Fixture.Journal.Results[h.Id] with
            { ResultadoFuncional = "U:" + new string('A', 64) + ":" + new string('B', 64) + ":900" };
        var service = h.Service();
        var preview = await service.PreviewAsync(h.Id, default);
        var result = await service.SendAsync(h.Id, preview.PayloadSha256!, preview.PreviewId!, true, default);
        Assert.False(result.CanSend);
        Assert.Equal("UNKNOWN", result.State);
        Assert.Single(h.Processor.Claims);
    }

    [Fact]
    public async Task Confirmation_and_fresh_exact_hash_are_required_before_motor_claim()
    {
        using var h = await Harness.Create();
        var service = h.Service();
        var preview = await service.PreviewAsync(h.Id, default);
        Assert.False((await service.SendAsync(h.Id, preview.PayloadSha256!, preview.PreviewId!, false, default)).CanSend);
        Assert.False((await service.SendAsync(h.Id, new string('0', 64), preview.PreviewId!, true, default)).CanSend);
        var changed = h.Service(preparation: new AcmR57NormalPreparation(AutomaticFixture.Options() with { IdAgenda = 1 }));
        Assert.NotEqual(preview.PayloadSha256, (await changed.PreviewAsync(h.Id, default)).PayloadSha256);
        Assert.False((await changed.SendAsync(h.Id, preview.PayloadSha256!, preview.PreviewId!, true, default)).CanSend);
        h.AssertNoDispatch();
    }

    [Fact]
    public async Task Send_uses_the_existing_motor_exact_id_once_and_returns_scoped_safe_diagnostic()
    {
        using var h = await Harness.Create();
        var older = h.Fixture.Add("1147").Version;
        PilotAutomaticSelectionTests.Seed(h.Repository, older);
        var service = h.Service();
        var preview = await service.PreviewAsync(h.Id, default);
        var sent = await service.SendAsync(h.Id, preview.PayloadSha256!, preview.PreviewId!, true, default);

        Assert.False(sent.CanSend);
        Assert.Equal(901, sent.ProtocolId);
        Assert.Equal(200, sent.HttpStatus);
        Assert.Equal(preview.PayloadJson, Encoding.UTF8.GetString(h.Wire.Payload!));
        Assert.Equal(preview.PayloadSha256, h.Guardian.LastR57Diagnostic!.PayloadSha256);
        Assert.Equal(h.Id, Assert.Single(h.Processor.Claims).IntegracaoId);
        Assert.Equal(h.Id, Assert.Single(h.Repository.Tentativas).IntegracaoId);
        Assert.Equal(EstadoIntegracao.Pendente, (await h.Repository.ObterPorIdAsync(older.IntegracaoId, default))!.Estado);
        Assert.Equal(1, h.Fixture.Soap.Creates);
        Assert.False((await service.SendAsync(h.Id, preview.PayloadSha256!, preview.PreviewId!, true, default)).CanSend);
        Assert.Single(h.Processor.Claims);
        Assert.Equal(1, h.Wire.ProtocolRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Known_rejection_or_cancelled_unknown_never_automatically_resubmits(bool cancelled)
    {
        using var h = await Harness.Create();
        h.Wire.Cancel = cancelled;
        h.Wire.Reject = !cancelled;
        var service = h.Service();
        var preview = await service.PreviewAsync(h.Id, default);
        var result = await service.SendAsync(h.Id, preview.PayloadSha256!, preview.PreviewId!, true, default);
        Assert.False(result.CanSend);
        Assert.Null(result.ProtocolId);
        if (!cancelled)
        {
            Assert.Equal(400, result.HttpStatus);
            Assert.NotNull(result.SafeBusinessMessage);
        }
        var replay = await service.SendAsync(h.Id, preview.PayloadSha256!, preview.PreviewId!, true, default);
        Assert.False(replay.CanSend);
        Assert.Single(h.Processor.Claims);
        Assert.Single(h.Repository.Tentativas);
        Assert.Equal(1, h.Wire.ProtocolRequests);
        Assert.Equal(cancelled ? StatusFluxoMvp.ResultadoIndeterminado : StatusFluxoMvp.FalhaNaoReprocessavel,
            h.Fixture.Journal.Results[h.Id].Status);
    }

    [Fact]
    public async Task Already_cancelled_request_never_reaches_motor_or_authentication()
    {
        using var h = await Harness.Create();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Service().SendAsync(h.Id, new string('A', 64), new string('A', 64), true, cancelled.Token));
        h.AssertNoDispatch();
    }

    private sealed class Harness : IDisposable
    {
        public static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        public readonly AutomaticFixture Fixture = new();
        public readonly RepositorioMotorIntegracaoFake Repository = new();
        public readonly SyntheticWire Wire = new();
        public readonly SyntheticCredentials Credentials = new();
        public readonly GuardianHttpOptions HttpOptions = new()
        {
            BaseUrl = new Uri("https://guardian.example.invalid/"), Timeout = TimeSpan.FromSeconds(5),
            PreCadastroEndpoint = "/api/v1/Guardian/ProtocoloPreCadastro"
        };
        private readonly HttpClient http;
        public readonly GuardianSet26HttpClient Guardian;
        public VersaoContratoSapToledo Version = null!;
        public Guid Id => Version.IntegracaoId;
        public GuardianProtocolCommandOptions Options = null!;
        public PipelineProcessor Processor = null!;
        private FugaTES.Aplicacao.Servicos.MotorIntegracao.MotorIntegracaoServico motor = null!;
        private ReadableJournal journal = null!;

        private Harness()
        {
            http = new HttpClient(Wire);
            Guardian = new(http, HttpOptions, new GuardianTokenClient(http, HttpOptions, Credentials));
        }

        public static async Task<Harness> Create(Func<IntegracaoMotor, IntegracaoMotor>? change = null)
        {
            var h = new Harness();
            var (version, claim) = h.Fixture.Add("1155");
            h.Version = version;
            var blocked = await h.Fixture.Pipeline(AutomaticFixture.Options(false)).ExecutarAsync(claim, default);
            Assert.Equal("R165U_PROTOCOL_BLOCKED", blocked.Etapa);
            var integration = new IntegracaoMotor(version.IntegracaoId, "SAP", "TOLEDO", "SAP_CONTRATO_TOLEDO",
                version.NumeroContrato, version.FingerprintSha256, claim.CorrelationId, EstadoIntegracao.Pendente,
                TipoDisparoPendente.Automatico, Now.AddMinutes(-1), 0, null, null, FaseEnvioIntegracao.NaoIniciado, null);
            h.Repository.Semear(change?.Invoke(integration) ?? integration);
            h.Options = new()
            {
                Enabled = true, Environment = "HML", IntegrationId = h.Id, Contract = version.NumeroContrato,
                OperatorSid = "S-1-5-21-123-456-789-1001", ExecutionAuthorized = true,
                AuthorizationReference = "SYNTHETIC_TEST_ONLY", UntilUtc = Now.AddHours(1)
            };
            h.journal = new(h.Fixture.Journal);
            var preparation = new AcmR57NormalPreparation(AutomaticFixture.Options());
            var gate = new AcmR57NormalRuntimeGate(AutomaticFixture.Options());
            h.Processor = new(h.Fixture, new(h.Fixture.Versions, preparation, gate, h.Fixture.Soap, h.Guardian, h.journal, h.Fixture.H4));
            h.motor = PilotAutomaticSelectionTests.Motor(h.Repository, h.Processor, new NoDetector(), h.Fixture.Versions);
            return h;
        }

        public GuardianProtocolCommandService Service(GuardianProtocolCommandOptions? options = null,
            IResultadoFluxoMvpStore? journal = null, IAcmR57RuntimeGate? gate = null, IAcmR57Preparacao? preparation = null,
            TimeProvider? clock = null, GuardianHttpOptions? httpOptions = null)
            => new(Fixture.Versions, Fixture.H4, Repository, journal ?? this.journal,
                preparation ?? new AcmR57NormalPreparation(AutomaticFixture.Options()),
                gate ?? new AcmR57NormalRuntimeGate(AutomaticFixture.Options()), motor, Guardian, httpOptions ?? HttpOptions,
                options ?? Options, clock ?? new FixedTime());

        public void AssertNoDispatch()
        {
            Assert.Empty(Repository.Tentativas);
            Assert.Empty(Processor.Claims);
            Assert.Equal(0, Credentials.Calls);
            Assert.Equal(0, Wire.Requests);
        }
        public void Dispose() => http.Dispose();
        private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    }

    private sealed class DeadlineClock(DateTimeOffset now) : TimeProvider
    {
        private readonly List<DeadlineTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DeadlineTimer(this, callback, state);
            timer.Change(dueTime, period);
            timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            now += duration;
            foreach (var timer in timers.ToArray()) timer.FireIfDue();
        }
        private sealed class DeadlineTimer(DeadlineClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? due;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed) return false;
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
                return true;
            }
            public void FireIfDue()
            {
                if (disposed || due is null || due > clock.GetUtcNow()) return;
                due = null;
                callback(state);
            }
            public void Dispose() { disposed = true; due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class ReadableJournal(AutomaticFixture.Ledger inner) : IResultadoFluxoMvpStore
    {
        public Task ExigirDisponibilidadeAsync(CancellationToken ct) => inner.ExigirDisponibilidadeAsync(ct);
        public Task<ResultadoFluxoMvp?> ObterAsync(ClaimIntegracaoMotor c, CancellationToken ct) => inner.ObterAsync(c, ct);
        public Task<ResultadoFluxoMvp?> ObterPorIntegracaoAsync(Guid id, string correlation, string operation, CancellationToken ct)
        {
            var claim = inner.Claims.GetValueOrDefault(id);
            return Task.FromResult(claim?.CorrelationId == correlation && claim.Operacao == operation
                ? inner.Results.GetValueOrDefault(id) : null);
        }
        public Task<bool> PersistirAsync(ClaimIntegracaoMotor c, ResultadoFluxoMvp r, CancellationToken ct) => inner.PersistirAsync(c, r, ct);
        public Task<bool> ReservarEtapaAsync(ClaimIntegracaoMotor c, string? stage, ResultadoFluxoMvp r, CancellationToken ct)
            => inner.ReservarEtapaAsync(c, stage, r, ct);
    }

    private sealed class PipelineProcessor(AutomaticFixture f, AcmR57Pipeline pipeline) : IProcessadorExternoIntegracao
    {
        public readonly List<ClaimIntegracaoMotor> Claims = [];
        public Action<CancellationToken>? Before;
        public Action? After;
        public async Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim, CancellationToken ct)
        {
            Claims.Add(claim);
            f.Journal.Claims[claim.IntegracaoId] = claim;
            Before?.Invoke(ct);
            var result = await pipeline.ExecutarAsync(claim, ct);
            After?.Invoke();
            return result.Status switch
            {
                StatusFluxoMvp.ResultadoIndeterminado => new(ClassificacaoFalhaIntegracao.ResultadoIndeterminado),
                StatusFluxoMvp.FalhaNaoReprocessavel => new(ClassificacaoFalhaIntegracao.FalhaDefinitiva),
                _ => ResultadoProcessamentoExterno.Sucesso
            };
        }
    }

    private sealed class NoDetector : IDetectorCandidatosIntegracao
    {
        public Task<IReadOnlyList<CandidatoIntegracao>> DetectarAsync(CancellationToken ct)
            => throw new InvalidOperationException("EXACT_ID_MUST_NOT_SCAN_QUEUE");
    }

    private sealed class SyntheticCredentials : ICredenciaisGuardianSet26Provider
    {
        public int Calls;
        public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new CredenciaisGuardianSet26("SYNTHETIC", "SYNTHETIC", "SYNTHETIC"));
        }
    }

    private sealed class SyntheticWire : HttpMessageHandler
    {
        public int Requests, ProtocolRequests;
        public bool Cancel, Reject;
        public byte[]? Payload;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (request.RequestUri!.AbsolutePath == "/api/v2/CreateToken")
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"SYNTHETIC_TOKEN\"}") };
            Assert.Equal("/api/v1/Guardian/ProtocoloPreCadastro", request.RequestUri.AbsolutePath);
            ProtocolRequests++;
            Payload = await request.Content!.ReadAsByteArrayAsync(ct);
            if (Cancel) throw new OperationCanceledException("SYNTHETIC_UNKNOWN");
            if (Reject) return new(HttpStatusCode.BadRequest)
                { Content = new StringContent("{\"message\":\"Contrato rejeitado\"}") };
            return new(HttpStatusCode.OK) { Content = new StringContent(
                "{\"id\":901,\"codigoContrato\":\"1155\",\"codigoItem\":\"7000000\",\"codigoEmissor\":\"0020000051\",\"numeroReferencia\":\"OC1155\"}") };
        }
    }
}
