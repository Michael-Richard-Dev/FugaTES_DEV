using System.Reflection;
using System.Text.Json;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.Guardian;
using FugaTES.Integracao.Guardian.Transportadoras;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace R165U.Automatic.Tests;

public sealed class MasterdataApiClosureTests
{
    private static Dictionary<string, string?> Profile() => new()
    {
        ["Carrier:Protocols:Guardian:Environment"] = "HML",
        ["Carrier:Protocols:Guardian:ProfileVerified"] = "true",
        ["Carrier:Protocols:Guardian:SignatureVerified"] = "true",
        ["Carrier:Protocols:Guardian:ApprovalReference"] = "SYNTHETIC_READ_ONLY_APPROVAL",
        ["Carrier:Protocols:Guardian:LookupRoute"] = "/api/v0/Guardian/Transportadora/{bp}",
        ["Carrier:Protocols:Guardian:CodeField"] = "codigo",
        ["Carrier:Protocols:Guardian:LegalNameField"] = "razaoSocial",
        ["Carrier:Protocols:Guardian:AbsenceHttpStatus"] = "404"
    };

    private static GuardianCarrierManualContract? Manual(IConfiguration config)
    {
        var method = typeof(CarrierProtocolBinding).GetMethod("BuildGuardianManualContract",
            BindingFlags.Public | BindingFlags.Static, [typeof(IConfiguration)]);
        Assert.NotNull(method);
        Assert.Equal(typeof(GuardianCarrierManualContract), method.ReturnType);
        return (GuardianCarrierManualContract?)method.Invoke(null, [config]);
    }

    [Fact]
    public void Manual_read_contract_does_not_require_or_enable_write_contract()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(Profile()).Build();
        var manual = Manual(config);
        Assert.NotNull(manual);
        Assert.Null(CarrierProtocolBinding.BuildGuardianContract(config));
        Assert.Equal("/api/v0/Guardian/Transportadora/{bp}", manual.LookupRoute);
    }

    [Theory]
    [InlineData("Environment", "PRODUCTION")]
    [InlineData("ProfileVerified", "false")]
    [InlineData("SignatureVerified", "false")]
    [InlineData("ApprovalReference", "")]
    [InlineData("LookupRoute", "https://outside.invalid/{bp}")]
    [InlineData("LookupRoute", "/lookup/without-placeholder")]
    [InlineData("LegalNameField", "codigo")]
    [InlineData("AbsenceHttpStatus", "500")]
    public void Manual_contract_rejects_unproven_or_ambiguous_read_scope(string key, string value)
    {
        var values = Profile();
        values["Carrier:Protocols:Guardian:" + key] = value;
        Assert.Null(Manual(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
    }

    private static GuardianCarrierCreateFieldsR37 Fields(string callback) => new()
    {
        BusinessPartner = "SYNTHETIC_BP", GuardianCode = "SYNTHETIC_CODE",
        FiscalIdentifier = "SYNTHETIC_FISCAL", Estado = 1, Sequencial = 0,
        Callback = callback, EvidenceReference = "SYNTHETIC_BINDING",
        SapIdentityConfirmed = true, CodeApproved = true, FiscalBindingApproved = true,
        EstadoBindingApproved = true, SequencialBindingApproved = true,
        CallbackBindingApproved = true, RequiredFieldContractApproved = true
    };

    private static object Binding(string environment = "HML", bool approved = true,
        bool transport = true, bool source = true, string address = "192.168.17.91", string evidence = "SYNTHETIC")
    {
        var type = typeof(GuardianCarrierCreateFieldsR37).Assembly.GetType(
            "FugaTES.Integracao.Guardian.Transportadoras.MasterdataPrivateHttpBindingR159B");
        Assert.NotNull(type);
        var binding = Activator.CreateInstance(type)!;
        foreach (var entry in new Dictionary<string, object>
        {
            ["Environment"] = environment, ["Approved"] = approved,
            ["PrivateTransportVerified"] = transport, ["GuardianSourceVerified"] = source,
            ["GuardianSourceAddress"] = address, ["ApprovalReference"] = evidence
        }) type.GetProperty(entry.Key)!.SetValue(binding, entry.Value);
        return binding;
    }

    private static GuardianCarrierCreateBodyResultR37 Prepare(GuardianCarrierCreateFieldsR37 fields, object? binding = null)
    {
        if (binding is not null)
        {
            var property = fields.GetType().GetProperty("PrivateHttpCallback");
            Assert.NotNull(property);
            Assert.Equal(binding.GetType(), property.PropertyType);
            property.SetValue(fields, binding);
        }
        using var sap = JsonDocument.Parse("""{"BusinessPartner":"SYNTHETIC_BP","BusinessPartnerName":"Synthetic","BusinessPartnerFullName":"Synthetic legal"}""");
        return GuardianCarrierCreateBodyR37.Prepare(sap.RootElement, fields);
    }

    [Fact]
    public void Existing_https_body_is_unchanged_and_binding_is_not_serialized()
    {
        var result = Prepare(Fields("https://callback.invalid/masterdata"));
        Assert.True(result.Ready);
        using var json = JsonDocument.Parse(result.TransientBody!);
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
        Assert.Equal("https://callback.invalid/masterdata", json.RootElement.GetProperty("voConfiguracao").GetProperty("urlParaRetorno").GetString());
        Assert.False(json.RootElement.TryGetProperty("PrivateHttpCallback", out _));
    }

    private const string PrivateUrl = "http://192.168.17.40/api/guardian/masterdata/transportadoras/callback";

    [Fact]
    public void Private_callback_requires_exact_existing_evidence_bound_contract()
    {
        Assert.False(Prepare(Fields(PrivateUrl)).Ready);
        var result = Prepare(Fields(PrivateUrl), Binding());
        Assert.True(result.Ready);
        using var json = JsonDocument.Parse(result.TransientBody!);
        Assert.Equal(PrivateUrl, json.RootElement.GetProperty("voConfiguracao").GetProperty("urlParaRetorno").GetString());
        Assert.DoesNotContain("SYNTHETIC_BINDING", json.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("approved")]
    [InlineData("transport")]
    [InlineData("source")]
    [InlineData("address")]
    [InlineData("evidence")]
    [InlineData("url")]
    public void Private_callback_never_expands_to_unproven_http(string defect)
    {
        var binding = Binding(environment: defect == "environment" ? "PRODUCTION" : "HML",
            approved: defect != "approved", transport: defect != "transport", source: defect != "source",
            address: defect == "address" ? "192.168.17.90" : "192.168.17.91", evidence: defect == "evidence" ? "" : "SYNTHETIC");
        var result = Prepare(Fields(defect == "url" ? "http://other.invalid/callback" : PrivateUrl), binding);
        Assert.False(result.Ready);
        Assert.Equal("CREATE_BODY_CALLBACK_INVALID", result.Code);
        Assert.Null(result.TransientBody);
    }
}
