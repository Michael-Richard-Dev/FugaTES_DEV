using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.Mvp;
using Xunit;

public sealed class R165AGNormalWindowTests
{
    [Fact]
    public async Task Approved_window_mapping_creates_unknown_contract_SOAP_then_REST()
    {
        var f = new AutomaticFixture(); var (v, c) = f.Add("9765405");
        var w = R165AGWindowSelectionTests.Window(); var options = Options(w);
        c = c with { CorrelationId = w.CriarCorrelationId() }; f.Journal.Claims[v.IntegracaoId] = c;
        var result = await f.Pipeline(options).ExecutarAsync(c, default);
        Assert.Equal(StatusFluxoMvp.AguardandoCallback, result.Status);
        Assert.Equal("9765405", f.Soap.Sent!.Codigo);
        Assert.Equal("SYNTHETIC-PARTNER", f.Soap.Sent.CodigoEmissor);
        Assert.Equal("SYNTHETIC-MATERIAL", f.Rest.Sent!.Dados.CodigoItem);
        Assert.Equal(1, f.Soap.Creates); Assert.Equal(1, f.Rest.Creates);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Window_is_rechecked_immediately_before_each_external_write(int write)
    {
        var f = new AutomaticFixture(); var (v, c) = f.Add("9765406");
        var w = R165AGWindowSelectionTests.Window(); var clock = new Clock(); var options = Options(w);
        c = c with { CorrelationId = w.CriarCorrelationId() }; f.Journal.Claims[v.IntegracaoId] = c;
        var p = new AcmR57Pipeline(f.Versions, new AcmR57NormalPreparation(options),
            new AcmR57NormalRuntimeGate(options, clock), f.Soap, f.Rest, f.Journal, f.H4);
        var callbacks = 0;
        var result = await p.ExecutarAsync(c, default, () => { if (++callbacks == write) clock.Now = w.UntilUtc!.Value; });
        Assert.Equal("R165AG_WINDOW_CLOSED", result.ErroCodigo);
        Assert.Equal(write - 1, f.Soap.Creates); Assert.Equal(0, f.Rest.Creates);
    }

    [Fact]
    public async Task Old_correlation_marker_never_enters_SOAP_or_REST_for_same_unknown_number()
    {
        var f = new AutomaticFixture(); var (_, c) = f.Add("9765407");
        var result = await f.Pipeline(Options(R165AGWindowSelectionTests.Window())).ExecutarAsync(c, default);
        Assert.Equal("R165AG_ADMISSION_MARKER_REQUIRED", result.ErroCodigo);
        Assert.Equal(0, f.Soap.Lookups); Assert.Equal(0, f.Rest.Creates);
    }

    [Fact]
    public async Task Fresh_window_refuses_existing_guardian_contract_without_REST_adoption()
    {
        var f = new AutomaticFixture(); var (v, c) = f.Add("9765401");
        var window = R165AGWindowSelectionTests.Window();
        var options = Options(window);
        c = c with { CorrelationId = window.CriarCorrelationId() }; f.Journal.Claims[v.IntegracaoId] = c;
        f.Soap.Lookup = EstadoConsultaContratoGuardian.Existente;
        f.Soap.Existing = new AcmR57NormalPreparation(options).PrepararContrato(v).Contrato;
        var r = await f.Pipeline(options).ExecutarAsync(c, default);
        Assert.Equal("R165AG_EXISTING_CONTRACT_RECONCILIATION_REQUIRED", r.ErroCodigo);
        Assert.Equal(0, f.Soap.Creates); Assert.Equal(0, f.Rest.Creates);
    }

    [Fact]
    public async Task Expiry_after_SOAP_receipt_keeps_receipt_and_blocks_REST()
    {
        var f = new AutomaticFixture(); var (v, c) = f.Add("9765402");
        var window = R165AGWindowSelectionTests.Window(); var clock = new Clock(); var options = Options(window);
        c = c with { CorrelationId = window.CriarCorrelationId() }; f.Journal.Claims[v.IntegracaoId] = c;
        f.Soap.AfterResponse = () => clock.Now = window.UntilUtc!.Value;
        var p = new AcmR57Pipeline(f.Versions, new AcmR57NormalPreparation(options),
            new AcmR57NormalRuntimeGate(options, clock), f.Soap, f.Rest, f.Journal, f.H4);
        var r = await p.ExecutarAsync(c, default);
        Assert.Equal("R165AG_WINDOW_CLOSED", r.ErroCodigo);
        Assert.Equal(v.NumeroContrato, f.H4.Items[v.IntegracaoId].CodigoContratoConfirmado);
        Assert.Equal("R165U_SOAP_CONFIRMED", f.Journal.Results[v.IntegracaoId].Etapa);
        Assert.Equal(1, f.Soap.Creates); Assert.Equal(0, f.Rest.Creates);
    }

    [Fact]
    public void Historical_mapping_or_wrong_window_mapping_never_authorizes_unknown_contract()
    {
        var f = new AutomaticFixture(); var (v, _) = f.Add("9765403");
        var w = R165AGWindowSelectionTests.Window();
        Assert.Equal("R165AG_WINDOW_MAPPING_REQUIRED", new AcmR57NormalRuntimeGate(
            AutomaticFixture.Options() with { AcmInboundWindow = w }).Bloqueio(v));
        var options = Options(w) with { ApprovedWindowPartnerMappings = [new()
        { WindowId = Guid.NewGuid().ToString("N"), Centro = "3011", Item = "10", SapPartnerId = "0020000051",
          GuardianCode = "SYNTHETIC-PARTNER", EvidenceReference = "SYNTHETIC_MAPPING_ONLY" }] };
        Assert.Equal("R165AG_WINDOW_MAPPING_REQUIRED", new AcmR57NormalRuntimeGate(options).Bloqueio(v));
    }

    internal static AcmR57NormalOptions Options(AcmInboundWindowOptions w) => AutomaticFixture.Options() with
    {
        AcmInboundWindow = w, SapIdentifiersCopiedVerbatimApproved = false, SapContractCopiedVerbatimApproved = true,
        ApprovedWindowPartnerMappings = [new() { WindowId = w.WindowId, Centro = w.Centro, Item = w.Item,
            SapPartnerId = "0020000051", GuardianCode = "SYNTHETIC-PARTNER", EvidenceReference = "SYNTHETIC_MAPPING_ONLY" }],
        ApprovedWindowMaterialMappings = [new() { WindowId = w.WindowId, Centro = w.Centro, Item = w.Item,
            SapMaterial = "7000000", GuardianCode = "SYNTHETIC-MATERIAL", EvidenceReference = "SYNTHETIC_MAPPING_ONLY" }]
    };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
