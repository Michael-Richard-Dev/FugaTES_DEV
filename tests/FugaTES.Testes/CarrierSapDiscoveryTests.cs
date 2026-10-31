using System.Net;
using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Aplicacao.CasosDeUso.SAP;
using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;
namespace FugaTES.Testes;
public sealed class CarrierSapDiscoveryTests
{
    [Fact] public async Task Missing_proven_delta_contract_blocks_before_HTTP()
    {
        var error=await Assert.ThrowsAsync<CarrierGatewayException>(()=>new CarrierSapDiscoveryAdapter().LerPaginaAsync(new(null,null,null),default));
        Assert.Equal("SAP_CRM010_DELTA_CONTRACT_NOT_PROVEN",error.Code);
    }
    [Theory][InlineData(false,true)][InlineData(true,false)] public async Task Both_BP_changes_and_new_role_assignments_are_required(bool bp,bool role)
    {
        var handler=new Handler("{}"); var contract=Contract() with { IncludesBpChanges=bp, IncludesNewCrm010Assignments=role };
        await Assert.ThrowsAsync<CarrierGatewayException>(()=>Adapter(handler,contract).LerPaginaAsync(new(null,null,null),default)); Assert.Equal(0,handler.Calls);
    }
    [Fact] public async Task Duplicates_are_removed_and_each_candidate_uses_INC002_validation()
    {
        var h=new Handler("{\"bps\":[\"000001\",\"000001\",\"000002\",\"000003\"]}");var g=new Gateway();
        var page=await Adapter(h,Contract(),g).LerPaginaAsync(new(null,null,null),default);
        Assert.Single(page.Carriers);Assert.Equal("000001",page.Carriers[0].BusinessPartner);Assert.Equal(3,g.Calls);
        Assert.True(page.End);Assert.NotNull(page.NextCursor.Watermark);Assert.Null(page.NextCursor.PageToken);Assert.True(h.AuthPresent);
    }
    [Fact] public async Task External_next_link_is_rejected_without_advancing()
    {
        var h=new Handler("{\"bps\":[],\"next\":\"https://evil.invalid/page\"}");
        var e=await Assert.ThrowsAsync<CarrierGatewayException>(()=>Adapter(h,Contract()).LerPaginaAsync(new(null,null,null),default));Assert.Equal("SAP_DELTA_CURSOR_INVALID",e.Code);Assert.Equal(1,h.Calls);
    }
    [Fact] public async Task Repeated_page_cursor_is_rejected()
    {
        var h=new Handler("{\"bps\":[],\"next\":\"/delta/page2\"}");
        await Assert.ThrowsAsync<CarrierGatewayException>(()=>Adapter(h,Contract()).LerPaginaAsync(new(null,DateTimeOffset.UtcNow,"/delta/page2"),default));
    }
    [Fact] public async Task Malformed_JSON_does_not_produce_checkpoint()
    {
        await Assert.ThrowsAsync<CarrierGatewayException>(()=>Adapter(new Handler("broken"),Contract()).LerPaginaAsync(new(null,null,null),default));
    }
    [Fact] public async Task Upper_bound_is_preserved_across_pages()
    {
        var upper=DateTimeOffset.UtcNow.AddMinutes(-1);var lower=upper.AddDays(-1);
        var page=await Adapter(new Handler("{\"bps\":[],\"next\":\"/delta/page2\"}"),Contract()).LerPaginaAsync(new(lower,upper,null),default);
        Assert.False(page.End);Assert.Equal(upper,page.NextCursor.UpperBound);Assert.Equal(lower,page.NextCursor.Watermark);
    }
    [Fact] public async Task Page_limit_is_enforced_before_individual_queries()
    {
        var g=new Gateway();await Assert.ThrowsAsync<CarrierGatewayException>(()=>Adapter(new Handler("{\"bps\":[\"000001\",\"000002\"]}"),Contract() with {PageLimit=1},g).LerPaginaAsync(new(null,null,null),default));Assert.Equal(0,g.Calls);
    }
    [Fact]public async Task Persisted_previous_page_hash_blocks_A_B_A_cycle()
    {
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("https://sap.invalid/delta/page1")));
        var cursor=new CarrierCursor(null,DateTimeOffset.UtcNow,"/delta/page2"){VisitedPages=[hash]};
        await Assert.ThrowsAsync<CarrierGatewayException>(()=>Adapter(new Handler("{\"bps\":[],\"next\":\"/delta/page1\"}"),Contract()).LerPaginaAsync(cursor,default));
    }
    [Fact]public async Task Checkpoint_contains_only_page_hashes_and_is_bounded()
    {
        var page=await Adapter(new Handler("{\"bps\":[],\"next\":\"/delta/page2\"}"),Contract()).LerPaginaAsync(new(null,null,null),default);
        var hash=Assert.Single(page.NextCursor.VisitedPages);Assert.Equal(64,hash.Length);Assert.DoesNotContain("http",hash,StringComparison.OrdinalIgnoreCase);
    }
    private static SapCarrierDeltaContract Contract()=>new(new("https://sap.invalid/"),"fixture-verified-delta",true,true,c=>new Uri("https://sap.invalid"+(c.PageToken??"/delta")),j=>new(j.GetProperty("bps").EnumerateArray().Select(x=>x.GetString()!).ToArray(),j.TryGetProperty("next",out var n)?n.GetString():null));
    private static CarrierSapDiscoveryAdapter Adapter(Handler h,SapCarrierDeltaContract c,Gateway? g=null)=>new(new HttpClient(h),new Auth(),new ConsultarTransportadoraSapCasoDeUso(g??new Gateway()),c);
    private sealed class Auth:IAutenticacaoSap {public Task AplicarAsync(HttpRequestMessage r,CancellationToken ct){r.Headers.Add("X-Fixture-Auth","present");return Task.CompletedTask;}}
    private sealed class Handler(string json):HttpMessageHandler {public int Calls;public bool AuthPresent;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Calls++;AuthPresent=r.Headers.Contains("X-Fixture-Auth");return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)});}}
    private sealed class Gateway:IConsultaTransportadoraSapGateway
    {
        public int Calls;
        public Task<ResultadoConsultaSap<ParceiroNegocioConsultado>> ConsultarAsync(string bp,CancellationToken ct)
        {
            Calls++;if(bp=="000002")return Task.FromResult(ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(StatusConsultaSap.CategoriaNaoCorrespondente));
            return Task.FromResult(ResultadoConsultaSap<ParceiroNegocioConsultado>.Sucesso(new(bp,"Carrier",[],null,[],[],bp=="000003"?SituacaoBloqueioSap.Bloqueado:SituacaoBloqueioSap.NaoBloqueado,CategoriaParceiroNegocio.Transportadora){NomeCompletoParceiroNegocio="Carrier Ltd"}));
        }
    }
}

