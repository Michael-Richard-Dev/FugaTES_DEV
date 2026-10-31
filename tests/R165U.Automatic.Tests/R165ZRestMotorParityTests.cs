using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.HostIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Integracao.Guardian;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class R165ZRestMotorParityTests
{
    private const string Callback = "http://192.168.17.40/api/guardian/set26/callback/recebimento";
    private const string Endpoint = "https://guardian.cloudprix.com.br/api/v1/Guardian/ProtocoloPreCadastro";
    private const string Historical1140Sha = "70DD1F869C1A39F682AE4D0EBBC52E99B91B12AF014DC9DB13AFE9E802105FEF";
    private const string SyntheticToken = "SYNTHETIC_R165Z_TOKEN_NOT_A_CREDENTIAL";
    // Operator-supplied successful OC1144 request. No response DTO is used as a request baseline.
    private const string Postman1144 = """
        {"configuracao":{"urlParaRetorno":"http://192.168.17.40/api/guardian/set26/callback/recebimento"},
         "dadosProtocoloPreCadastro":{"id":0,"codigoFluxo":null,"codigoItem":"7000000","codigoEmissor":"20000051",
         "codigoContrato":"0000001144","idAgenda":1,"numeroReferencia":"OC1144",
         "camposAdicionais":[{"numero":1000,"valor":"AC3P"},{"numero":1002,"valor":"3011"},{"numero":1003,"valor":"10"}]}}
        """;

    [Theory]
    [InlineData("1140", false)]
    [InlineData("1140", true)]
    [InlineData("1144", false)]
    [InlineData("1144", true)]
    public async Task Real_H4_through_motor_or_Windows_command_matches_Postman_and_actual_send_bytes(string reference, bool manual)
    {
        using var h = new Harness(reference);
        var result = await h.Run(manual);
        Assert.Equal("CONFIRMED", result.State);
        Assert.Equal(99001, result.ProtocolId); // Synthetic response only, never a live Guardian identifier.
        var body = Assert.IsType<byte[]>(h.Wire.Body);
        var expected = Baseline(reference);
        var actual = JsonNode.Parse(body)!;
        Assert.True(JsonNode.DeepEquals(expected, actual), "CURRENT_MOTOR_REQUEST_DIFFERS_FROM_OPERATOR_POSTMAN_BASELINE");
        var data = actual["dadosProtocoloPreCadastro"]!.AsObject();
        Assert.Equal("000000" + reference, data["codigoContrato"]!.GetValue<string>());
        Assert.NotEqual("585", data["codigoContrato"]!.GetValue<string>());
        Assert.True(data.ContainsKey("codigoFluxo"));
        Assert.Null(data["codigoFluxo"]);
        Assert.False(data.ContainsKey("codigoTransportadora"));
        Assert.Equal(0, data["id"]!.GetValue<int>());
        Assert.Equal(1, data["idAgenda"]!.GetValue<int>());
        Assert.Equal("OC" + reference, data["numeroReferencia"]!.GetValue<string>());
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal(1, h.Wire.ProtocolCalls);
        Assert.Equal(1, h.Wire.TokenCalls);
        Assert.Equal(1, h.SoapCreates);
        Assert.Null(h.Version.Snapshot.Item);
        Assert.Null(h.Version.Snapshot.UnidCont);
        Assert.Equal("R165U_CONFIRMED", h.Fixture.Journal.Results[h.Version.IntegracaoId].Etapa);
        Assert.Single(h.Repository.Tentativas);
        Assert.Equal(585, h.Wire.SequenceBeforeRest);
        Assert.Equal("R165U_REST_ARMED", h.Wire.JournalBeforeRest);
        var normal = new AcmR57NormalPreparation(h.NormalOptions);
        Assert.Equal(GuardianSet26HttpClient.SerializarPayloadR57(normal.PrepararProtocolo(h.Version,
            normal.PrepararContrato(h.Version))), body);
        if (reference == "1140") Assert.Equal(Historical1140Sha, Convert.ToHexString(SHA256.HashData(body)));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(body)), result.PayloadSha256);
        Save(reference, manual, body, result);
    }

    [Fact]
    public async Task Windows_preview_and_automatic_motor_are_byte_identical_not_just_equal_DTOs()
    {
        using var automatic = new Harness("1140");
        using var command = new Harness("1140");
        await automatic.Run(false);
        await command.PrepareConfirmedSoapStage();
        var service = command.Service();
        var preview = await service.PreviewAsync(command.Version.IntegracaoId, default);
        Assert.True(preview.CanSend, preview.BlockCode);
        Assert.Equal(0, command.Wire.TokenCalls);
        Assert.Equal(0, command.Wire.ProtocolCalls);
        var sent = await service.SendAsync(command.Version.IntegracaoId, preview.PayloadSha256!, preview.PreviewId!, true, default);
        Assert.Equal("CONFIRMED", sent.State);
        Assert.Equal(automatic.Wire.Body, command.Wire.Body);
        Assert.Equal(command.Wire.Body, Encoding.UTF8.GetBytes(preview.PayloadJson!));
        Assert.Equal(Historical1140Sha, preview.PayloadSha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Motor_retains_known_400_and_command_exposes_sanitized_message_without_automatic_resend(bool manual)
    {
        using var h = new Harness("1140", reject: true);
        var result = await h.Run(manual);
        var diagnostic = Assert.IsType<GuardianR57ExchangeDiagnostic>(h.Guardian.LastR57Diagnostic);
        Assert.Equal(400, diagnostic.HttpStatus);
        Assert.Equal("The codigoContrato field is invalid.", diagnostic.SafeBusinessMessage);
        Assert.Contains("E_SYNTHETIC_FIELD", diagnostic.SafeResponseBody);
        Assert.DoesNotContain(SyntheticToken, diagnostic.SafeResponseBody);
        Assert.DoesNotContain("synthetic@example.invalid", diagnostic.SafeResponseBody);
        Assert.DoesNotContain("12.345.678/0001-90", diagnostic.SafeResponseBody);
        Assert.Equal("R165Y_REST_REJECTED", h.Fixture.Journal.Results[h.Version.IntegracaoId].Etapa);
        Assert.Equal("GUARDIAN_R57_HTTP_400", h.Fixture.Journal.Results[h.Version.IntegracaoId].ErroCodigo);
        Assert.Equal(StatusFluxoMvp.FalhaNaoReprocessavel, h.Fixture.Journal.Results[h.Version.IntegracaoId].Status);
        if (manual)
        {
            Assert.Equal("FAILED", result.State);
            Assert.Equal(400, result.HttpStatus);
            Assert.Equal(diagnostic.SafeResponseBody, result.SafeResponseBody);
            var second = await h.Service().SendAsync(h.Version.IntegracaoId, result.PayloadSha256!, result.PreviewId!, true, default);
            Assert.False(second.CanSend);
        }
        else await h.Motor.ExecutarPorIdAsync(h.Version.IntegracaoId, default);
        var terminal = Assert.IsType<IntegracaoMotor>(await h.Repository.ObterPorIdAsync(h.Version.IntegracaoId, default));
        Assert.Equal(EstadoIntegracao.FalhaDefinitiva, terminal.Estado);
        Assert.Null(terminal.TipoDisparoPendente);
        Assert.Null(terminal.ProximaTentativaEm);
        Assert.Null(await h.Repository.ClaimarEspecificamenteAsync(terminal.Id, terminal.Estado,
            terminal.TipoDisparoPendente, terminal.FingerprintSha256, new("SYNTHETIC_R165Z_LATE_WORKER"),
            TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow.AddHours(1), default));
        Assert.Equal(1, h.Wire.ProtocolCalls);
        Assert.Single(h.Repository.Tentativas);
    }

    [Theory]
    [InlineData("sequence-instead-of-code")]
    [InlineData("contract-as-number")]
    [InlineData("unpad-code")]
    [InlineData("flow-missing")]
    [InlineData("flow-empty-string")]
    [InlineData("carrier-null-instead-of-omitted")]
    [InlineData("id-string")]
    [InlineData("agenda-zero")]
    [InlineData("reference-changed")]
    [InlineData("additional-field-missing")]
    [InlineData("additional-value-number")]
    [InlineData("callback-placeholder")]
    [InlineData("wrong-envelope")]
    public void Differential_oracle_rejects_each_material_wire_mutation(string mutation)
    {
        var baseline = Baseline("1140");
        var changed = baseline.DeepClone().AsObject();
        var data = changed["dadosProtocoloPreCadastro"]!.AsObject();
        switch (mutation)
        {
            case "sequence-instead-of-code": data["codigoContrato"] = "585"; break;
            case "contract-as-number": data["codigoContrato"] = 1140; break;
            case "unpad-code": data["codigoContrato"] = "1140"; break;
            case "flow-missing": data.Remove("codigoFluxo"); break;
            case "flow-empty-string": data["codigoFluxo"] = ""; break;
            case "carrier-null-instead-of-omitted": data["codigoTransportadora"] = null; break;
            case "id-string": data["id"] = "0"; break;
            case "agenda-zero": data["idAgenda"] = 0; break;
            case "reference-changed": data["numeroReferencia"] = "1140"; break;
            case "additional-field-missing": data["camposAdicionais"]!.AsArray().RemoveAt(2); break;
            case "additional-value-number": data["camposAdicionais"]![2]!["valor"] = 10; break;
            case "callback-placeholder": changed["configuracao"]!["urlParaRetorno"] = "{{callback}}"; break;
            case "wrong-envelope": changed["dadosProtocolo"] = data.DeepClone(); changed.Remove("dadosProtocoloPreCadastro"); break;
        }
        Assert.False(JsonNode.DeepEquals(baseline, changed));
    }

    private static JsonNode Baseline(string reference)
    {
        var value = JsonNode.Parse(Postman1144)!;
        // Only these two identity fields are normalized between the real reference contracts.
        value["dadosProtocoloPreCadastro"]!["codigoContrato"] = "000000" + reference;
        value["dadosProtocoloPreCadastro"]!["numeroReferencia"] = "OC" + reference;
        return value;
    }

    private static ContratoSapToledoSnapshot Snapshot(string reference)
    {
        if (reference == "1144") return JsonSerializer.Deserialize<ContratoSapToledoSnapshot>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Original1144Snapshot.json")))!;
        Assert.Equal("1140", reference);
        var root = new DirectoryInfo(Environment.GetEnvironmentVariable("FUGATES_TEST_EVIDENCE_ROOT") ?? AppContext.BaseDirectory);
        const string relative = "evidence/R165Y_REST_POSTMAN_PARITY/CONTRACT1140_SOURCE_READONLY.json";
        while (root is not null && !File.Exists(Path.Combine(root.FullName, relative))) root = root.Parent;
        Assert.NotNull(root);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, relative)));
        var source = document.RootElement.EnumerateArray().Single(r => r.GetProperty("identity").GetProperty("port").GetInt32() == 5432);
        return JsonSerializer.Deserialize<ContratoSapToledoSnapshot>(source.GetProperty("versions").EnumerateArray()
            .Single().GetProperty("received_contract_snapshot").GetRawText())!;
    }

    private static void Save(string reference, bool manual, byte[] body, GuardianProtocolCommandView result)
        => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"R165Z_CAPTURE_{reference}_{(manual ? "COMMAND" : "MOTOR")}.json"),
            JsonSerializer.Serialize(new { mode="OFFLINE_REAL_H4_FAKE_HTTP_NO_NETWORK_NO_DATABASE", reference, manual,
                payload=JsonNode.Parse(body), payloadSha256=Convert.ToHexString(SHA256.HashData(body)),
                endpoint=Endpoint, host="guardian.cloudprix.com.br", method="POST", contentType="application/json", accept="*/*",
                transferMode="FIXED_CONTENT_LENGTH", contentLength=body.Length, bearer="SYNTHETIC_ISSUED_TOKEN_MATCHED_NOT_RETAINED",
                soapSequence=585, result.State, result.ProtocolId, realGuardianPosts=0, databaseWrites=0 }));

    private sealed class Harness : IDisposable
    {
        public readonly AutomaticFixture Fixture = new();
        public readonly RepositorioMotorIntegracaoFake Repository = new();
        public readonly VersaoContratoSapToledo Version;
        public readonly AcmR57NormalOptions NormalOptions;
        public readonly Capture Wire;
        public readonly GuardianSet26HttpClient Guardian;
        public readonly MotorIntegracaoServico Motor;
        private readonly HttpClient http;
        private readonly ServiceProvider services;
        private readonly ReadableJournal journal;
        private readonly SequenceSoap soap;
        private readonly GuardianHttpOptions httpOptions = new() { BaseUrl=new("https://guardian.cloudprix.com.br"), Timeout=TimeSpan.FromSeconds(5), PreCadastroEndpoint="/api/v1/Guardian/ProtocoloPreCadastro" };
        public int SoapCreates => Fixture.Soap.Creates;

        public Harness(string reference, bool reject=false)
        {
            var snapshot = Snapshot(reference);
            var pair = Fixture.Add(snapshot.NumeroContrato, _ => snapshot);
            Version = pair.Version;
            Assert.Equal(reference == "1140" ? "F399DEB81333D8E87D2772F905E14B207950395C0DBB99E8C3DA24CE6BF15A1C" :
                "9BF8A01AEE4FE049C267F1191C0C6E2B863BDB076E472D4E843A9683FC40EC5C", Version.FingerprintSha256);
            NormalOptions = AutomaticFixture.Options() with {
                SapIdentifiersCopiedVerbatimApproved=false, SapContractCopiedVerbatimApproved=true,
                ApprovedPartnerMapping=new() { NumeroContrato=snapshot.NumeroContrato, Item=snapshot.Item, Centro=snapshot.Centro, SapPartnerId=snapshot.IdParceiro, GuardianCode="20000051", EvidenceReference="SYNTHETIC_REPLAY_OF_DOCUMENTED_MAPPING_ONLY" },
                ApprovedMaterialMapping=new() { NumeroContrato=snapshot.NumeroContrato, Item=snapshot.Item, Centro=snapshot.Centro, SapMaterial=snapshot.Material, GuardianCode="7000000", EvidenceReference="SYNTHETIC_REPLAY_OF_DOCUMENTED_MAPPING_ONLY" },
                IdAgenda=1, CallbackUrl=Callback, CallbackBinding=new("HML", Callback, "192.168.17.91/32", true, true, "SYNTHETIC_TEST_ONLY") };
            Repository.Semear(new(Version.IntegracaoId,"SAP","TOLEDO","SAP_CONTRATO_TOLEDO",Version.NumeroContrato,
                Version.FingerprintSha256,pair.Claim.CorrelationId,EstadoIntegracao.Pendente,TipoDisparoPendente.Automatico,
                DateTimeOffset.UtcNow.AddMinutes(-1),0,null,null,FaseEnvioIntegracao.NaoIniciado,null));
            journal = new(Fixture.Journal);
            soap = new(Fixture.Soap);
            Wire = new(this, reject);
            http = new(Wire);
            Guardian = new(http,httpOptions,new GuardianTokenClient(http,httpOptions,new Credentials()));
            var pipeline = Pipeline(NormalOptions);
            var processor = new Processor(Fixture,pipeline);
            services = new ServiceCollection().AddSingleton<IMotorIntegracaoRepository>(Repository)
                .AddSingleton<IProcessadorExternoIntegracao>(processor).AddSingleton<IDetectorCandidatosIntegracao,NoQueue>()
                .AddSingleton<IContratoSapToledoVersaoStore>(Fixture.Versions).AddSingleton<IValidadorIntegracao,ValidadorGenericoIntegracao>()
                .AddSingleton<IIdentidadeWorkerProvider,Identity>().AddSingleton<IPoliticaTimeoutProcessamentoIntegracao,TimeoutPolicy>()
                .AddSingleton(TimeProvider.System).AddSingleton(new MotorIntegracaoOptions { LeaseDuration=TimeSpan.FromMinutes(1),
                    LeaseRenewInterval=TimeSpan.FromSeconds(10), RetryBackoff=TimeSpan.FromMinutes(1),
                    AcmPilotNumeroContrato=snapshot.NumeroContrato, AcmPilotCentro=snapshot.Centro, AcmPilotItem=snapshot.Item })
                .BuildServiceProvider();
            Motor = ActivatorUtilities.CreateInstance<MotorIntegracaoServico>(services);
        }

        private AcmR57Pipeline Pipeline(AcmR57NormalOptions options) => new(Fixture.Versions,
            new AcmR57NormalPreparation(options),new AcmR57NormalRuntimeGate(options),soap,Guardian,journal,Fixture.H4);
        public async Task PrepareConfirmedSoapStage()
        {
            var result = await Pipeline(NormalOptions with { CallbackApproved=false }).ExecutarAsync(Fixture.Journal.Claims[Version.IntegracaoId],default);
            Assert.Equal("R165U_PROTOCOL_BLOCKED",result.Etapa);
        }
        public GuardianProtocolCommandService Service() => new(Fixture.Versions,Fixture.H4,Repository,journal,
            new AcmR57NormalPreparation(NormalOptions),new AcmR57NormalRuntimeGate(NormalOptions),Motor,Guardian,httpOptions,
            new() { Enabled=true, Environment="HML", IntegrationId=Version.IntegracaoId, Contract=Version.NumeroContrato,
                OperatorSid="S-1-5-21-123-456-789-1001",ExecutionAuthorized=true,AuthorizationReference="SYNTHETIC_TEST_ONLY",UntilUtc=DateTimeOffset.UtcNow.AddHours(1) },TimeProvider.System);
        public async Task<GuardianProtocolCommandView> Run(bool manual)
        {
            if (manual)
            {
                await PrepareConfirmedSoapStage();
                var service = Service();
                var preview = await service.PreviewAsync(Version.IntegracaoId,default);
                Assert.True(preview.CanSend,preview.BlockCode);
                return await service.SendAsync(Version.IntegracaoId,preview.PayloadSha256!,preview.PreviewId!,true,default);
            }
            await Motor.ExecutarPorIdAsync(Version.IntegracaoId,default);
            var diagnostic = Assert.IsType<GuardianR57ExchangeDiagnostic>(Guardian.LastR57Diagnostic);
            return new() { State=diagnostic.ProtocolId is null?"FAILED":"CONFIRMED",ProtocolId=diagnostic.ProtocolId,
                HttpStatus=diagnostic.HttpStatus,PayloadSha256=diagnostic.PayloadSha256 };
        }
        public void Dispose() { http.Dispose(); services.Dispose(); }
    }

    private sealed class Capture(Harness h, bool reject) : HttpMessageHandler
    {
        public byte[]? Body;
        public int TokenCalls,ProtocolCalls;
        public long? SequenceBeforeRest;
        public string? JournalBeforeRest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal("https",request.RequestUri!.Scheme);
            Assert.Equal("guardian.cloudprix.com.br",request.RequestUri.IdnHost);
            if (request.RequestUri.AbsolutePath == "/api/v2/CreateToken")
            {
                Assert.Equal(1,++TokenCalls);
                return Reply(200,JsonSerializer.Serialize(new { token=SyntheticToken }));
            }
            Assert.Equal(Endpoint,request.RequestUri.AbsoluteUri);
            Assert.Equal(HttpMethod.Post,request.Method);
            Assert.Equal(1,++ProtocolCalls);
            Assert.Equal("Bearer",request.Headers.Authorization?.Scheme);
            Assert.Equal(SyntheticToken,request.Headers.Authorization?.Parameter);
            Body=await request.Content!.ReadAsByteArrayAsync(ct);
            Assert.Equal(Body.Length,request.Content.Headers.ContentLength);
            Assert.Equal("application/json",request.Content.Headers.ContentType!.ToString());
            Assert.Equal("*/*",request.Headers.Accept.Single().ToString());
            Assert.False(request.Headers.TransferEncodingChunked ?? false);
            Assert.False(Body.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            JournalBeforeRest=h.Fixture.Journal.Results[h.Version.IntegracaoId].Etapa;
            Assert.Equal(h.Version.NumeroContrato,h.Fixture.H4.Items[h.Version.IntegracaoId].CodigoContratoConfirmado);
            SequenceBeforeRest=long.Parse(h.Fixture.Journal.Results[h.Version.IntegracaoId].ResultadoFuncional!.Split(':')[3]);
            if (reject) return Reply(400,JsonSerializer.Serialize(new { errors=new Dictionary<string,string[]> {
                ["codigoContrato"]=["The codigoContrato field is invalid."] },code="E_SYNTHETIC_FIELD",
                token=SyntheticToken,email="synthetic@example.invalid",documento="12.345.678/0001-90" }));
            return Reply(200,JsonSerializer.Serialize(new { id=99001,codigoContrato=h.Version.NumeroContrato,
                codigoItem="7000000",codigoEmissor="20000051",numeroReferencia="OC"+h.Version.NumeroContrato.TrimStart('0') }));
        }
        private static HttpResponseMessage Reply(int status,string body) => new((HttpStatusCode)status)
            { Content=new StringContent(body,Encoding.UTF8,"application/json") };
    }
    private sealed class SequenceSoap(AutomaticFixture.TestSoap inner) : IGuardianContratoSincronizacaoGateway
    {
        public Task<ConsultaContratoGuardian> ConsultarAsync(string code,CancellationToken ct) => inner.ConsultarAsync(code,ct);
        public async Task<ResultadoContratoGuardianComResposta> CadastrarComRespostaAsync(ContratoGuardianSet26 contract,CancellationToken ct)
        { var result=await inner.CadastrarComRespostaAsync(contract,ct);return result with { SequencialContrato=585 }; }
        public Task CadastrarAsync(ContratoGuardianSet26 c,CancellationToken ct) => throw new InvalidOperationException("NO_LEGACY_CREATE_IN_TEST");
        public Task<ResultadoContratoGuardianComResposta> AlterarComRespostaAsync(ContratoGuardianSet26 a,ContratoGuardianSet26 b,CancellationToken ct)
            => throw new InvalidOperationException("NO_UPDATE_IN_TEST");
    }
    private sealed class ReadableJournal(AutomaticFixture.Ledger inner) : IResultadoFluxoMvpStore
    {
        public Task ExigirDisponibilidadeAsync(CancellationToken ct) => inner.ExigirDisponibilidadeAsync(ct);
        public Task<ResultadoFluxoMvp?> ObterAsync(ClaimIntegracaoMotor c,CancellationToken ct) => inner.ObterAsync(c,ct);
        public Task<ResultadoFluxoMvp?> ObterPorIntegracaoAsync(Guid id,string correlation,string operation,CancellationToken ct)
            => Task.FromResult(inner.Claims.GetValueOrDefault(id)?.CorrelationId==correlation && operation=="SAP_CONTRATO_TOLEDO"?inner.Results.GetValueOrDefault(id):null);
        public Task<bool> PersistirAsync(ClaimIntegracaoMotor c,ResultadoFluxoMvp r,CancellationToken ct) => inner.PersistirAsync(c,r,ct);
        public Task<bool> ReservarEtapaAsync(ClaimIntegracaoMotor c,string? stage,ResultadoFluxoMvp r,CancellationToken ct) => inner.ReservarEtapaAsync(c,stage,r,ct);
    }
    private sealed class Processor(AutomaticFixture fixture,AcmR57Pipeline pipeline) : IProcessadorExternoIntegracao
    {
        public async Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim,CancellationToken ct)
        {
            fixture.Journal.Claims[claim.IntegracaoId]=claim;
            var result=await pipeline.ExecutarAsync(claim,ct);
            return result.Status switch {
                StatusFluxoMvp.FalhaNaoReprocessavel => new(ClassificacaoFalhaIntegracao.FalhaDefinitiva),
                StatusFluxoMvp.ResultadoIndeterminado => new(ClassificacaoFalhaIntegracao.ResultadoIndeterminado),
                _ => ResultadoProcessamentoExterno.Sucesso };
        }
    }
    private sealed class Credentials : ICredenciaisGuardianSet26Provider
    {
        public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct) => Task.FromResult(new CredenciaisGuardianSet26("synthetic@example.invalid","SYNTHETIC_PASSWORD","SYNTHETIC_SIGNATURE"));
    }
    private sealed class NoQueue : IDetectorCandidatosIntegracao
    {
        public Task<IReadOnlyList<CandidatoIntegracao>> DetectarAsync(CancellationToken ct) => throw new InvalidOperationException("NO_GLOBAL_QUEUE_IN_OFFLINE_TEST");
    }
    private sealed class Identity : IIdentidadeWorkerProvider { public IdentidadeWorkerIntegracao Obter() => new("SYNTHETIC_R165Z_WORKER"); }
    private sealed class TimeoutPolicy : IPoliticaTimeoutProcessamentoIntegracao { public TimeSpan ObterTimeout(ClaimIntegracaoMotor claim) => TimeSpan.FromSeconds(5); }
}
