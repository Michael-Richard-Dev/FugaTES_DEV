using System.Net;
using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.Guardian;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FugaTES.SingleCreate.R41.Tests;

public sealed class GuardianIdentityRegressionTests
{
    private static IConfigurationRoot Profile(bool includeIdentity = true)
    {
        var values = new Dictionary<string, string?>();
        foreach (var pair in new Dictionary<string, string?>
        {
            ["Environment"] = "HML", ["ProfileVerified"] = "true", ["SignatureVerified"] = "true",
            ["ApprovalReference"] = "SYNTHETIC_CONTRACT", ["LookupRoute"] = "/lookup/{bp}",
            ["CreateRoute"] = "/create", ["UpdateRoute"] = "/update/{code}", ["ReadbackRoute"] = "/readback/{code}",
            ["RowsPath"] = "$", ["CodeField"] = "codigo", ["NameField"] = "descricao", ["LegalNameField"] = "razaoSocial",
            ["WriteCodeField"] = "codigo", ["WriteNameField"] = "descricao", ["WriteLegalNameField"] = "razaoSocial",
            ["ResponseCodePath"] = "codigo"
        }) values["Carrier:Protocols:Guardian:" + pair.Key] = pair.Value;
        if (includeIdentity)
        {
            values["Carrier:ManualReadOnly:Identities:0000000042:Proven"] = "true";
            values["Carrier:ManualReadOnly:Identities:0000000042:Code"] = "G00042";
            values["Carrier:ManualReadOnly:Identities:0000000042:ApprovalReference"] = "SYNTHETIC_MAPPING";
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void Guardian_without_wire_bp_uses_exact_approved_mapping_not_numeric_equivalence()
    {
        var contract = CarrierProtocolBinding.BuildGuardianContract(Profile());
        Assert.NotNull(contract);
        using var json = JsonDocument.Parse("""{"codigo":"G00042","descricao":"Synthetic","razaoSocial":"Synthetic legal"}""");
        var found = contract.DecodeLookup(json.RootElement);
        Assert.Equal(CarrierLookupState.Unique, found.State);
        Assert.Equal("G00042", found.GuardianCode);
        Assert.Equal("0000000042", found.Payload!.BusinessPartner);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unproven")]
    [InlineData("evidence")]
    [InlineData("ambiguous")]
    [InlineData("wire-alias")]
    public void Full_contract_never_invents_or_ambiguously_assigns_bp(string defect)
    {
        var config = Profile(defect != "missing");
        config["Carrier:Protocols:Guardian:BusinessPartnerField"] = defect == "wire-alias" ? "codigo" : "BP";
        if (defect == "unproven") config["Carrier:ManualReadOnly:Identities:0000000042:Proven"] = "false";
        if (defect == "evidence") config["Carrier:ManualReadOnly:Identities:0000000042:ApprovalReference"] = "";
        if (defect == "ambiguous")
        {
            config["Carrier:ManualReadOnly:Identities:0000000043:Proven"] = "true";
            config["Carrier:ManualReadOnly:Identities:0000000043:Code"] = "G00042";
            config["Carrier:ManualReadOnly:Identities:0000000043:ApprovalReference"] = "SYNTHETIC_OTHER";
        }
        Assert.Null(CarrierProtocolBinding.BuildGuardianContract(config));
    }

    [Fact]
    public void Lookup_without_matching_approved_code_is_not_unique_and_binding_is_frozen()
    {
        var config = Profile();
        var contract = CarrierProtocolBinding.BuildGuardianContract(config);
        Assert.NotNull(contract);
        config["Carrier:ManualReadOnly:Identities:0000000042:Code"] = "OTHER";
        using var wrong = JsonDocument.Parse("""{"codigo":"OTHER","descricao":"Synthetic","razaoSocial":"Synthetic legal"}""");
        Assert.Equal(CarrierLookupState.Failed, contract.DecodeLookup(wrong.RootElement).State);
        using var right = JsonDocument.Parse("""{"codigo":"G00042","descricao":"Synthetic","razaoSocial":"Synthetic legal"}""");
        Assert.Equal("0000000042", contract.DecodeLookup(right.RootElement).Payload!.BusinessPartner);
    }

    [Theory]
    [InlineData("271", "271")]
    [InlineData("\"000271\"", "000271")]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("1.5", null)]
    [InlineData("9223372036854775808", null)]
    [InlineData("true", null)]
    public async Task Sequence_is_separate_from_code_and_numeric_form_is_validated(string sequence, string? expected)
    {
        using var http = new HttpClient(new Wire(sequence));
        var options = new GuardianHttpOptions { BaseUrl = new("https://synthetic.invalid/"), Timeout = TimeSpan.FromSeconds(5) };
        var adapter = new GuardianCarrierManualReadOnlyAdapter(http, options, new GuardianTokenClient(http, options, new Credentials()),
            new(true, true, "SYNTHETIC", "/api/v0/Guardian/Transportadora/{bp}?idAssinatura=SYNTHETIC_SCOPE", "codigo", "razaoSocial", true));
        var result = await adapter.ConsultarCodigoAsync(new("G00042", "SYNTHETIC_SCOPE", true, "SYNTHETIC"), default);
        Assert.Equal(CarrierLookupState.Unique, result.State);
        Assert.Equal("G00042", result.Codigo);
        if (expected is null) Assert.False(result.Campos.ContainsKey("sequencial"));
        else Assert.Equal(expected, result.Campos["sequencial"]);
    }

    private sealed class Credentials : ICredenciaisGuardianSet26Provider
    {
        public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct) =>
            Task.FromResult(new CredenciaisGuardianSet26("SYNTHETIC", "SYNTHETIC_PASSWORD", "SYNTHETIC_SIGNATURE"));
    }

    private sealed class Wire(string sequence) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("synthetic.invalid", request.RequestUri!.Host);
            var token = request.RequestUri.AbsolutePath == "/api/v2/CreateToken";
            if (!token) Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(token ? "{\"token\":\"SYNTHETIC_TOKEN\"}" :
                    "{\"codigo\":\"G00042\",\"razaoSocial\":\"Synthetic\",\"sequencial\":" + sequence + "}")
            });
        }
    }
}
