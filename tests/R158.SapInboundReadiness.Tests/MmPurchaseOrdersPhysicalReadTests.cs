using System.Text.Json;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.WinForms.Transportadoras;
namespace FugaTES.Testes;
public sealed class MmPurchaseOrdersPhysicalReadTests
{
    [Fact] public async Task Optional_authorized_company3000_MM_GET_records_only_sanitized_counts()
    {
        var path=Environment.GetEnvironmentVariable("FUGATES_MM_PHYSICAL_RESULT");if(string.IsNullOrEmpty(path))return;
        var config=CarrierDesktopProfile.Load(@"C:\FugaTES_DEV110\FugaTES.carrier.json");
        Assert.Equal("110",config["Sap:SapClient"]);
        Assert.Equal(PedidosCompraMmConsultaAdapter.Origem,new Uri(config["Sap:BaseUrl"]!).GetLeftPart(UriPartial.Authority));
        using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false}){Timeout=Timeout.InfiniteTimeSpan};
        var result=await new PedidosCompraMmConsultaAdapter(http,new CarrierServiceSapAuthentication(config)).ConsultarAsync(default);
        File.WriteAllText(path,JsonSerializer.Serialize(new{atUtc=DateTimeOffset.UtcNow,method="GET",sapClient="110",company="3000",plants=new[]{"3011","3008"},
            result.Sucesso,result.Codigo,result.Http,result.Paginas,items=result.Itens.Count,orders=result.Itens.Select(x=>x.Pedido).Distinct().Count(),
            plantCounts=result.Itens.GroupBy(x=>x.Centro).ToDictionary(x=>x.Key,x=>x.Count()),statusCounts=result.Itens.GroupBy(x=>x.EstadoSap).ToDictionary(x=>x.Key,x=>x.Count()),
            posts=0,databaseWrites=0,secretsExported=false},new JsonSerializerOptions{WriteIndented=true}));
        Assert.True(result.Sucesso,result.Codigo);
    }
}
