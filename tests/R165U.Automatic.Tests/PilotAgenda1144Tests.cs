using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Integracao.Guardian;
using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class PilotAgenda1144Tests
{
    [Fact]
    public async Task Real_1144_snapshot_hash_and_final_wire_are_preserved_using_only_fake_transports()
    {
        var snapshot=JsonSerializer.Deserialize<FugaTES.Aplicacao.Modelos.ContratosSapToledo.ContratoSapToledoSnapshot>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Original1144Snapshot.json")))!;
        var f=new AutomaticFixture();
        var (version,claim)=f.Add("0000001144",_=>snapshot);
        Assert.Equal("9BF8A01AEE4FE049C267F1191C0C6E2B863BDB076E472D4E843A9683FC40EC5C",version.FingerprintSha256);
        var options=Options(1) with
        {
            SapIdentifiersCopiedVerbatimApproved=false,SapContractCopiedVerbatimApproved=true,
            ApprovedPartnerMapping=new(){NumeroContrato=snapshot.NumeroContrato,Item=snapshot.Item,Centro=snapshot.Centro,
                SapPartnerId=snapshot.IdParceiro,GuardianCode="20000051",EvidenceReference="SYNTHETIC_REFERENCE_ONLY"},
            ApprovedMaterialMapping=new(){NumeroContrato=snapshot.NumeroContrato,Item=snapshot.Item,Centro=snapshot.Centro,
                SapMaterial=snapshot.Material,GuardianCode="7000000",EvidenceReference="SYNTHETIC_REFERENCE_ONLY"},
            CallbackUrl="http://192.168.17.40/api/guardian/set26/callback/recebimento",
            CallbackBinding=new("HML","http://192.168.17.40/api/guardian/set26/callback/recebimento",
                "192.168.17.91/32",true,true,"SYNTHETIC_REFERENCE_ONLY")
        };
        var result=await f.Pipeline(options).ExecutarAsync(claim,default);
        Assert.Equal("R165U_CONFIRMED",result.Etapa);
        Assert.Null(snapshot.Item);
        Assert.Equal(5000m,snapshot.QuantidadeSaldoDevolucao);
        Assert.Equal(10000m,f.Soap.Sent!.ValorContrato);
        var json=JsonSerializer.Serialize(GuardianSet26HttpClient.CriarPayloadR57(f.Rest.Sent!),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("{{",json);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"PILOT1144_PREVIEW.json"),json);
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task Unapproved_agenda_blocks_protocol_without_fabricating_a_value(int agenda)
    {
        var f=new AutomaticFixture();
        var (_,claim)=f.Add("0000001144");
        var result=await f.Pipeline(Options(agenda)).ExecutarAsync(claim,default);
        Assert.Equal("R165U_PROTOCOL_BLOCKED",result.Etapa);
        Assert.Equal(0,f.Rest.Creates);
    }
    private static AcmR57NormalOptions Options(int agenda)
    {
        var json=JsonSerializer.SerializeToNode(AutomaticFixture.Options())!.AsObject();
        json["IdAgenda"]=agenda;
        using var stream=new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString()));
        return new ConfigurationBuilder().AddJsonStream(stream).Build().Get<AcmR57NormalOptions>()!;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task Explicit_agenda_is_serialized_without_transport_or_placeholders(int agenda)
    {
        var f=new AutomaticFixture();
        var (_,claim)=f.Add("0000001144");
        var result=await f.Pipeline(Options(agenda)).ExecutarAsync(claim,default);
        Assert.Equal("R165U_CONFIRMED",result.Etapa);
        Assert.Equal(agenda,f.Rest.Sent!.Dados.IdAgenda);
        var payload=GuardianSet26HttpClient.CriarPayloadR57(f.Rest.Sent);
        var json=JsonSerializer.Serialize(payload,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document=JsonDocument.Parse(json);
        var data=document.RootElement.GetProperty("dadosProtocoloPreCadastro");
        Assert.Equal(0,data.GetProperty("id").GetInt32());
        Assert.Equal(agenda,data.GetProperty("idAgenda").GetInt32());
        Assert.Equal(JsonValueKind.Null,data.GetProperty("codigoFluxo").ValueKind);
        Assert.False(data.TryGetProperty("codigoTransportadora",out _));
        Assert.Equal("OC1144",data.GetProperty("numeroReferencia").GetString());
        Assert.DoesNotContain("{{",json);
    }
}
