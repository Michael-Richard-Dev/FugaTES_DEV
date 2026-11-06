using System.Text.Json;
using System.Text.RegularExpressions;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.Mm;
namespace FugaTES.Aplicacao.Servicos.Mm;
/// <summary>Mesmo contrato de protocolo do SD; sem alterar as regras ou tipos SD/ACM.</summary>
public static class MapeadorProtocoloMm
{
    public static string SourceHash(PedidoCompraMmLeitura linha)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(linha))));
    public static string Codigo(string value)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Length>30||!Regex.IsMatch(value,"^[A-Za-z0-9._-]+$",RegexOptions.CultureInvariant))
            throw new InvalidOperationException("MM_CODIGO_INVALIDO");
        return value.All(char.IsAsciiDigit)?(value.TrimStart('0') is {Length:>0} n?n:"0"):value;
    }
    public static string ChaveNatural(PedidoCompraMmLeitura linha)
        => linha.Empresa+"/"+Codigo(linha.Pedido)+"/"+Codigo(linha.Item);
    public static ProtocoloPreCadastroMmRequest Montar(PedidoCompraMmLeitura linha,string urlParaRetorno)
    {
        ArgumentNullException.ThrowIfNull(linha);
        if(linha.Empresa!="3000"||linha.Centro is not("3011" or "3008")||linha.EstadoSap!="05"||linha.Quantidade<0)
            throw new InvalidOperationException("MM_ITEM_FORA_DO_ESCOPO");
        if(!Uri.TryCreate(urlParaRetorno,UriKind.Absolute,out var url)||url.Scheme!="https"||url.UserInfo.Length!=0||url.Fragment.Length!=0)
            throw new InvalidOperationException("MM_CALLBACK_INVALIDO");
        var pedido=Codigo(linha.Pedido);var item=Codigo(linha.Item);
        if(pedido=="0"||item=="0"||!pedido.All(char.IsAsciiDigit)||!item.All(char.IsAsciiDigit))throw new InvalidOperationException("MM_PEDIDO_ITEM_INVALIDO");
        var material=Codigo(linha.Material);var fornecedor=Codigo(linha.Fornecedor);
        if(material=="0"||fornecedor=="0"||fornecedor.Length>20)throw new InvalidOperationException("MM_CADASTRO_INVALIDO");
        return new(new(urlParaRetorno),new(0,material,fornecedor,new CampoAdicionalGuardianSet26[]
        {new(1000,"E"),new(1001,linha.Centro),new(1002,"0"),new(1003,"0"),new(1004,"0"),
         new(1005,pedido),new(1006,"0"),new(1007,"0"),new(1008,"0"),new(1009,item)}) { NumeroReferencia = "OC" + pedido });
    }
    public static string Serializar(ProtocoloPreCadastroMmRequest request)=>JsonSerializer.Serialize(request,new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
