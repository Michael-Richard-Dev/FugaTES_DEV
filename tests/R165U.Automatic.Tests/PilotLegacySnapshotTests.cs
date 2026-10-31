using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using System.Text.Json;
using Xunit;

public sealed class PilotLegacySnapshotTests
{
    [Fact]
    public void Original_1155_snapshot_keeps_proven_hash_and_does_not_gain_an_Item_or_unit()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Original1155Snapshot.json"));
        var snapshot = JsonSerializer.Deserialize<ContratoSapToledoSnapshot>(json)!;
        var canonical = new ContratoSapToledoCanonicalizer().Canonicalizar(snapshot);
        Assert.Equal("2AAD46D5FCE100CD8AB808D30F475627E7F4C29A400DFC021A509D3139E9EF8E", canonical.FingerprintSha256);
        Assert.Equal("0000001155", snapshot.NumeroContrato);
        Assert.Equal("000000000007000000", snapshot.Material);
        Assert.Equal(10000m, snapshot.QuantidadeContrato);
        Assert.Null(snapshot.Item);
        Assert.Null(snapshot.UnidCont);
    }

    [Fact]
    public async Task Explicit_partner_mapping_preserves_absent_legacy_Item_without_inventing_one()
    {
        var f = new AutomaticFixture();
        var (version, claim) = f.Add("0000001155", s => s with { Item = null });
        var options = AutomaticFixture.Options() with
        {
            SapIdentifiersCopiedVerbatimApproved = false,
            SapContractAndMaterialCopiedVerbatimApproved = true,
            ApprovedPartnerMapping = new()
            {
                NumeroContrato = "0000001155", Item = null, Centro = "3011", SapPartnerId = "0020000051",
                GuardianCode = "SYNTHETIC_EMITTER", EvidenceReference = "SYNTHETIC_MAPPING_NOT_OPERATIONAL_APPROVAL"
            }
        };
        Assert.Null(new AcmR57NormalRuntimeGate(options).Bloqueio(version));
        Assert.Equal("R165U_CONFIRMED", (await f.Pipeline(options).ExecutarAsync(claim, default)).Etapa);
        Assert.Null(version.Snapshot.Item);
        Assert.Equal("SYNTHETIC_EMITTER", f.Soap.Sent!.CodigoEmissor);
    }
}
