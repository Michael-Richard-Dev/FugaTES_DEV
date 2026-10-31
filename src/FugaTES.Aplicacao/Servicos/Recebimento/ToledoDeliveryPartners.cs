using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
namespace FugaTES.Aplicacao.Servicos.Recebimento;

public enum ToledoSupplierStatus { Found, NotFound, NotSupplier, Blocked, Unavailable, InvalidResponse }
public sealed record ToledoSupplierResult(ToledoSupplierStatus Status,string? BusinessPartner=null,string? Supplier=null,
 string? SupplierName=null,string? AccountGroup=null,int? HttpStatus=null,string? ResponseSha256=null);
public interface IToledoSupplierLookup
{ Task<ToledoSupplierResult> FindAsync(string code,CancellationToken ct); }
public sealed class ToledoDeliveryPartnerException(string code,int status,int? index=null,string? bp=null):Exception(code)
{ public string Code{get;}=code;public int HttpStatus{get;}=status;public int? Index{get;}=index;public string? BusinessPartner{get;}=bp; }
public sealed record ToledoDeliveryPartnerItem(int Indice,string OriginalCode,string Code,string Name,string Source,
 string? SapBusinessPartner=null,string? SapSupplier=null,string? AccountGroup=null,int? HttpStatus=null,string? ResponseSha256=null);
public sealed record ToledoDeliveryPartnerProof(string Version,string OriginalSha256,DateTimeOffset CheckedAt,
 IReadOnlyList<ToledoDeliveryPartnerItem> Items);

// Frozen admission evidence. Never substitutes the original Guardian document/fingerprint.
public static class ToledoDeliveryPartners
{
 public const string Version="TOLEDO_DELIVERY_BP_V1";
 public const string Event="toledo_bp_entrega_validado";
 public static string Scalar(JsonNode? node)=>node is null?"":node.GetValueKind()==JsonValueKind.String?node.GetValue<string>():
  node.GetValueKind()==JsonValueKind.Number?node.ToJsonString():throw new ToledoDeliveryPartnerException("TOLEDO_DELIVERY_BP_INVALID",400);
 static JsonNode? Field(JsonObject node,string name)=>node.FirstOrDefault(p=>p.Key.Equals(name,StringComparison.OrdinalIgnoreCase)).Value;
 static void Set(JsonObject node,string name,string value)
 {var key=node.Select(p=>p.Key).SingleOrDefault(k=>k.Equals(name,StringComparison.OrdinalIgnoreCase));if(key is not null)node.Remove(key);node[name]=value;}
 public static string Numeric(string value)
 {if(value.Length is <1 or >10||!value.All(char.IsAsciiDigit))throw new ToledoDeliveryPartnerException("TOLEDO_DELIVERY_BP_INVALID",400);return value.TrimStart('0');}
 static bool Default(string value)=>string.IsNullOrWhiteSpace(value)||Numeric(value).Length==0;
 static IReadOnlyList<JsonObject> Weights(string original)
 {var root=JsonNode.Parse(original,new(),new(){MaxDepth=32}) as JsonObject??throw new JsonException();
  var items=Field(root,"to_Pesagem") as JsonArray??throw new JsonException();if(items.Count is <1 or >1000)throw new JsonException();
  return items.Select(x=>x as JsonObject??throw new JsonException()).ToArray();}
 public static bool RequiresLookup(string original)=>Weights(original).Any(w=>!Default(Scalar(Field(w,"IdParceiroEntrega"))));
 public static async Task<ToledoDeliveryPartnerProof> ValidateAsync(string original,IToledoSupplierLookup? lookup,CancellationToken ct)
 {
  var items=new List<ToledoDeliveryPartnerItem>();var cache=new Dictionary<string,ToledoSupplierResult>();
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(25));
  foreach(var w in Weights(original))
  {
   if(!int.TryParse(Scalar(Field(w,"Indice")),out var index)||index<0)throw new ToledoDeliveryPartnerException("TOLEDO_DELIVERY_BP_INVALID",400);
   var code=Scalar(Field(w,"IdParceiroEntrega"));
   if(Default(code))
   {
    var owner=Scalar(Field(w,"IdParceiro"));if(Numeric(owner).Length==0)throw new ToledoDeliveryPartnerException("TOLEDO_DELIVERY_BP_INVALID",400,index);
    items.Add(new(index,code,owner,Scalar(Field(w,"ParceiroNome")),"GUARDIAN_DEFAULT"));continue;
   }
   var key=Numeric(code);
   if(lookup is null)throw new ToledoDeliveryPartnerException("TOLEDO_DELIVERY_BP_VALIDATION_UNAVAILABLE",503,index,code);
   if(!cache.TryGetValue(key,out var result))
   {
    try{result=await lookup.FindAsync(code,deadline.Token);}
    catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
    catch{result=new(ToledoSupplierStatus.Unavailable);}
    cache.Add(key,result);
   }
   if(result.Status!=ToledoSupplierStatus.Found)throw new ToledoDeliveryPartnerException(result.Status switch{
    ToledoSupplierStatus.NotFound=>"TOLEDO_DELIVERY_BP_NOT_FOUND",ToledoSupplierStatus.NotSupplier=>"TOLEDO_DELIVERY_BP_NOT_SUPPLIER",
    ToledoSupplierStatus.Blocked=>"TOLEDO_DELIVERY_BP_BLOCKED",ToledoSupplierStatus.InvalidResponse=>"TOLEDO_DELIVERY_BP_INVALID_SAP_RESPONSE",
    _=>"TOLEDO_DELIVERY_BP_VALIDATION_UNAVAILABLE"},result.Status is ToledoSupplierStatus.NotFound or ToledoSupplierStatus.NotSupplier or ToledoSupplierStatus.Blocked?422:result.Status==ToledoSupplierStatus.InvalidResponse?502:503,index,code);
   var item=new ToledoDeliveryPartnerItem(index,code,code,result.SupplierName??"","SAP_SUPPLIER_GET",result.BusinessPartner,result.Supplier,result.AccountGroup,result.HttpStatus,result.ResponseSha256);
   if(!SupplierValid(item))throw new ToledoDeliveryPartnerException("TOLEDO_DELIVERY_BP_INVALID_SAP_RESPONSE",502,index,code);
   items.Add(item);
  }
  return new(Version,RegistrarPesagemToledoCasoDeUso.Fingerprint(original),DateTimeOffset.UtcNow,items);
 }
 static bool SupplierValid(ToledoDeliveryPartnerItem item)
 {
  try{return item.HttpStatus==200&&Numeric(item.Code).Length>0&&Numeric(item.SapBusinessPartner??"")==Numeric(item.Code)&&Numeric(item.SapSupplier??"")==Numeric(item.Code)&&
   !string.IsNullOrWhiteSpace(item.Name)&&item.Name.Length<=80&&!item.Name.Any(char.IsControl)&&
   !string.IsNullOrWhiteSpace(item.AccountGroup)&&item.AccountGroup.Length<=4&&item.ResponseSha256 is{Length:64}&&item.ResponseSha256.All(char.IsAsciiHexDigit);}catch{return false;}
 }
 public static string Apply(string original,ToledoDeliveryPartnerProof? proof)
 {
  if(proof is null||proof.Version!=Version||proof.CheckedAt==default||proof.OriginalSha256!=RegistrarPesagemToledoCasoDeUso.Fingerprint(original)||proof.Items is null)
   throw new ToledoDeliveryPartnerException("TOLEDO_DELIVERY_BP_PROOF_REQUIRED",422);
  var root=JsonNode.Parse(original) as JsonObject??throw new JsonException();var weights=Field(root,"to_Pesagem") as JsonArray??throw new JsonException();
  if(proof.Items.Count!=weights.Count||proof.Items.Any(p=>p is null)||proof.Items.Select(p=>p.Indice).Distinct().Count()!=weights.Count)throw new ToledoDeliveryPartnerException("TOLEDO_DELIVERY_BP_PROOF_INVALID",422);
  foreach(var node in weights)
  {
   var w=(JsonObject)node!;var index=int.Parse(Scalar(Field(w,"Indice")),System.Globalization.CultureInfo.InvariantCulture);
   var p=proof.Items.SingleOrDefault(p=>p.Indice==index);var originalCode=Scalar(Field(w,"IdParceiroEntrega"));
   if(p is null||p.OriginalCode!=originalCode)throw new ToledoDeliveryPartnerException("TOLEDO_DELIVERY_BP_PROOF_INVALID",422);
   var valid=Default(originalCode)?p.Source=="GUARDIAN_DEFAULT"&&p.Code==Scalar(Field(w,"IdParceiro"))&&p.Name==Scalar(Field(w,"ParceiroNome"))&&Numeric(p.Code).Length>0:
    p.Source=="SAP_SUPPLIER_GET"&&p.Code==originalCode&&SupplierValid(p);
   if(!valid)throw new ToledoDeliveryPartnerException("TOLEDO_DELIVERY_BP_PROOF_INVALID",422);
   Set(w,"IdParceiroEntrega",p.Code);Set(w,"ParceiroEntregaNome",p.Name);
  }
  return root.ToJsonString();
 }
}
