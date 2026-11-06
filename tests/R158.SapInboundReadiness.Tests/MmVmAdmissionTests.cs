using System.Net;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.WinForms.Mm;
namespace FugaTES.Testes;
public sealed class MmVmAdmissionTests
{
    [Theory]
    [InlineData("2026-10-31",0)][InlineData("2026-11-01",1)][InlineData("2026-11-02",1)]
    public async Task Creation_cutoff_is_fixed_and_inclusive(string date,int count)
    {
        var header=PedidosCompraMmTests.Header();header["CreationDate"]=date;
        using var http=new HttpClient(new PedidosCompraMmTests.Transport(_=>PedidosCompraMmTests.Page(header)));
        var result=await PedidosCompraMmTests.Query(http).ConsultarAsync(default);
        Assert.True(result.Sucesso,result.Codigo);Assert.Equal(count,result.Itens.Count);
    }
    [Theory][InlineData(null)][InlineData("")][InlineData("01/11/2026")][InlineData("2026-02-30")]
    public async Task Unknown_creation_date_fails_closed(string? date)
    {
        var header=PedidosCompraMmTests.Header();if(date is null)header.Remove("CreationDate");else header["CreationDate"]=date;
        using var http=new HttpClient(new PedidosCompraMmTests.Transport(_=>PedidosCompraMmTests.Page(header)));
        var result=await PedidosCompraMmTests.Query(http).ConsultarAsync(default);
        Assert.False(result.Sucesso);Assert.Empty(result.Itens);
    }
    [Fact] public async Task Uses_v4_EdmDate_filter_not_SD_datetime_literal()
    {
        var transport=new PedidosCompraMmTests.Transport(_=>PedidosCompraMmTests.Page(PedidosCompraMmTests.Header()));
        using var http=new HttpClient(transport);await PedidosCompraMmTests.Query(http).ConsultarAsync(default);
        var query=Uri.UnescapeDataString(Assert.Single(transport.Uris).Query);
        Assert.Contains("CreationDate ge 2026-11-01",query);Assert.Contains("CreationDate,",query);Assert.DoesNotContain("datetime'",query);
    }
    [Fact] public void Three_minute_poll_does_not_expire_at_ninety_seconds()
    {
        var now=DateTimeOffset.UtcNow;
        var snapshot=new MmPurchaseOrderSnapshot{State="MM_SAP_COMPLETE",LastSuccessUtc=now.AddSeconds(-170)};
        Assert.True(snapshot.IsFresh(now));Assert.False(snapshot.IsFresh(now.AddMinutes(10)));
    }
}
