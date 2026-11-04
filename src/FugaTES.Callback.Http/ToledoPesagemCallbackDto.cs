using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Callback.Http;

public sealed record ToledoPesagemCallbackDto(string SistemaBalanca,string DataPesagem,DateTime DataPesagemLocal,
    string TipoPesagem,string IdPesagem,string Centro,string Material,decimal PesoBruto,decimal PesoTara,
    decimal PesoAntesDesconto,decimal PesoAposDesconto,IReadOnlyList<ToledoQualidadeCallbackDto> Qualidades,
    IReadOnlyList<ToledoItemPesagemCallbackDto> Pesagens,string DocumentoJson)
{
    /// <summary>
    /// Formatos de data do Guardian. Os MESMOS que o fluxo SD ja aceita (LeitorDevolutivaSd):
    ///
    ///   automatico : "11/3/2026 9:23:54 AM"   - mes/dia, SEM zero a esquerda, com AM/PM
    ///   manual     : "03/11/2026 08:30:44"    - dia/mes, com zero a esquerda, 24 horas
    ///
    /// "M" e "d" aceitam um OU dois digitos; "MM" e "dd" exigem dois. Era essa a falha: a lista
    /// anterior so tinha "MM/dd", e a pesagem 0020345, de 3 de novembro de 2026, foi recusada
    /// porque o dia veio como "3". O mesmo defeito ja tinha derrubado a pesagem 0020340 no lado
    /// do SD, em 02/11/2026 - dois receptores, a mesma lista incompleta.
    ///
    /// Cada ordem aparece so com o seu tipo de relogio, e isso e deliberado: e o relogio que
    /// desfaz a ambiguidade. Juntar as duas faria "03/04/2026 10:00:00" virar 3 de abril ou 4 de
    /// marco conforme a ordem da lista, em silencio.
    /// </summary>
    private static readonly string[] FormatosData=
        ["M/d/yyyy h:mm:ss tt","M/d/yyyy hh:mm:ss tt","d/M/yyyy H:mm:ss","d/M/yyyy HH:mm:ss"];

    private static readonly Regex Digits=new("^[0-9]+$",RegexOptions.CultureInvariant);
    private static readonly Regex DecimalText=new("^[0-9]+(?:\\.[0-9]+)?$",RegexOptions.CultureInvariant);

    public PesagemToledoRecebida Mapear()=>new("TOLEDO",Centro,IdPesagem,DataPesagemLocal,DocumentoJson)
    {
        Material=Material,
        Itens=Pesagens.Select(i=>new ItemPesagemToledoRecebido(i.Indice,i.NrIndiceTotal,i.ContratoACM,i.IdParceiro,i.DocumentoJson)).ToArray()
    };

    public static ToledoPesagemCallbackDto Deserializar(JsonElement root)
    {
        var fields=Fields(root);
        var system=Text(fields,"SistemaBalanca",true)!;
        if(!string.Equals(system,"Toledo",StringComparison.OrdinalIgnoreCase))throw Invalid("SistemaBalanca");
        var date=Text(fields,"DataPesagem",true)!;
        if(!DateTime.TryParseExact(date,FormatosData,
            CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsedDate))throw Invalid("DataPesagem");
        // Wire has no timezone. Preserve local wall time; do not invent UTC or a business timezone.
        parsedDate=DateTime.SpecifyKind(parsedDate,DateTimeKind.Unspecified);
        // Guardian uses E/S; retain its exact value and original JSON at ingress.
        // Numeric legacy values remain accepted here; outbound SAP validation is separate.
        var type=Scalar(fields,"TipoPesagem",true)!;
        if(type is not ("E" or "S"))type=Identifier(fields,"TipoPesagem",true)!;
        var id=Identifier(fields,"IdPesagem",true)!;
        var center=Identifier(fields,"Centro",true)!;var material=Identifier(fields,"Material",true)!;
        foreach(var key in new[]{"UsuarioPesagem","UsuarioClassificacao","NomeMotorista","CpfMotorista","Placa","Observacao"})_ = Text(fields,key,false);
        foreach(var key in new[]{"IdPlanejamento","IdAgendamento","IdTransportadora"})_ = Identifier(fields,key,false);
        var qualities=Collection(fields,"to_QualidPes",allowEmpty:true).Select(x=>
        {
            var f=Fields(x);
            // Empty strings are meaningful source values, not zeros or missing generated measurements.
            return new ToledoQualidadeCallbackDto(Scalar(f,"Tipo",required:false),Scalar(f,"ValorAferido",required:false));
        }).ToArray();
        var items=Collection(fields,"to_Pesagem",allowEmpty:false).Select(x=>
        {
            var f=Fields(x);
            foreach(var key in new[]{"ParceiroNome","ParceiroCpfCnpj","ParceiroEntregaNome","ObservacaoPesagem","ChaveAcessoNFe"})_=Text(f,key,false);
            _=Identifier(f,"IdParceiroEntrega",false);
            var indice=Index(f,"Indice");var total=Index(f,"NrIndiceTotal");
            var percentage=Weight(f,"PercentualDesdobro");if(percentage>100m)throw Invalid("PercentualDesdobro");
            return new ToledoItemPesagemCallbackDto(indice,total,Identifier(f,"ContratoACM",true)!,Identifier(f,"IdParceiro",true)!,
                percentage,Weight(f,"PesoAntesDesconto"),Weight(f,"PesoAposDesconto")){DocumentoJson=x.GetRawText()};
        }).ToArray();
        // Support either explicit contiguous index convention. Preserve the original convention in JSON.
        if(items.Any(x=>x.NrIndiceTotal!=items.Length) || items.Select(x=>x.Indice).Distinct().Count()!=items.Length)
            throw Invalid("to_Pesagem.indices");
        var indexes=items.Select(x=>x.Indice).Order().ToArray();
        if(!indexes.SequenceEqual(Enumerable.Range(0,items.Length)) && !indexes.SequenceEqual(Enumerable.Range(1,items.Length)))
            throw Invalid("to_Pesagem.indices");
        return new(system,date,parsedDate,type,id,center,material,Weight(fields,"PesoBruto"),Weight(fields,"PesoTara"),
            Weight(fields,"PesoAntesDesconto"),Weight(fields,"PesoAposDesconto"),qualities,items,root.GetRawText());
    }
    private static Dictionary<string,JsonElement> Fields(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Object)throw Invalid("object");
        var fields=new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach(var field in value.EnumerateObject())if(!fields.TryAdd(field.Name,field.Value))throw Invalid("duplicate_property");
        return fields;
    }
    private static JsonElement[] Collection(Dictionary<string,JsonElement> f,string key,bool allowEmpty)
    {
        if(!f.TryGetValue(key,out var x) || x.ValueKind!=JsonValueKind.Array || x.GetArrayLength()>1000 || (!allowEmpty&&x.GetArrayLength()==0))throw Invalid(key);
        return x.EnumerateArray().ToArray();
    }
    private static string? Text(Dictionary<string,JsonElement> f,string key,bool required)
    {
        if(!f.TryGetValue(key,out var x)||x.ValueKind==JsonValueKind.Null){if(required)throw Invalid(key);return null;}
        if(x.ValueKind!=JsonValueKind.String)throw Invalid(key);
        var s=x.GetString()!;if(s.Length>4096 || s.Any(c=>char.IsControl(c)&&c is not ('\r' or '\n' or '\t')) || (required&&string.IsNullOrWhiteSpace(s)))throw Invalid(key);
        return s;
    }
    private static string? Scalar(Dictionary<string,JsonElement> f,string key,bool required)
    {
        if(f.TryGetValue(key,out var x)&&x.ValueKind==JsonValueKind.Number)
        {var s=x.GetRawText();if(s.Length>128)throw Invalid(key);return s;}
        return Text(f,key,required);
    }
    private static string? Identifier(Dictionary<string,JsonElement> f,string key,bool required)
    {
        var s=Scalar(f,key,required);
        if(!required&&(string.IsNullOrEmpty(s)||(key=="IdParceiroEntrega"&&string.IsNullOrWhiteSpace(s))))return s;
        if(s is null || s.Length>128 || !Digits.IsMatch(s))throw Invalid(key);
        // No Int64 or Double conversion, no padding, trimming or zero removal.
        return s;
    }
    private static int Index(Dictionary<string,JsonElement> f,string key)
    {
        var s=Identifier(f,key,true)!;
        if(!int.TryParse(s,NumberStyles.None,CultureInfo.InvariantCulture,out var value)||value<0)throw Invalid(key);
        return value;
    }
    private static decimal Weight(Dictionary<string,JsonElement> f,string key)
    {
        // Normalize only the numeric parsing copy. The persisted Guardian document remains exact.
        var s=Scalar(f,key,true)!.Replace(',','.');
        if(s.Length>128 || !DecimalText.IsMatch(s) || !decimal.TryParse(s,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value))throw Invalid(key);
        var dot=s.IndexOf('.');var scale=dot<0?0:s.Length-dot-1;
        var coefficient=BigInteger.Parse(s.Replace(".","",StringComparison.Ordinal),CultureInfo.InvariantCulture);
        var bits=decimal.GetBits(value);var parsed=(BigInteger)(uint)bits[0]+((BigInteger)(uint)bits[1]<<32)+((BigInteger)(uint)bits[2]<<64);
        if(coefficient*BigInteger.Pow(10,(bits[3]>>16)&0x7f)!=parsed*BigInteger.Pow(10,scale))throw Invalid(key);
        return value;
    }
    private static ArgumentException Invalid(string field)=>new("Invalid Toledo field.",field);
}
public sealed record ToledoQualidadeCallbackDto(string? Tipo,string? ValorAferido);
public sealed record ToledoItemPesagemCallbackDto(int Indice,int NrIndiceTotal,string ContratoACM,string IdParceiro,
    decimal PercentualDesdobro,decimal PesoAntesDesconto,decimal PesoAposDesconto)
{ public string DocumentoJson {get;init;}=""; }
