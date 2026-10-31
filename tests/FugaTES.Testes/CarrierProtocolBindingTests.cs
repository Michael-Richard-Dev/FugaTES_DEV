using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.HostIntegracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace FugaTES.Testes;
public sealed class CarrierProtocolBindingTests
{
    [Fact]public void Unproven_profiles_are_unbound(){var c=Config(new());Assert.Null(CarrierProtocolBinding.BuildSapContract(c));Assert.Null(CarrierProtocolBinding.BuildGuardianContract(c));}
    [Fact]public void SAP_contract_requires_new_roles_and_BP_changes(){var d=Sap();d["IncludesNewCrm010Assignments"]="false";Assert.Null(CarrierProtocolBinding.BuildSapContract(Config(d,"Sap")));}
    [Fact]public void SAP_contract_without_approval_reference_is_unbound(){var d=Sap();d.Remove("ApprovalReference");Assert.Null(CarrierProtocolBinding.BuildSapContract(Config(d,"Sap")));}
    [Fact]public void Delta_template_uses_frozen_watermark_and_upper_bound()
    {
        var c=CarrierProtocolBinding.BuildSapContract(Config(Sap(),"Sap"));Assert.NotNull(c);var low=DateTimeOffset.Parse("2026-10-01T00:00:00Z");var upper=low.AddDays(1);
        var uri=c.BuildRequest(new(low,upper,null));Assert.Contains("2026-10-01",Uri.UnescapeDataString(uri.Query));Assert.Contains("2026-10-02",Uri.UnescapeDataString(uri.Query));
        Assert.Equal("sap.invalid",uri.Host);
    }
    [Fact]public void SAP_configured_rows_and_nextlink_are_decoded()
    {
        var c=CarrierProtocolBinding.BuildSapContract(Config(Sap(),"Sap"))!;using var j=JsonDocument.Parse("{\"feed\":{\"rows\":[{\"BP\":\"000001\"}],\"next\":\"/page2\"}}");
        var p=c.Decode(j.RootElement);Assert.Equal("000001",Assert.Single(p.BusinessPartners));Assert.Equal("/page2",p.NextLink);
    }
    [Fact]public void Guardian_PRD_metadata_is_not_accepted(){var d=Guardian();d["Environment"]="PRD";Assert.Null(CarrierProtocolBinding.BuildGuardianContract(Config(d,"Guardian")));}
    [Fact]public void Guardian_external_route_is_rejected(){var d=Guardian();d["LookupRoute"]="//evil.invalid/{bp}";Assert.Null(CarrierProtocolBinding.BuildGuardianContract(Config(d,"Guardian")));}
    [Fact]public void Guardian_fields_are_explicitly_mapped_without_truncation()
    {
        var c=CarrierProtocolBinding.BuildGuardianContract(Config(Guardian(),"Guardian"))!;var payload=new CarrierPayload("000001","Carrier","Carrier Ltd");
        var wire=c.EncodeWrite(new(Guid.NewGuid(),"000001",null,"CREATE",payload));var json=JsonSerializer.SerializeToElement(wire);
        Assert.Equal("000001",json.GetProperty("carrierCode").GetString());Assert.Equal("Carrier Ltd",json.GetProperty("legal").GetString());
        using var response=JsonDocument.Parse("{\"data\":[{\"BP\":\"000001\",\"CODE\":\"G1\",\"NAME\":\"Carrier\",\"LEGAL\":\"Carrier Ltd\"}]}");var found=c.DecodeLookup(response.RootElement);
        Assert.Equal(CarrierLookupState.Unique,found.State);Assert.Equal(payload,found.Payload);
    }
    [Fact]public void Guardian_ambiguous_result_stays_ambiguous()
    {
        var c=CarrierProtocolBinding.BuildGuardianContract(Config(Guardian(),"Guardian"))!;using var j=JsonDocument.Parse("{\"data\":[{},{}]}");Assert.Equal(CarrierLookupState.Ambiguous,c.DecodeLookup(j.RootElement).State);
    }
    [Fact]public void Guardian_missing_mapping_is_not_bound(){var d=Guardian();d.Remove("WriteNameField");Assert.Null(CarrierProtocolBinding.BuildGuardianContract(Config(d,"Guardian")));}
    [Fact]public async Task DI_registration_without_proven_protocols_stays_fail_closed()
    {
        var services=new ServiceCollection();var policy=new CarrierSyncPolicy(new("HML",false,false,DateTimeOffset.UtcNow.AddHours(1),[],[]),TimeProvider.System);
        CarrierProtocolBinding.Register(services,Config(new()),policy);using var provider=services.BuildServiceProvider();
        await Assert.ThrowsAsync<CarrierGatewayException>(()=>provider.GetRequiredService<ISapCarrierDiscovery>().LerPaginaAsync(new(null,null,null),default));
        var result=await provider.GetRequiredService<IGuardianCarrierGateway>().ConsultarAsync("000001",default);Assert.Equal(CarrierLookupState.Failed,result.State);
    }
    [Fact]public void Verified_wire_mapping_is_frozen_against_configuration_mutation()
    {
        var cfg=Config(Guardian(),"Guardian");var contract=CarrierProtocolBinding.BuildGuardianContract(cfg)!;
        cfg["Carrier:Protocols:Guardian:WriteNameField"]="tampered";
        var wire=contract.EncodeWrite(new(Guid.NewGuid(),"000001",null,"CREATE",new("000001","Carrier","Carrier Ltd")));
        var json=JsonSerializer.SerializeToElement(wire);Assert.Equal("Carrier",json.GetProperty("name").GetString());Assert.False(json.TryGetProperty("tampered",out _));
    }
    private static IConfiguration Config(Dictionary<string,string?> d,string? section=null)
    {
        var values=d.ToDictionary(p=>"Carrier:Protocols:"+(section is null?"":section+":")+p.Key,p=>p.Value);
        if(section=="Guardian")
        {
            values["Carrier:ManualReadOnly:Identities:000001:Proven"]="true";
            values["Carrier:ManualReadOnly:Identities:000001:Code"]="G1";
            values["Carrier:ManualReadOnly:Identities:000001:ApprovalReference"]="SYNTHETIC_EXPLICIT_BINDING";
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
    private static Dictionary<string,string?> Sap()=>new(){["Proven"]="true",["ApprovalReference"]="verified-fixture",["IncludesBpChanges"]="true",["IncludesNewCrm010Assignments"]="true",["Origin"]="https://sap.invalid/",["DeltaTemplate"]="/delta?lower={watermark}&upper={upperBound}",["RowsPath"]="feed.rows",["BusinessPartnerField"]="BP",["NextLinkPath"]="feed.next",["PageLimit"]="100"};
    private static Dictionary<string,string?> Guardian()=>new(){["Environment"]="HML",["ProfileVerified"]="true",["SignatureVerified"]="true",["ApprovalReference"]="verified-fixture",["LookupRoute"]="/lookup/{bp}",["CreateRoute"]="/create",["UpdateRoute"]="/update/{code}",["ReadbackRoute"]="/readback/{code}",["RowsPath"]="data",["BusinessPartnerField"]="BP",["CodeField"]="CODE",["NameField"]="NAME",["LegalNameField"]="LEGAL",["WriteCodeField"]="carrierCode",["WriteNameField"]="name",["WriteLegalNameField"]="legal",["ResponseCodePath"]="code",["AbsenceHttpStatus"]="404"};
}


