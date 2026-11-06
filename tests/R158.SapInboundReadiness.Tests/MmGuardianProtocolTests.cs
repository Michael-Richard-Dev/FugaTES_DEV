using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
using FugaTES.Integracao.Guardian;
namespace R158.SapInboundReadiness.Tests;
public class MmGuardianProtocolTests
{
    internal static PedidoCompraMmLeitura Linha(string pedido="4500002895",string item="00010")
        => new("3000",pedido,item,"ZCOL","3011","000000000001000249","L002",50000m,"KG","0020000613","05","{}");
    [Fact] public void Mapping_uses_real_order_item_supplier_material_and_ten_fields()
    {
        var request=MapeadorProtocoloMm.Montar(Linha(),"https://example.invalid/mm/callback");
        var fields=request.DadosProtocoloPreCadastro.CamposAdicionais.ToDictionary(x=>x.Numero,x=>x.Valor);
        Assert.Equal(0,request.DadosProtocoloPreCadastro.Id);
        Assert.Equal("1000249",request.DadosProtocoloPreCadastro.CodigoItem);
        Assert.Equal("20000613",request.DadosProtocoloPreCadastro.CodigoEmissor);
        Assert.Equal(10,fields.Count);Assert.Equal("E",fields[1000]);Assert.Equal("3011",fields[1001]);
        Assert.Equal("4500002895",fields[1005]);Assert.Equal("10",fields[1009]);
        foreach(var number in new[]{1002,1003,1004,1006,1007,1008})Assert.Equal("0",fields[number]);
        using var json=JsonDocument.Parse(MapeadorProtocoloMm.Serializar(request));
        Assert.Equal("E",json.RootElement.GetProperty("dadosProtocoloPreCadastro").GetProperty("camposAdicionais")[0].GetProperty("valor").GetString());
    }
    [Theory][InlineData("3011")][InlineData("3008")]
    public void Real_plant_is_not_fixed(string plant)
        => Assert.Equal(plant,MapeadorProtocoloMm.Montar(Linha() with{Centro=plant},"https://example.invalid/mm").DadosProtocoloPreCadastro.CamposAdicionais.Single(x=>x.Numero==1001).Valor);
    [Fact] public void Leading_zero_variants_have_same_key_and_distinct_items_do_not()
    {
        Assert.Equal(MapeadorProtocoloMm.ChaveNatural(Linha("0000000123","00010")),MapeadorProtocoloMm.ChaveNatural(Linha("123","10")));
        Assert.NotEqual(MapeadorProtocoloMm.ChaveNatural(Linha()),MapeadorProtocoloMm.ChaveNatural(Linha(item:"00020")));
    }
    [Theory][InlineData("http://example.invalid/mm")][InlineData("https://user:password@example.invalid/mm")][InlineData("https://example.invalid/mm#fragment")][InlineData("")]
    public void Unsafe_callback_is_rejected(string url)=>Assert.Throws<InvalidOperationException>(()=>MapeadorProtocoloMm.Montar(Linha(),url));
    [Theory][InlineData("4000","3000","05")][InlineData("3011","4000","05")][InlineData("3011","3000","02")]
    public void Ineligible_scope_is_rejected(string plant,string company,string state)
        =>Assert.Throws<InvalidOperationException>(()=>MapeadorProtocoloMm.Montar(Linha() with{Centro=plant,Empresa=company,EstadoSap=state},"https://example.invalid/mm"));
    [Fact] public void Automatic_dispatch_is_not_present_yet()
        =>Assert.NotNull(typeof(PedidoCompraMmLeitura).Assembly.GetType("FugaTES.Aplicacao.Servicos.Mm.EnvioAutomaticoMm"));
    [Fact] public void Guardian_mm_wire_gateway_is_not_present_yet()
        =>Assert.NotNull(typeof(GuardianProtocoloSdClient).Assembly.GetType("FugaTES.Integracao.Guardian.GuardianProtocoloMmClient"));
    [Theory]
    [InlineData("100", "OC100")]
    [InlineData("4500002895", "OC4500002895")]
    [InlineData("4500002592", "OC4500002592")]
    [InlineData("0000000100", "OC100")]
    public void Purchase_order_reference_is_OC_followed_by_the_order(string order, string expected)
    {
        var request=MapeadorProtocoloMm.Montar(Linha(order),"https://example.invalid/mm");
        using var json=JsonDocument.Parse(MapeadorProtocoloMm.Serializar(request));
        var data=json.RootElement.GetProperty("dadosProtocoloPreCadastro");
        Assert.Equal(expected,data.GetProperty("numeroReferencia").GetString());
        Assert.Equal("OC"+data.GetProperty("camposAdicionais").EnumerateArray().Single(x=>x.GetProperty("numero").GetInt32()==1005).GetProperty("valor").GetString(),expected);
    }
    [Fact]
    public void Items_of_the_same_order_share_reference_but_keep_distinct_idempotency_keys()
    {
        var a=Linha(item:"10");var b=Linha(item:"20");
        string Reference(PedidoCompraMmLeitura line)
        {
            using var json=JsonDocument.Parse(MapeadorProtocoloMm.Serializar(MapeadorProtocoloMm.Montar(line,"https://example.invalid/mm")));
            return json.RootElement.GetProperty("dadosProtocoloPreCadastro").GetProperty("numeroReferencia").GetString()!;
        }
        Assert.Equal("OC4500002895",Reference(a));Assert.Equal(Reference(a),Reference(b));
        Assert.NotEqual(MapeadorProtocoloMm.ChaveNatural(a),MapeadorProtocoloMm.ChaveNatural(b));
    }}
