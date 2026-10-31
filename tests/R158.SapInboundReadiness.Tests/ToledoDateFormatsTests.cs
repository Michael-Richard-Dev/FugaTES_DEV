using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Callback.Http;
using FugaTES.Integracao.SAP.Http;

namespace FugaTES.Testes;

public sealed class ToledoDateFormatsTests
{
    const string Document = """{"SistemaBalanca":"Toledo","DataPesagem":"DATE_VALUE","TipoPesagem":"S","IdPesagem":"00900010","Centro":"3011","Material":"9000000","PesoBruto":"15000.0","PesoTara":"10000.0","PesoAntesDesconto":"5000.0","PesoAposDesconto":"5000.0","Observacao":"SYNTHETIC OFFLINE DATE TEST","to_QualidPes":[{"Tipo":"UMIDADE","ValorAferido":"0.0"}],"to_Pesagem":[{"Indice":1,"NrIndiceTotal":1,"ContratoACM":"0099999910","IdParceiro":"90000010","IdParceiroEntrega":"90000010","PercentualDesdobro":"100","PesoAntesDesconto":"5000.0","PesoAposDesconto":"5000.0","ChaveAcessoNFe":""}]}""";

    [Theory]
    [InlineData("31/10/2026 11:46:05",2026,10,31,11,46,5)]
    [InlineData("10/31/2026 11:46:05 AM",2026,10,31,11,46,5)]
    [InlineData("31/10/2026 23:59:58",2026,10,31,23,59,58)]
    [InlineData("10/31/2026 11:59:58 PM",2026,10,31,23,59,58)]
    [InlineData("31/10/2026 00:00:00",2026,10,31,0,0,0)]
    [InlineData("10/31/2026 12:00:00 AM",2026,10,31,0,0,0)]
    [InlineData("10/31/2026 1:46:05 PM",2026,10,31,13,46,5)]
    public void Receipt_and_SAP_keep_the_same_Brasilia_instant_and_original_JSON(
        string date,int year,int month,int day,int hour,int minute,int second)
    {
        var original=Input(date);
        using var document=JsonDocument.Parse(original);
        var receipt=ToledoPesagemCallbackDto.Deserializar(document.RootElement);
        Assert.Equal(new DateTime(year,month,day,hour,minute,second,DateTimeKind.Unspecified),receipt.DataPesagemLocal);
        Assert.Equal(DateTimeKind.Unspecified,receipt.DataPesagemLocal.Kind);
        Assert.Equal(date,receipt.DataPesagem);
        Assert.Equal(original,receipt.DocumentoJson);
        Assert.Equal(original,receipt.Mapear().DocumentoJson);
        var prepared=ToledoSapODataPayload.Prepare(receipt.DocumentoJson,new("America/Sao_Paulo"));
        var epoch=new DateTimeOffset(year,month,day,hour,minute,second,TimeSpan.FromHours(-3)).ToUnixTimeMilliseconds();
        Assert.Equal(epoch,prepared.EpochMilliseconds);
        using var outbound=JsonDocument.Parse(prepared.Json);
        Assert.Equal("/Date("+epoch.ToString(CultureInfo.InvariantCulture)+")/",outbound.RootElement.GetProperty("DataPesagem").GetString());
    }

    [Fact]
    public void Brazilian_ambiguous_day_and_month_are_never_interpreted_as_US_without_AM_PM()
    {
        using var source=JsonDocument.Parse(Input("02/03/2026 11:46:05"));
        var receipt=ToledoPesagemCallbackDto.Deserializar(source.RootElement);
        Assert.Equal(new DateTime(2026,3,2,11,46,5),receipt.DataPesagemLocal);
        var br=ToledoSapODataPayload.Prepare(receipt.DocumentoJson,new("America/Sao_Paulo"));
        var us=ToledoSapODataPayload.Prepare(Input("03/02/2026 11:46:05 AM"),new("America/Sao_Paulo"));
        Assert.Equal(new DateTimeOffset(2026,3,2,14,46,5,TimeSpan.Zero).ToUnixTimeMilliseconds(),br.EpochMilliseconds);
        Assert.Equal(br.EpochMilliseconds,us.EpochMilliseconds);
    }

    [Theory][InlineData("pt-BR")][InlineData("en-US")]
    public void Machine_culture_cannot_change_either_date_interpretation(string culture)
    {
        var previous=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(culture);
            foreach(var text in new[]{"31/10/2026 11:46:05","10/31/2026 11:46:05 AM"})
            {
                using var source=JsonDocument.Parse(Input(text));
                var receipt=ToledoPesagemCallbackDto.Deserializar(source.RootElement);
                Assert.Equal(new DateTime(2026,10,31,11,46,5),receipt.DataPesagemLocal);
                Assert.Equal(new DateTimeOffset(2026,10,31,14,46,5,TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    ToledoSapODataPayload.Prepare(receipt.DocumentoJson,new("America/Sao_Paulo")).EpochMilliseconds);
            }
        }
        finally{CultureInfo.CurrentCulture=previous;}
    }

    [Theory]
    [InlineData("31/02/2026 11:46:05")]
    [InlineData("31/10/2026 24:00:00")]
    [InlineData("31/10/2026 11:46")]
    [InlineData("2026-10-31T11:46:05")]
    [InlineData("31/10/2026 11:46:05 PM")]
    [InlineData("02/30/2026 11:46:05 AM")]
    public void Invalid_dates_stay_rejected_before_storage_or_business_transport(string date)
    {
        using var source=JsonDocument.Parse(Input(date));
        Assert.Equal("DataPesagem",Assert.Throws<ArgumentException>(()=>ToledoPesagemCallbackDto.Deserializar(source.RootElement)).ParamName);
        Assert.Equal("SAP_WEIGHING_DATE_INVALID",Assert.Throws<ToledoSapPayloadException>(()=>
            ToledoSapODataPayload.Prepare(Input(date),new("America/Sao_Paulo"))).Code);
    }

    [Fact]
    public void Brazilian_local_date_still_requires_explicit_outbound_timezone()
    {
        Assert.Equal("SAP_WEIGHING_TIMEZONE_REQUIRED",Assert.Throws<ToledoSapPayloadException>(()=>
            ToledoSapODataPayload.Prepare(Input("31/10/2026 11:46:05"),new())).Code);
    }

    [Theory]
    [InlineData("31/10/2026 11:46:05")]
    [InlineData("10/31/2026 11:46:05 AM")]
    public void Audited_SAP_JSON_can_be_reproduced_from_either_original_date_format(string date)
    {
        var original=Input(date);
        var sent=ToledoSapODataPayload.Prepare(original,new("America/Sao_Paulo"));
        var readback=ToledoSapODataPayload.ReproduceAudited(original,new("America/Sao_Paulo"),sent.Sha256,ToledoSapPreparedPayload.Version);
        Assert.NotNull(readback);
        Assert.Equal(sent.Json,readback.Value.Payload.Json);
        Assert.Equal(sent.Sha256,readback.Value.Payload.Sha256);
        Assert.Equal(sent.EpochMilliseconds,readback.Value.Payload.EpochMilliseconds);
        Assert.Equal(original,Input(date));
    }

    static string Input(string date)
    {
        var node=JsonNode.Parse(Document)!.AsObject();node["DataPesagem"]=date;return node.ToJsonString();
    }
}
