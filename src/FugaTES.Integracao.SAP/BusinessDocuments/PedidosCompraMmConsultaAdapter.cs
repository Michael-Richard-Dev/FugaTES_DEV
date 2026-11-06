using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Integracao.SAP.Autenticacao;

namespace FugaTES.Integracao.SAP.BusinessDocuments;

/// <summary>Leitura V4 completa e restrita: não compartilha o caminho de escrita SAP.</summary>
/// <summary>
/// O ambiente vem de quem compoe o adaptador, nao de constantes aqui dentro: o MM de DEV110 e o
/// de QAS120 rodam o MESMO binario e so se distinguem pelo que tem configurado.
///
/// Ate 05/11/2026 a origem e o sap-client estavam ESCRITOS nesta classe. Um MM de QAS teria lido
/// os pedidos do DEV com toda a aparencia de sucesso.
///
/// <paramref name="ambienteDoProcesso"/> e opcional por uma razao de convivencia, nao de desenho:
/// o servico MM da VM (MmLinuxComposition) ainda nao tem caminho de configuracao para o ambiente,
/// e exigir o argumento quebraria a composicao dele. Quando o MM do QAS120 existir, ele tera de
/// passar o seu - e este padrao deve sair junto.
/// </summary>
public sealed class PedidosCompraMmConsultaAdapter(
    HttpClient http,
    IAutenticacaoSap authentication,
    FugaTES.Aplicacao.Configuracao.AmbienteSap? ambienteDoProcesso = null) : IPedidosCompraMmConsulta
{
    private readonly FugaTES.Aplicacao.Configuracao.AmbienteSap ambiente =
        ambienteDoProcesso ?? FugaTES.Aplicacao.Configuracao.AmbientesSap.Dev110;

    public const string Rota = "/sap/opu/odata4/sap/api_purchaseorder_2/srvd_a2x/sap/purchaseorder/0001/";
    private const int PageLimit=1000, RowLimit=100000, PageBytes=4*1024*1024, TotalBytes=64*1024*1024;
    private const string HeaderSelect="CreationDate,PurchaseOrder,PurchaseOrderType,CompanyCode,Supplier,PurchasingProcessingStatus,PurchaseOrderDeletionCode,ReleaseIsNotCompleted,PurchasingCompletenessStatus";
    private const string ItemSelect="PurchaseOrder,PurchaseOrderItem,CompanyCode,Plant,Material,MaterialGroup,OrderQuantity,PurchaseOrderQuantityUnit,PurchasingDocumentDeletionCode";

    public async Task<MmConsultaResultado> ConsultarAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var ct=deadline.Token;
        var seen=new HashSet<string>(StringComparer.Ordinal);
        var orders=new HashSet<string>(StringComparer.Ordinal);
        var rows=new Dictionary<string,PedidoCompraMmLeitura>(StringComparer.Ordinal);
        int pages=0, bytes=0;int? lastHttp=null;
        try
        {
            var filter="CreationDate ge 2026-11-01 and CompanyCode eq '3000' and PurchasingProcessingStatus eq '05' and ReleaseIsNotCompleted eq false and PurchasingCompletenessStatus eq false and PurchaseOrderDeletionCode eq ''";
            var expand="_PurchaseOrderItem($select="+ItemSelect+";$filter=(Plant eq '3011' or Plant eq '3008') and PurchasingDocumentDeletionCode eq '';$orderby=PurchaseOrderItem)";
            Uri? next=new(ambiente.Origem+Rota+"PurchaseOrder?sap-client="+Uri.EscapeDataString(ambiente.SapClient)+"&$select="+Uri.EscapeDataString(HeaderSelect)+"&$filter="+Uri.EscapeDataString(filter)+"&$expand="+Uri.EscapeDataString(expand)+"&$orderby=PurchaseOrder");
            while(next is not null)
            {
                var current=next;
                using var page=await GetPage(current);
                foreach(var header in Values(page.RootElement).EnumerateArray())
                {
                    var po=Code(header,"PurchaseOrder",10);
                    if(!orders.Add(po)||orders.Count>RowLimit)throw new FormatException();
                    var company=Text(header,"CompanyCode");var status=Text(header,"PurchasingProcessingStatus");
                    var supplier=Text(header,"Supplier",true);var type=Text(header,"PurchaseOrderType",true);
                    var deletion=Text(header,"PurchaseOrderDeletionCode");
                    var release=Bool(header,"ReleaseIsNotCompleted");var incomplete=Bool(header,"PurchasingCompletenessStatus");
                    if(company!="3000"||status!="05"||deletion.Length!=0||release||incomplete)continue;
                    if(!header.TryGetProperty("_PurchaseOrderItem",out var items)||items.ValueKind!=JsonValueKind.Array)throw new FormatException();
                    var creation=Text(header,"CreationDate");
                    if(!DateOnly.TryParseExact(creation,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var created))throw new FormatException();
                    if(created<MmCollectionPolicy.CreationCutoff)continue;
                    AddItems(items,po,type,supplier,status,created);
                    var itemNext=Next(header,"_PurchaseOrderItem@odata.nextLink",current);
                    while(itemNext is not null)
                    {
                        var itemCurrent=itemNext;
                        using var itemPage=await GetPage(itemCurrent);
                        AddItems(Values(itemPage.RootElement),po,type,supplier,status,created);
                        itemNext=Next(itemPage.RootElement,"@odata.nextLink",itemCurrent);
                    }
                }
                next=Next(page.RootElement,"@odata.nextLink",current);
            }
            return new(true,"MM_SAP_COMPLETE",lastHttp,pages,rows.Values.OrderBy(x=>x.Pedido,StringComparer.Ordinal).ThenBy(x=>x.Item,StringComparer.Ordinal).ToArray());
        }
        catch(MmHttpException e){return Failed(e.Code,e.Status);}
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested){return Failed("MM_SAP_TIMEOUT",lastHttp);}
        catch(HttpRequestException){return Failed("MM_SAP_UNAVAILABLE",lastHttp);}
        catch(Exception e) when(e is JsonException or FormatException or InvalidOperationException or OverflowException or ArgumentException)
        {return Failed("MM_SAP_RESPONSE_INVALID",lastHttp);}
        // Provider exceptions are sanitized here; caller never receives its credential diagnostics.
        catch(Exception) when(!cancellationToken.IsCancellationRequested){return Failed("MM_SAP_PROVIDER_UNAVAILABLE",lastHttp);}

        MmConsultaResultado Failed(string code,int? status)=>new(false,code,status,pages,Array.Empty<PedidoCompraMmLeitura>());
        async Task<JsonDocument> GetPage(Uri raw)
        {
            var uri=ValidarNoAmbiente(raw); // before credential resolution
            if(!seen.Add(uri.AbsoluteUri)||++pages>PageLimit)throw new FormatException();
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);
            request.Headers.TryAddWithoutValidation("Accept","application/json;IEEE754Compatible=true");
            request.Headers.TryAddWithoutValidation("Prefer","odata.maxpagesize=200");
            await authentication.AplicarAsync(request,ct);
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            lastHttp=(int)response.StatusCode;
            if(response.StatusCode!=HttpStatusCode.OK)
                throw new MmHttpException(lastHttp.Value,lastHttp switch{401=>"MM_SAP_AUTHENTICATION_REQUIRED",403=>"MM_SAP_AUTHORIZATION_REQUIRED",400=>"MM_SAP_QUERY_REJECTED",_=>"MM_SAP_UNAVAILABLE"});
            if(response.Content.Headers.ContentLength>PageBytes)throw new FormatException();
            await using var stream=await response.Content.ReadAsStreamAsync(ct);
            using var memory=new MemoryStream();var buffer=new byte[8192];int count;
            while((count=await stream.ReadAsync(buffer,ct))!=0)
            {
                bytes=checked(bytes+count);
                if(memory.Length+count>PageBytes||bytes>TotalBytes)throw new FormatException();
                memory.Write(buffer,0,count);
            }
            return JsonDocument.Parse(memory.ToArray(),new JsonDocumentOptions{MaxDepth=32});
        }
        void AddItems(JsonElement items,string po,string type,string supplier,string status,DateOnly created)
        {
            foreach(var item in items.EnumerateArray())
            {
                if(Code(item,"PurchaseOrder",10)!=po)throw new FormatException();
                var line=Code(item,"PurchaseOrderItem",5);var plant=Text(item,"Plant");
                var company=Text(item,"CompanyCode");var deletion=Text(item,"PurchasingDocumentDeletionCode");
                if(company!="3000"||plant is not("3011" or "3008")||deletion.Length!=0)continue;
                var material=Text(item,"Material");var group=Text(item,"MaterialGroup");var unit=Text(item,"PurchaseOrderQuantityUnit",true);
                if(!item.TryGetProperty("OrderQuantity",out var quantity)||quantity.ValueKind is not(JsonValueKind.String or JsonValueKind.Number))throw new FormatException();
                var number=quantity.ValueKind==JsonValueKind.String?quantity.GetString():quantity.GetRawText();
                if(!decimal.TryParse(number,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out var amount)||amount<0)throw new FormatException();
                // Projection explicitly retains source field names; not a create payload or a synthetic SAP response.
                var json=JsonSerializer.Serialize(new{CreationDate=created.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),PurchaseOrder=po,PurchaseOrderType=type,CompanyCode=company,Supplier=supplier,PurchasingProcessingStatus=status,
                    _PurchaseOrderItem=new{PurchaseOrder=po,PurchaseOrderItem=line,Plant=plant,Material=material,MaterialGroup=group,OrderQuantity=amount,PurchaseOrderQuantityUnit=unit}},new JsonSerializerOptions{WriteIndented=true});
                if(rows.Count>=RowLimit||!rows.TryAdd(po+"/"+line,new(company,po,line,type,plant,material,group,amount,unit,supplier,status,json,created)))throw new FormatException();
            }
        }
    }
    private static JsonElement Values(JsonElement root)
    {if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("value",out var value)||value.ValueKind!=JsonValueKind.Array)throw new FormatException();return value;}
    private static string Text(JsonElement obj,string name,bool nonEmpty=false)
    {if(obj.ValueKind!=JsonValueKind.Object||!obj.TryGetProperty(name,out var value)||value.ValueKind!=JsonValueKind.String)throw new FormatException();var text=value.GetString()!;if(text.Length>1000||text.Any(char.IsControl)||(nonEmpty&&string.IsNullOrWhiteSpace(text)))throw new FormatException();return text;}
    private static string Code(JsonElement obj,string name,int max)
    {var value=Text(obj,name,true);if(value.Length>max||!value.All(char.IsAsciiDigit))throw new FormatException();return value;}
    private static bool Bool(JsonElement obj,string name)
    {if(!obj.TryGetProperty(name,out var value)||value.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new FormatException();return value.GetBoolean();}
    private Uri? Next(JsonElement root,string key,Uri current)
    {if(!root.TryGetProperty(key,out var property))return null;if(property.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(property.GetString()))throw new FormatException();return ValidarNoAmbiente(new Uri(current,property.GetString()));}

    /// <summary>
    /// A conferencia que este adaptador faz em cada URL que vai buscar: a rota, e o ambiente
    /// EXATO que ele recebeu. Mais estrita do que <see cref="ValidateUri"/>, que so exige um
    /// ambiente aprovado - aqui um link de continuacao que viesse apontando para o outro
    /// ambiente aprovado e recusado, porque misturar DEV e QAS na mesma leitura daria um
    /// resultado que parece inteiro e nao e.
    /// </summary>
    private Uri ValidarNoAmbiente(Uri uri)
    {
        ValidateUri(uri);
        if(uri.GetLeftPart(UriPartial.Authority)!=ambiente.Origem)throw new FormatException();
        var clients=ClientesNaQuery(uri);
        if(clients.Length==1&&Uri.UnescapeDataString(clients[0][1])!=ambiente.SapClient)throw new FormatException();
        return clients.Length==1?uri:new Uri(uri.AbsoluteUri+(uri.Query.Length==0?"?":"&")+"sap-client="+Uri.EscapeDataString(ambiente.SapClient));
    }

    /// <summary>
    /// Guarda de transporte, usada pelo handler do servico MM: a requisicao tem de ir para a rota
    /// de leitura de um ambiente APROVADO, nunca para outro host. Nao sabe qual dos aprovados e o
    /// desta instancia - por isso o adaptador ainda confere o seu em <see cref="ValidarNoAmbiente"/>.
    /// Lanca quando recusa; devolve a propria URL quando aceita.
    /// </summary>
    public static Uri ValidateUri(Uri uri)
    {
        if(!uri.IsAbsoluteUri||uri.Scheme!="https"||uri.UserInfo.Length!=0||uri.Fragment.Length!=0)throw new FormatException();
        var autoridade=uri.GetLeftPart(UriPartial.Authority);
        if(!FugaTES.Aplicacao.Configuracao.AmbientesSap.Aprovados.Any(a=>a.Origem==autoridade))throw new FormatException();
        var suffix=uri.AbsolutePath.StartsWith(Rota,StringComparison.Ordinal)?uri.AbsolutePath[Rota.Length..]:"";
        if(suffix!="PurchaseOrder"&&!Regex.IsMatch(suffix,@"^PurchaseOrder\('[0-9]{1,10}'\)/_PurchaseOrderItem$",RegexOptions.CultureInvariant))throw new FormatException();
        var clients=ClientesNaQuery(uri);
        if(clients.Length==1&&!FugaTES.Aplicacao.Configuracao.AmbientesSap.EhAprovado(autoridade,Uri.UnescapeDataString(clients[0][1])))throw new FormatException();
        return uri;
    }

    /// <summary>Os sap-client da query. Lanca se houver mais de um, ou um sem valor.</summary>
    private static string[][] ClientesNaQuery(Uri uri)
    {
        var clients=uri.Query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries).Select(p=>p.Split('=',2)).Where(p=>Uri.UnescapeDataString(p[0]).Equals("sap-client",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(clients.Length>1||(clients.Length==1&&clients[0].Length!=2))throw new FormatException();
        return clients;
    }
    private sealed class MmHttpException(int status,string code):Exception
    {public int Status{get;}=status;public string Code{get;}=code;}
}
