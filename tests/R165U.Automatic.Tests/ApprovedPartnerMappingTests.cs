using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Integracao.Guardian;
using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class ApprovedPartnerMappingTests
{
    private static AcmR57NormalOptions ApprovedOptions(Action<JsonObject>? change = null)
    {
        var json = JsonSerializer.SerializeToNode(AutomaticFixture.Options())!.AsObject();
        json["SapIdentifiersCopiedVerbatimApproved"] = false;
        json["SapContractAndMaterialCopiedVerbatimApproved"] = true;
        // Operator-confirmed identities; this reference does not authorize runtime execution.
        json["ApprovedPartnerMapping"] = JsonSerializer.SerializeToNode(new {
            NumeroContrato = "1147", Item = "10", Centro = "3011",
            SapPartnerId = "0020000051", GuardianCode = "20000051",
            EvidenceReference = "OPERATOR_CONFIRMED_20261025_TEST_REFERENCE"
        });
        change?.Invoke(json);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString()));
        return new ConfigurationBuilder().AddJsonStream(stream).Build().Get<AcmR57NormalOptions>()!;
    }

    [Fact]
    public async Task Approved_1147_partner_reaches_contract_and_protocol_without_mutating_sap_snapshot()
    {
        var f = new AutomaticFixture();
        var (version, claim) = f.Add();
        var originalJson = JsonSerializer.Serialize(version.Snapshot);
        var originalFingerprint = version.FingerprintSha256;
        var options = ApprovedOptions();

        Assert.Null(new AcmR57NormalRuntimeGate(options).Bloqueio(version));
        var prepared = new AcmR57NormalPreparation(options).PrepararContrato(version);
        Assert.Equal("20000051", prepared.Contrato.CodigoEmissor);
        Assert.Equal("1147", prepared.Contrato.Codigo);
        Assert.Equal("7000000", prepared.Contrato.CodigoItem);
        Assert.Equal("308102", prepared.Contrato.Estado);
        Assert.Equal("309837", prepared.Contrato.TipoLancamento);

        var result = await f.Pipeline(options).ExecutarAsync(claim, default);

        Assert.Equal(StatusFluxoMvp.AguardandoCallback, result.Status);
        Assert.Equal("R165U_CONFIRMED", result.Etapa);
        Assert.Equal("20000051", f.Soap.Sent!.CodigoEmissor);
        Assert.Equal("20000051", f.Rest.Sent!.Dados.CodigoEmissor);
        var payload = JsonSerializer.SerializeToNode(GuardianSet26HttpClient.CriarPayloadR57(f.Rest.Sent),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("20000051", payload["dadosProtocoloPreCadastro"]!["codigoEmissor"]!.GetValue<string>());
        Assert.Equal("0020000051", version.Snapshot.IdParceiro);
        Assert.Equal(originalJson, JsonSerializer.Serialize(f.Versions.Items[version.IntegracaoId].Snapshot));
        Assert.Equal(originalFingerprint, f.Versions.Items[version.IntegracaoId].FingerprintSha256);
        Assert.Equal(1, f.Soap.Creates);
        Assert.Equal(1, f.Rest.Creates);

        var replay = claim with { FencingToken = 2, TentativaId = Guid.NewGuid() };
        f.Journal.Claims[claim.IntegracaoId] = replay;
        Assert.Equal(result, await f.Pipeline(options).ExecutarAsync(replay, default));
        Assert.Equal(1, f.Soap.Creates);
        Assert.Equal(1, f.Rest.Creates);
    }

    [Theory]
    [InlineData("1148", "BP-SYNTHETIC-0007", "EM-SYNTHETIC-0003")]
    [InlineData("1147", "0020000051", "020000051")]
    public async Task Configured_mapping_is_literal_not_numeric_conversion_or_contract_hardcode(
        string number, string partner, string emitter)
    {
        var f = new AutomaticFixture();
        var (version, claim) = f.Add(number, s => s with { IdParceiro = partner });
        var options = ApprovedOptions(json => {
            json["ApprovedPartnerMapping"]!["NumeroContrato"] = number;
            json["ApprovedPartnerMapping"]!["SapPartnerId"] = partner;
            json["ApprovedPartnerMapping"]!["GuardianCode"] = emitter;
        });

        Assert.Equal(StatusFluxoMvp.AguardandoCallback, (await f.Pipeline(options).ExecutarAsync(claim, default)).Status);
        Assert.Equal(emitter, f.Soap.Sent!.CodigoEmissor);
        Assert.Equal(emitter, f.Rest.Sent!.Dados.CodigoEmissor);
        Assert.Equal(partner, version.Snapshot.IdParceiro);
    }

    [Theory]
    [InlineData("NumeroContrato", "1148")]
    [InlineData("NumeroContrato", "0000001147")]
    [InlineData("NumeroContrato", null)]
    [InlineData("Item", "20")]
    [InlineData("Item", "00010")]
    [InlineData("Item", null)]
    [InlineData("Centro", "3012")]
    [InlineData("Centro", "03011")]
    [InlineData("Centro", null)]
    [InlineData("SapPartnerId", "0020000052")]
    [InlineData("SapPartnerId", "20000051")]
    [InlineData("SapPartnerId", null)]
    [InlineData("GuardianCode", null)]
    [InlineData("GuardianCode", "")]
    [InlineData("GuardianCode", " 20000051")]
    [InlineData("GuardianCode", "PENDENTE")]
    [InlineData("GuardianCode", "123456789012345678901")]
    [InlineData("EvidenceReference", null)]
    [InlineData("EvidenceReference", "")]
    [InlineData("EvidenceReference", "PENDENTE")]
    [InlineData("EvidenceReference", "NO\nEVIDENCE")]
    [InlineData("EvidenceReference", " evidence ")]
    public async Task Missing_unproven_or_other_context_mapping_blocks_gate_and_preparation_before_dispatch(
        string field, string? value)
    {
        var f = new AutomaticFixture();
        var (version, claim) = f.Add();
        var options = ApprovedOptions(json => json["ApprovedPartnerMapping"]![field] = value);

        Assert.Equal("R165U_PARTNER_MAPPING_REQUIRED", new AcmR57NormalRuntimeGate(options).Bloqueio(version));
        var error = Assert.Throws<AcmR57BlockedException>(() => new AcmR57NormalPreparation(options).PrepararContrato(version));
        Assert.Equal("R165U_PARTNER_MAPPING_REQUIRED", error.Code);
        var result = await f.Pipeline(options).ExecutarAsync(claim, default);
        Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna, result.Status);
        Assert.Equal("R165U_PARTNER_MAPPING_REQUIRED", result.ErroCodigo);
        Assert.Equal(0, f.Soap.Lookups);
        Assert.Equal(0, f.Soap.Creates);
        Assert.Equal(0, f.Rest.Creates);
    }

    [Theory]
    [InlineData("missing_mapping")]
    [InlineData("missing_contract_material_approval")]
    [InlineData("ambiguous_verbatim_approval")]
    [InlineData("mapping_with_legacy_approval_only")]
    [InlineData("new_approval_without_mapping_and_legacy_true")]
    public async Task Missing_or_ambiguous_approval_never_falls_back_to_copying_sap_partner(string defect)
    {
        var f = new AutomaticFixture();
        var (version, claim) = f.Add();
        var options = ApprovedOptions(json => {
            if (defect is "missing_mapping" or "new_approval_without_mapping_and_legacy_true")
                json["ApprovedPartnerMapping"] = null;
            if (defect is "missing_contract_material_approval" or "mapping_with_legacy_approval_only")
                json["SapContractAndMaterialCopiedVerbatimApproved"] = false;
            if (defect is "ambiguous_verbatim_approval" or "mapping_with_legacy_approval_only" or "new_approval_without_mapping_and_legacy_true")
                json["SapIdentifiersCopiedVerbatimApproved"] = true;
        });

        Assert.Equal("R165U_PARTNER_MAPPING_REQUIRED", new AcmR57NormalRuntimeGate(options).Bloqueio(version));
        Assert.Throws<AcmR57BlockedException>(() => new AcmR57NormalPreparation(options).PrepararContrato(version));
        Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna, (await f.Pipeline(options).ExecutarAsync(claim, default)).Status);
        Assert.Equal(0, f.Soap.Lookups);
        Assert.Equal(0, f.Soap.Creates);
        Assert.Equal(0, f.Rest.Creates);
    }

    [Fact]
    public async Task Contract_material_mapping_still_requires_its_own_identifier_evidence()
    {
        var f = new AutomaticFixture();
        var (_, claim) = f.Add();
        var options = ApprovedOptions(json => json["IdentifierMappingEvidence"] = null);
        var result = await f.Pipeline(options).ExecutarAsync(claim, default);
        Assert.Equal("R165U_IDENTIFIER_MAPPING_REQUIRED", result.ErroCodigo);
        Assert.Equal(0, f.Soap.Lookups);
        Assert.Equal(0, f.Rest.Creates);
    }

    [Fact]
    public async Task Changed_partner_mapping_cannot_replace_confirmed_digest_or_repeat_creates()
    {
        var f = new AutomaticFixture();
        var (_, claim) = f.Add();
        Assert.Equal(StatusFluxoMvp.AguardandoCallback, (await f.Pipeline(ApprovedOptions()).ExecutarAsync(claim, default)).Status);
        var changed = ApprovedOptions(json => json["ApprovedPartnerMapping"]!["GuardianCode"] = "SYNTHETIC-OTHER");
        var replay = claim with { FencingToken = 2, TentativaId = Guid.NewGuid() };
        f.Journal.Claims[claim.IntegracaoId] = replay;
        var result = await f.Pipeline(changed).ExecutarAsync(replay, default);
        Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado, result.Status);
        Assert.Equal("R165U_MAPPING_OR_STAGE_CONFLICT", result.ErroCodigo);
        Assert.Equal(1, f.Soap.Creates);
        Assert.Equal(1, f.Rest.Creates);
    }

    [Fact]
    public async Task Explicit_partner_mapping_does_not_bypass_historical_1106_guard()
    {
        var f = new AutomaticFixture();
        var (_, claim) = f.Add("0000001106", s => s with { IdParceiro = "0020000184" });
        var options = ApprovedOptions(json => {
            json["ApprovedPartnerMapping"]!["NumeroContrato"] = "0000001106";
            json["ApprovedPartnerMapping"]!["SapPartnerId"] = "0020000184";
            json["ApprovedPartnerMapping"]!["GuardianCode"] = "20000184";
        });
        var result = await f.Pipeline(options).ExecutarAsync(claim, default);
        Assert.Equal("R165U_HISTORICAL_MAPPING_RECONCILIATION_REQUIRED", result.ErroCodigo);
        Assert.Equal(0, f.Soap.Lookups);
        Assert.Equal(0, f.Rest.Creates);
    }

    [Fact]
    public void New_mapping_approval_is_false_by_default_and_legacy_verbatim_remains_unchanged()
    {
        var defaults = new AcmR57NormalOptions();
        var json = JsonSerializer.SerializeToNode(defaults)!;
        Assert.Equal(false, json["SapContractAndMaterialCopiedVerbatimApproved"]?.GetValue<bool>());
        Assert.False(defaults.Enabled);
        Assert.False(defaults.ExecutionAuthorized);
        var f = new AutomaticFixture();
        var (version, _) = f.Add();
        Assert.Equal("0020000051", new AcmR57NormalPreparation(AutomaticFixture.Options()).PrepararContrato(version).Contrato.CodigoEmissor);
    }
}
