using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.Guardian;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

public sealed class GuardianSapCompositionTests
{
    // Every identity, operation, XML name and attestation below is SYNTHETIC.
    // These are not provider samples or an operational authorization.
    const string Test = "urn:synthetic-r164e-not-guardian-wire";
    static readonly XNamespace T = Test;
    static readonly XNamespace S = "http://schemas.xmlsoap.org/soap/envelope/";

    static CorrelacaoRecebimentoMvp Correlation(string? contract = "0000001106", string material = "7000000")
    {
        var callback = new CallbackGuardianMvp("9471", "8810", 150, 50, 100, "5");
        return new("OC1106", "9471", new RecebimentoSapCpiMvp("RECEBIMENTO", "SYNTHETIC", material,
            "0020000184", "", "0000001106", "OC1106", "AC3P", "3011", "10", "AC3P", null, 10, contract),
            "8810", callback, EstadoCorrelacaoGuardianSap.TicketAssociado,
            CallbackGuardianMvpFingerprint.Calcular(callback), null, new(150, 50, 100));
    }

    static ClaimIntegracaoMotor Claim(CorrelacaoRecebimentoMvp c) => new(Guid.NewGuid(), Guid.NewGuid(),
        c.DocumentoSap, "GUARDIAN", "SAP", GuardianPesagemSapMotorWork.Operacao, c.ProtocoloGuardian ?? "MISSING",
        GuardianPesagemSapMotorWork.Fingerprint(c.ProtocoloGuardian ?? "MISSING", c.CallbackFingerprint ?? "MISSING"),
        1, "SYNTHETIC", DateTimeOffset.UtcNow.AddMinutes(1));

    static GuardianTicketSoapBindingR164D Binding() => new(new("https://guardian.invalid/synthetic.asmx"),
        XName.Get("TicketCompletoObtem", "http://toledobrasil.com.br/WS_Guardian"), T + "request", T + "ticket",
        T + "response", T + "result", new Dictionary<string, string> { ["t"] = Test }, new string('A', 64),
        "SYNTHETIC_AUTH_ATTESTATION", true, false, TimeSpan.FromSeconds(5));

    static GuardianTicketFieldContractR164D Fields(bool confirmed = true) => new(
        new("t:ticket", "t:protocol", "t:finalOp", "t:finished", "t:direction", "t:person", "t:gross", "t:tara", "t:net",
            "t:qualities/t:item", "t:type", "t:value", "t:group", "t:operation", "t:operationEvidence",
            "t:property", NfeKey: "t:nfe", Contract: "t:contract", Material: "t:material", Reference: "t:reference", Center: "t:center", TicketCode: "t:realCode"),
        new Dictionary<string, string> { ["t"] = Test }, "SYNTHETIC_CAPTURE", "SYNTHETIC_OPERATION_RULE",
        "SYNTHETIC_DIRECTION_RULE", confirmed, confirmed);

    static XElement Ticket(string person = "PJ", string direction = "S", string material = "7000000") => new(T + "result",
        new XElement(T + "realCode", "TKT-00042"), new XElement(T + "ticket", "8810"), new XElement(T + "protocol", "9471"), new XElement(T + "finalOp", "3"),
        new XElement(T + "finished", "2026-10-21T16:30:00-03:00"), new XElement(T + "direction", direction),
        new XElement(T + "person", person), new XElement(T + "gross", "150"), new XElement(T + "tara", "50"),
        new XElement(T + "net", "100"), new XElement(T + "operation", "7"),
        new XElement(T + "operationEvidence", "SYNTHETIC_OPERATION_RULE"), new XElement(T + "property", "0099990001"),
        new XElement(T + "contract", "0000001106"), new XElement(T + "material", material),
        new XElement(T + "reference", "OC1106"), new XElement(T + "center", "3011"),
        new XElement(T + "nfe", direction == "E" ? new string('1', 44) : ""),
        new XElement(T + "qualities", new XElement(T + "item", new XElement(T + "type", "SYNTHETIC_QUALITY"),
            new XElement(T + "value", "1.2"))));

    static IServiceCollection HostServices()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["HostIntegracao:ExternalMode"] = "Guardian", ["HostIntegracao:PersistenceMode"] = "PostgreSql",
            ["ConnectionStrings:FugaTES"] = "Host=127.0.0.1;Port=5433;Database=synthetic;Username=synthetic",
            ["MotorIntegracao:LeaseDuration"] = "00:01:00", ["MotorIntegracao:LeaseRenewInterval"] = "00:00:10",
            ["MotorIntegracao:RetryBackoff"] = "00:00:10", ["TimeoutProcessamentoIntegracao:Timeout"] = "00:00:30",
            ["Guardian:BaseUrl"] = "https://guardian.invalid", ["Guardian:Timeout"] = "00:00:05",
            ["Guardian:Email"] = "synthetic", ["Guardian:Password"] = "synthetic-not-a-credential",
            ["Guardian:Signature"] = "synthetic", ["Sap:Enabled"] = "false",
            ["FUGATES_INTEGRATION_WORKER_ENABLED"] = "false"
        }).Build();
        var services = new ServiceCollection();
        HostIntegracaoComposition.ConfigurarServicos(services, config);
        return services;
    }

    // Explicit synthetic association, never a production resolver or manual-code workflow.
    static GuardianTicketCodeEvidenceR165G CodeIdentity(CorrelacaoRecebimentoMvp? correlation = null)
    {
        var c = correlation ?? Correlation();
        return new("TKT-00042", c.TicketGuardian!, c.ProtocoloGuardian!, c.Entrada.ContratoAcmSap!,
            c.CallbackFingerprint!, "SYNTHETIC_CODE_ASSOCIATION", true);
    }

    [Fact] public async Task Shipped_host_registers_exactly_one_inert_source_without_workers()
    {
        var services = HostServices();
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var source = Assert.Single(scope.ServiceProvider.GetServices<IAcmSapTicketEvidenceSource>());
        Assert.Null(await source.ObterAsync(Correlation(), default));
    }

    [Theory] [InlineData("PJ", "E")] [InlineData("PJ", "S")] [InlineData("PF", "E")] [InlineData("PF", "S")]
    public async Task Soap_adapter_correlation_mapper_and_odata_preserve_business_fields(string person, string direction)
    {
        using var h = new Harness(Ticket(person, direction));
        var result = await h.Run(Correlation());
        Assert.Equal(ClassificacaoFalhaIntegracao.ResultadoIndeterminado, result.Falha);
        Assert.Equal(1, h.Soap.Calls); Assert.Equal(1, h.Sap.Posts); Assert.Equal(1, h.Preflight.Calls);
        using var json = JsonDocument.Parse(Assert.IsType<string>(h.Sap.Body));
        var root = json.RootElement; var item = root.GetProperty("to_Pesagem")[0];
        Assert.Equal("8810", root.GetProperty("IdPesagem").GetString());
        Assert.Equal(7, root.GetProperty("TipoOperacao").GetInt32());
        Assert.Equal("0000001106", item.GetProperty("ContratoACM").GetString());
        Assert.Equal("0020000184", item.GetProperty("IdParceiro").GetString());
        Assert.Equal(person == "PF" ? "0099990001" : "0020000184", item.GetProperty("IdParceiroEntrega").GetString());
        Assert.Equal(direction == "E", item.TryGetProperty("ChaveAcessoNFe", out _));
        Assert.False(root.TryGetProperty("IdTransportadora", out _));
        Assert.False(root.TryGetProperty("IdAgendamento", out _));
    }

    [Fact] public async Task Activated_reader_without_proven_code_blocks_both_soap_and_sap()
    {
        using var h = new Harness(Ticket(), includeCodeIdentity: false);
        var result = await h.Run(Correlation());
        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva, result.Falha);
        Assert.Equal(0, h.Soap.Calls);
        Assert.Equal(0, h.Sap.Calls);
    }

    [Theory] [InlineData(null)] [InlineData("")] [InlineData(" ")]
    public async Task Missing_contract_is_rejected_before_soap_with_sanitized_cause(string? contract)
    {
        using var h = new Harness(Ticket());
        var ex = await Assert.ThrowsAsync<GuardianTicketReadExceptionR164D>(() => h.Reader.ObterAsync(Correlation(contract), default));
        Assert.Equal("GUARDIAN_TICKET_ACM_CONTRACT_REQUIRED", ex.Message);
        Assert.Equal(0, h.Soap.Calls); Assert.Equal(0, h.Sap.Posts);
        var result = await h.Run(Correlation(contract));
        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva, result.Falha);
        Assert.Contains("GUARDIAN_TICKET_ACM_CONTRACT_REQUIRED", h.BlockedReasons);
    }

    [Theory] [InlineData("operation")] [InlineData("operationEvidence")] [InlineData("nfe")] [InlineData("qualities")]
    [InlineData("property")] [InlineData("ticket")] [InlineData("protocol")] [InlineData("contract")]
    public async Task Incomplete_or_mismatched_evidence_never_reaches_sap(string defect)
    {
        var ticket = Ticket("PF", "E");
        ticket.Element(T + defect)?.Remove();
        using var h = new Harness(ticket);
        var result = await h.Run(Correlation());
        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva, result.Falha);
        Assert.Equal(1, h.Soap.Calls); Assert.Equal(0, h.Sap.Calls);
    }

    [Fact] public async Task Unattested_operation_stays_unavailable_without_http()
    {
        using var h = new Harness(Ticket(), confirmed: false);
        Assert.Null(await h.Source.ObterAsync(Correlation(), default));
        var result = await h.Run(Correlation());
        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva, result.Falha);
        Assert.Equal(0, h.Soap.Calls); Assert.Equal(0, h.Sap.Calls);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Rehydrated_durable_correlation_does_not_require_callback_in_memory(bool eventFingerprint)
    {
        var c = Correlation() with { Callback = null };
        if (eventFingerprint) c = c with {
            CallbackFingerprint = ProcessarEventoGuardianCasoDeUso.CalcularFingerprint(new EventoGuardianRecebimento(9471, 8810, 5, 150, 50, 100)) };
        using var h = new Harness(Ticket(), identityCorrelation: c);
        var result = await h.Run(c);
        Assert.Equal(ClassificacaoFalhaIntegracao.ResultadoIndeterminado, result.Falha);
        Assert.Equal(1, h.Soap.Calls); Assert.Equal(1, h.Sap.Posts);
    }

    [Fact] public async Task Corrupt_rehydrated_fingerprint_blocks_before_soap()
    {
        using var h = new Harness(Ticket());
        var result = await h.Run(Correlation() with { Callback = null, CallbackFingerprint = "SYNTHETIC_CORRUPT" });
        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva, result.Falha);
        Assert.Equal(0, h.Soap.Calls); Assert.Equal(0, h.Sap.Calls);
    }

    [Fact] public async Task Soap_body_timeout_is_bounded_and_never_retried()
    {
        using var handler = new SoapHandler(Ticket()) { SlowBody = true };
        var soap = new GuardianTicketCompletoSoapClientR164D(new HttpClient(handler),
            Binding() with { Timeout = TimeSpan.FromMilliseconds(50) }, new SoapAuth());
        var reader = new GuardianTicketCompletoVerifiedReaderR164D(soap, Fields(), ticketCodeEvidence: CodeIdentity());
        var ex = await Assert.ThrowsAsync<GuardianTicketReadExceptionR164D>(() => reader.ObterAsync(Correlation(), default));
        Assert.Equal("GUARDIAN_TICKET_SOAP_TIMEOUT_UNKNOWN", ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory] [InlineData("7000000")] [InlineData("7000001")] [InlineData("7000002")]
    public async Task Real_measured_quality_follows_the_correlated_material(string material)
    {
        using var h = new Harness(Ticket(material: material));
        await h.Run(Correlation(material: material));
        using var json = JsonDocument.Parse(Assert.IsType<string>(h.Sap.Body));
        Assert.Equal(material, json.RootElement.GetProperty("Material").GetString());
        Assert.Equal(1.2m, json.RootElement.GetProperty("to_QualidPes")[0].GetProperty("ValorAferido").GetDecimal());
    }

    [Theory] [InlineData(401)] [InlineData(500)] [InlineData(0)]
    public async Task Soap_auth_error_or_timeout_has_no_retry_or_sap_dispatch(int failure)
    {
        using var h = new Harness(Ticket()); h.Soap.Failure = failure;
        var result = await h.Run(Correlation());
        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva, result.Falha);
        Assert.Equal(1, h.Soap.Calls); Assert.Equal(0, h.Sap.Calls);
    }

    [Theory] [InlineData(201)] [InlineData(400)] [InlineData(500)] [InlineData(0)]
    public async Task Sap_technical_receipt_error_or_timeout_never_schedules_safe_retry(int status)
    {
        using var h = new Harness(Ticket()); h.Sap.Status = status;
        var result = await h.Run(Correlation());
        Assert.Equal(status == 400 ? ClassificacaoFalhaIntegracao.FalhaDefinitiva : ClassificacaoFalhaIntegracao.ResultadoIndeterminado, result.Falha);
        Assert.Equal(1, h.Sap.Posts); Assert.Equal(2, h.Sap.Calls);
    }

    sealed class Harness : IDisposable
    {
        readonly ServiceProvider services;
        readonly IServiceScope scope;
        public readonly SoapHandler Soap;
        public readonly SapHandler Sap = new();
        public readonly Guard Preflight = new();
        public readonly List<string> BlockedReasons = [];
        public IAcmGuardianVerifiedWeighingReaderR164A Reader => scope.ServiceProvider.GetRequiredService<IAcmGuardianVerifiedWeighingReaderR164A>();
        public IAcmSapTicketEvidenceSource Source => scope.ServiceProvider.GetRequiredService<IAcmSapTicketEvidenceSource>();
        public Harness(XElement ticket, bool confirmed = true, bool includeCodeIdentity = true,
            CorrelacaoRecebimentoMvp? identityCorrelation = null)
        {
            Soap = new(ticket);
            var registrations = HostServices();
            // Override only data access and HTTP with synthetic test doubles. No socket handler exists here.
            registrations.AddSingleton(Binding()); registrations.AddSingleton(Fields(confirmed));
            if (includeCodeIdentity) registrations.AddSingleton(CodeIdentity(identityCorrelation));
            registrations.AddSingleton<IConfirmedGuardianTicketSoapAuthenticationR164D, SoapAuth>();
            registrations.AddSingleton(sp => new GuardianTicketCompletoSoapClientR164D(new HttpClient(Soap), Binding(), new SoapAuth()));
            registrations.Replace(ServiceDescriptor.Singleton<IAcmGuardianVerifiedWeighingReaderR164A, GuardianTicketCompletoVerifiedReaderR164D>());
            registrations.AddSingleton<IAcmR106SapReturnPreflight>(Preflight);
            registrations.AddSingleton<IPesagemSapMvpGateway>(new PesagemSapHttpGateway(new HttpClient(Sap),
                new OpcoesSap { BaseUrl = new("https://sap.invalid"), SapClient = "110", Timeout = TimeSpan.FromSeconds(5) },
                new SapAuth(), new CookieContainer()));
            services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            scope = services.CreateScope();
            Assert.Single(scope.ServiceProvider.GetServices<IAcmSapTicketEvidenceSource>());
            Assert.Single(scope.ServiceProvider.GetServices<IAcmGuardianVerifiedWeighingReaderR164A>());
        }
        public Task<ResultadoProcessamentoExterno> Run(CorrelacaoRecebimentoMvp correlation) =>
            new ProcessadorPesagemSapMotor(new Store(correlation), services.GetRequiredService<IPesagemSapMvpGateway>(),
                preflight: Preflight, ticketEvidenceSource: Source, reportBlockedReason: BlockedReasons.Add,
                ticketEvidenceStore: new EvidenceStore()).ProcessarAsync(Claim(correlation), default);
        public void Dispose() { scope.Dispose(); services.Dispose(); }
    }

    // R165A: explicit test-only durability boundary; never registers a production fake.
    sealed class EvidenceStore : IAcmSapTicketEvidenceStore
    {
        public Task PreservarAsync(CorrelacaoRecebimentoMvp c, PesagemSapMvp p, CancellationToken ct)
        { _ = EvidenciaPesagemGuardianSnapshot.Criar(c,p); return Task.CompletedTask; }
    }

    sealed class SoapHandler(XElement ticket) : HttpMessageHandler
    {
        public int Calls; public int? Failure; public bool SlowBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var sent = XDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("TKT-00042", Assert.Single(sent.Descendants(T + "ticket")).Value);
            if (Failure == 0) throw new TaskCanceledException("SYNTHETIC_TIMEOUT");
            var xml = new XDocument(new XElement(S + "Envelope", new XElement(S + "Body", new XElement(T + "response", ticket))));
            return new HttpResponseMessage((HttpStatusCode)(Failure ?? 200)) {
                Content = SlowBody ? new SlowContent() : new StringContent(xml.ToString(), Encoding.UTF8, "text/xml") };
        }
    }
    sealed class SlowContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("SYNTHETIC_MISSING_CANCELLATION");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new NoSynchronousReadStream());
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => CreateContentReadStreamAsync();
    }
    sealed class NoSynchronousReadStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("SYNTHETIC_UNCANCELLABLE_BODY_READ");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    sealed class SapHandler : HttpMessageHandler
    {
        public int Calls, Posts; public int Status = 201; public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (request.Method == HttpMethod.Get)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Add("X-CSRF-Token", "SYNTHETIC"); return response;
            }
            Posts++; Body = await Assert.IsAssignableFrom<HttpContent>(request.Content).ReadAsStringAsync(ct);
            if (Status == 0) throw new TaskCanceledException("SYNTHETIC_TIMEOUT");
            return new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent("{}") };
        }
    }
    sealed class SoapAuth : IConfirmedGuardianTicketSoapAuthenticationR164D
    { public Task ApplyAsync(HttpRequestMessage r, CancellationToken ct) => Task.CompletedTask; }
    sealed class SapAuth : IAutenticacaoSap
    { public Task AplicarAsync(HttpRequestMessage r, CancellationToken ct) => Task.CompletedTask; }
    sealed class Guard : IAcmR106SapReturnPreflight
    { public int Calls; public Task<bool> ValidarAsync(CorrelacaoRecebimentoMvp c, CancellationToken ct) { Calls++; return Task.FromResult(true); } }
    sealed class Store(CorrelacaoRecebimentoMvp correlation) : IRepositorioCorrelacaoRecebimento
    {
        public Task<CorrelacaoRecebimentoMvp?> ObterPorProtocoloAsync(string id, CancellationToken ct) => Task.FromResult<CorrelacaoRecebimentoMvp?>(correlation);
        public Task<ResultadoRegistroSnapshotMvp> RegistrarSnapshotAsync(RecebimentoSapCpiMvp x, CancellationToken ct) => throw new NotSupportedException();
        public Task<CorrelacaoRecebimentoMvp?> ObterPorDocumentoAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ResultadoAssociacaoContratoAcmMvp> AssociarContratoAcmAsync(string d, string a, CancellationToken ct) => throw new NotSupportedException();
        public Task<ResultadoAssociacaoProtocoloMvp> AssociarProtocoloAsync(string d, string p, CancellationToken ct, DateTimeOffset created) => throw new NotSupportedException();
        public Task<ResultadoRegistroCallbackMvp> RegistrarCallbackAsync(CallbackGuardianMvp cb, CancellationToken ct) => throw new NotSupportedException();
    }
}
