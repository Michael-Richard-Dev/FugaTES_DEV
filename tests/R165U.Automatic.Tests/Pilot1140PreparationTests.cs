using System.Text.Json;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Integracao.Guardian;
using Xunit;

public sealed class Pilot1140PreparationTests
{
    [Fact]
    public async Task Original_1140_is_composed_without_borrowed_item_or_snapshot_and_without_real_transports()
    {
        var root = new DirectoryInfo(Environment.GetEnvironmentVariable("FUGATES_TEST_EVIDENCE_ROOT") ?? AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "evidence/R165Y_REST_POSTMAN_PARITY/CONTRACT1140_SOURCE_READONLY.json"))) root = root.Parent;
        Assert.NotNull(root);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName,
            "evidence/R165Y_REST_POSTMAN_PARITY/CONTRACT1140_SOURCE_READONLY.json")));
        var source = document.RootElement.EnumerateArray().Single(row => row.GetProperty("identity").GetProperty("port").GetInt32() == 5432);
        var original = source.GetProperty("versions").EnumerateArray().Single();
        Assert.Equal("65b160cf-af30-410a-bced-785105e8a33e", original.GetProperty("integracao_id").GetString());
        var snapshot = JsonSerializer.Deserialize<ContratoSapToledoSnapshot>(original.GetProperty("received_contract_snapshot").GetRawText())!;
        var f = new AutomaticFixture();
        var (version, claim) = f.Add("0000001140", _ => snapshot);
        Assert.Equal("F399DEB81333D8E87D2772F905E14B207950395C0DBB99E8C3DA24CE6BF15A1C", version.FingerprintSha256);
        Assert.Null(snapshot.Item);
        Assert.Null(snapshot.UnidCont);
        Assert.Equal("AC3P", snapshot.TipoContrato);
        Assert.Equal("10", snapshot.OperacaoContrato);
        var options = AutomaticFixture.Options() with
        {
            SapIdentifiersCopiedVerbatimApproved = false, SapContractCopiedVerbatimApproved = true,
            ApprovedPartnerMapping = new() { NumeroContrato = snapshot.NumeroContrato, Item = snapshot.Item, Centro = snapshot.Centro,
                SapPartnerId = snapshot.IdParceiro, GuardianCode = "20000051", EvidenceReference = "SYNTHETIC_TEST_MAPPING_PENDING_FRESH_GET" },
            ApprovedMaterialMapping = new() { NumeroContrato = snapshot.NumeroContrato, Item = snapshot.Item, Centro = snapshot.Centro,
                SapMaterial = snapshot.Material, GuardianCode = "7000000", EvidenceReference = "SYNTHETIC_TEST_MAPPING_PENDING_FRESH_GET" },
            IdAgenda = 1, CallbackUrl = "http://192.168.17.40/api/guardian/set26/callback/recebimento",
            CallbackBinding = new("HML", "http://192.168.17.40/api/guardian/set26/callback/recebimento",
                "192.168.17.91/32", true, true, "SYNTHETIC_TEST_ONLY")
        };
        var result = await f.Pipeline(options).ExecutarAsync(claim, default);
        Assert.Equal("R165U_CONFIRMED", result.Etapa);
        Assert.Equal(80000m, f.Soap.Sent!.ValorContrato);
        Assert.Equal("0000001140", f.Soap.Sent.Codigo);
        Assert.Equal("ACM contrato compra de terceiros", f.Soap.Sent.Descricao);
        Assert.Equal("OC1140", f.Rest.Sent!.Dados.NumeroReferencia);
        Assert.Equal(1, f.Rest.Sent.Dados.IdAgenda);
        var payload = JsonSerializer.SerializeToElement(GuardianSet26HttpClient.CriarPayloadR57(f.Rest.Sent),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var data = payload.GetProperty("dadosProtocoloPreCadastro");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("codigoFluxo").ValueKind);
        Assert.False(data.TryGetProperty("codigoTransportadora", out _));
        Assert.Equal("0000001140", data.GetProperty("codigoContrato").GetString());
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "PILOT1140_PREVIEW.json"), payload.GetRawText());
    }
}
