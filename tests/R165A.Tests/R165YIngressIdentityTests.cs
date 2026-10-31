using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Callback.Http;
using Xunit;

public sealed class R165YIngressIdentityTests
{
    private const string HistoricalJson = """
        {"SistemaBalanca":"Toledo","NumeroContrato":"1147","TipoContrato":null,"DescricaoTipoContrato":null,"DataEmissao":null,"DataEntregaInicio":null,"DataEntregaFim":null,"DiasToler":null,"DataLimiteEntrega":null,"StatusContrato":null,"QuantidadeContrato":null,"QuantidadeSaldoDevolucao":null,"IdParceiro":null,"Material":null,"TipoPesagem":null,"TabelaQualidadeID":null,"TabelaQualidadeVersao":null,"TabelaQualidadeVersaoValidade":null,"Observacao":null,"ValorUnitario":null,"Centro":"3011","OperacaoContrato":null,"CentroOpcional":[]}
        """;
    private const string HistoricalFingerprint =
        "91F052F926C94AC01CEAE9989418BBD1100BA48F67FB876549A3A551A7E71D1F";

    [Theory]
    [InlineData("1147", "\"Item\":\"10\",\"UnidCont\":\"KG\",", "10", "KG")]
    [InlineData("0000001147", "\"Item\":\"00010\",\"UnidCont\":\"KG\",", "00010", "KG")]
    [InlineData("1147", "", null, null)]
    [InlineData("1147", "\"Item\":null,\"UnidCont\":null,", null, null)]
    [InlineData("1147", "\"Item\":\"10\",", "10", null)]
    [InlineData("1147", "\"UnidCont\":\"KG\",", null, "KG")]
    public void Sanitized_local_identity_reaches_snapshot_without_changing_legacy_history(
        string contract, string optionalFields, string? expectedItem, string? expectedUnit)
    {
        // Controlled local reference only, not a raw SAP payload or integration attestation.
        var json = $$"""
            {"Contrato":[{ {{optionalFields}} "SistemaBalanca":"Toledo","NumeroContrato":"{{contract}}","Centro":"3011"}]}
            """;
        var dto = JsonSerializer.Deserialize<SapContractInboundHttpDto>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var item = Assert.Single(dto.Contrato!)!;
        var snapshot = SapContractInboundMapper.MapearSnapshot(item);

        Assert.Equal(contract, snapshot.NumeroContrato);
        Assert.Equal("3011", snapshot.Centro);
        Assert.Equal(expectedItem, snapshot.Item);
        Assert.Equal(expectedUnit, snapshot.UnidCont);

        var expected = JsonNode.Parse(HistoricalJson)!.AsObject();
        expected["NumeroContrato"] = contract;
        var expectedLegacy = expected.DeepClone().AsObject();
        expectedLegacy["CentroOpcional"] = null;
        Assert.True(JsonNode.DeepEquals(expectedLegacy,
            JsonSerializer.SerializeToNode(Assert.Single(SapContractInboundMapper.Mapear(dto)))));

        if (expectedItem is not null) expected["Item"] = expectedItem;
        if (expectedUnit is not null) expected["UnidCont"] = expectedUnit;
        Assert.True(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(snapshot)));

        var expectedDto = expected.DeepClone().AsObject();
        expectedDto["CentroOpcional"] = null;
        Assert.True(JsonNode.DeepEquals(expectedDto, JsonSerializer.SerializeToNode(item)));

        var canonicalizer = new ContratoSapToledoCanonicalizer();
        var canonical = canonicalizer.Canonicalizar(snapshot);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(canonical.PayloadCanonicoJson)));
        Assert.Equal(canonical.FingerprintSha256,
            canonicalizer.Canonicalizar(snapshot).FingerprintSha256);

        // The old positional constructor must still compile and retain its historical output.
        var legacyItem = new SapContractInboundItemHttpDto(
            "Toledo", "1147", null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null, null, null, "3011", null, null);
        var historical = canonicalizer.Canonicalizar(SapContractInboundMapper.MapearSnapshot(legacyItem));
        Assert.Equal(HistoricalJson, historical.PayloadCanonicoJson);
        Assert.Equal(HistoricalFingerprint, historical.FingerprintSha256);

        if (expectedItem is null && expectedUnit is null)
        {
            Assert.Equal(historical.PayloadCanonicoJson, canonical.PayloadCanonicoJson);
            Assert.Equal(historical.FingerprintSha256, canonical.FingerprintSha256);
        }
        else
        {
            Assert.NotEqual(canonical.FingerprintSha256,
                canonicalizer.Canonicalizar(snapshot with { Item = null, UnidCont = null }).FingerprintSha256);
            if (expectedItem is not null)
                Assert.NotEqual(canonical.FingerprintSha256,
                    canonicalizer.Canonicalizar(snapshot with { Item = null }).FingerprintSha256);
            if (expectedUnit is not null)
                Assert.NotEqual(canonical.FingerprintSha256,
                    canonicalizer.Canonicalizar(snapshot with { UnidCont = null }).FingerprintSha256);
        }
    }
}
