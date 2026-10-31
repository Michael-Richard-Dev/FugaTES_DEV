using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.Servicos.Mvp;
using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class PilotMaterialMappingTests
{
    internal static AcmR57NormalOptions Options(Action<JsonObject>? change = null)
    {
        var json = JsonSerializer.SerializeToNode(AutomaticFixture.Options())!.AsObject();
        json["SapIdentifiersCopiedVerbatimApproved"] = false;
        json["SapContractAndMaterialCopiedVerbatimApproved"] = false;
        json["SapContractCopiedVerbatimApproved"] = true;
        json["ApprovedPartnerMapping"] = JsonSerializer.SerializeToNode(new
        {
            NumeroContrato = "0000001155", Item = (string?)null, Centro = "3011", SapPartnerId = "0020000051",
            GuardianCode = "20000051", EvidenceReference = "SYNTHETIC_OPERATOR_MAPPING_REFERENCE"
        });
        json["ApprovedMaterialMapping"] = JsonSerializer.SerializeToNode(new
        {
            NumeroContrato = "0000001155", Item = (string?)null, Centro = "3011", SapMaterial = "000000000007000000",
            GuardianCode = "7000000", EvidenceReference = "SYNTHETIC_EXPLICIT_MATERIAL_MAPPING_NOT_OPERATIONAL_AUTH"
        });
        change?.Invoke(json);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString()));
        return new ConfigurationBuilder().AddJsonStream(stream).Build().Get<AcmR57NormalOptions>()!;
    }

    [Fact]
    public async Task Explicit_1155_mapping_uses_Guardian_code_without_modifying_original_material_or_contract()
    {
        var f = new AutomaticFixture();
        var (version, claim) = f.Add("0000001155", s => s with { Item = null, Material = "000000000007000000" });
        var originalHash = version.FingerprintSha256;
        var result = await f.Pipeline(Options()).ExecutarAsync(claim, default);
        Assert.Equal("R165U_CONFIRMED", result.Etapa);
        Assert.Equal("0000001155", f.Soap.Sent!.Codigo);
        Assert.Equal("20000051", f.Soap.Sent.CodigoEmissor);
        Assert.Equal("7000000", f.Soap.Sent.CodigoItem);
        Assert.Equal("7000000", f.Rest.Sent!.Dados.CodigoItem);
        Assert.Equal("OC1155", f.Rest.Sent.Dados.NumeroReferencia);
        Assert.Equal("000000000007000000", f.Versions.Items[version.IntegracaoId].Snapshot.Material);
        Assert.Equal(originalHash, f.Versions.Items[version.IntegracaoId].FingerprintSha256);
        Assert.Null(version.Snapshot.Item);
    }

    [Theory]
    [InlineData("NumeroContrato", "1155")]
    [InlineData("Item", "10")]
    [InlineData("Centro", "3012")]
    [InlineData("SapMaterial", "7000000")]
    [InlineData("GuardianCode", null)]
    [InlineData("GuardianCode", "")]
    [InlineData("EvidenceReference", null)]
    [InlineData("EvidenceReference", "PENDENTE")]
    public async Task Missing_or_different_context_material_mapping_blocks_before_Guardian(string field, string? value)
    {
        var f = new AutomaticFixture();
        var (_, claim) = f.Add("0000001155", s => s with { Item = null, Material = "000000000007000000" });
        var result = await f.Pipeline(Options(j => j["ApprovedMaterialMapping"]![field] = value)).ExecutarAsync(claim, default);
        Assert.NotEqual("R165U_CONFIRMED", result.Etapa);
        Assert.Equal(0, f.Soap.Lookups);
        Assert.Equal(0, f.Soap.Creates);
        Assert.Equal(0, f.Rest.Creates);
    }

    [Theory]
    [InlineData("contract_approval_missing")]
    [InlineData("material_mapping_missing")]
    [InlineData("ambiguous_legacy_verbatim")]
    [InlineData("ambiguous_combined_verbatim")]
    public async Task Ambiguous_or_unapproved_material_mode_never_falls_back_to_verbatim(string defect)
    {
        var f = new AutomaticFixture();
        var (_, claim) = f.Add("0000001155", s => s with { Item = null, Material = "000000000007000000" });
        var options = Options(j =>
        {
            if (defect == "contract_approval_missing") j["SapContractCopiedVerbatimApproved"] = false;
            if (defect == "material_mapping_missing") j["ApprovedMaterialMapping"] = null;
            if (defect == "ambiguous_legacy_verbatim") j["SapIdentifiersCopiedVerbatimApproved"] = true;
            if (defect == "ambiguous_combined_verbatim") j["SapContractAndMaterialCopiedVerbatimApproved"] = true;
        });
        await f.Pipeline(options).ExecutarAsync(claim, default);
        Assert.Equal(0, f.Soap.Lookups);
        Assert.Equal(0, f.Soap.Creates);
        Assert.Equal(0, f.Rest.Creates);
    }
}
