using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Servicos.Recebimento;
namespace FugaTES.Integracao.SAP.Http;

// Read-only OData lookup. A valid empty filtered collection proves absence; HTTP faults never do.
public sealed class ToledoSupplierSapLookup(SapODataHttpExecutor sap):IToledoSupplierLookup
{
 public async Task<ToledoSupplierResult> FindAsync(string code,CancellationToken ct)
 {
  var normalized=ToledoDeliveryPartners.Numeric(code);if(normalized.Length==0)return new(ToledoSupplierStatus.InvalidResponse);
  // Numeric BP keys can be rendered with or without SAP's ten-position leading zeros.
  // The filter is closed over validated ASCII digits; there is no caller-controlled URI or OData expression.
  var padded=normalized.PadLeft(10,'0');
  var filter=Uri.EscapeDataString($"BusinessPartner eq '{normalized}' or BusinessPartner eq '{padded}'");
  var uri=sap.ConstruirUri("/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner",$"$filter={filter}&$expand=to_Supplier&$format=json&$top=2");
  try
  {
   var response=await sap.ObterJsonLimitadoAsync(uri,ct);
   if(response.Status!=HttpStatusCode.OK)return new(ToledoSupplierStatus.Unavailable,HttpStatus:(int)response.Status);
   using var document=JsonDocument.Parse(response.Body,new(){MaxDepth=32});var root=document.RootElement;
   if(Duplicate(root)||!root.TryGetProperty("d",out var d)||!d.TryGetProperty("results",out var rows)||rows.ValueKind!=JsonValueKind.Array||
    d.TryGetProperty("__next",out _)||rows.GetArrayLength()>1)return new(ToledoSupplierStatus.InvalidResponse,HttpStatus:200);
   if(rows.GetArrayLength()==0)return new(ToledoSupplierStatus.NotFound,HttpStatus:200);
   var bp=rows[0];string Text(JsonElement node,string field)=>node.TryGetProperty(field,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString()!:"";
   bool Blocked(JsonElement node,string field)
   {if(!node.TryGetProperty(field,out var p))return false;if(p.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new JsonException();return p.GetBoolean();}
   var identity=Text(bp,"BusinessPartner");
   if(ToledoDeliveryPartners.Numeric(identity)!=normalized||string.IsNullOrWhiteSpace(Text(bp,"BusinessPartnerFullName"))||Text(bp,"BusinessPartnerCategory") is not("1" or "2"))return new(ToledoSupplierStatus.InvalidResponse,HttpStatus:200);
   if(Blocked(bp,"BusinessPartnerIsBlocked"))return new(ToledoSupplierStatus.Blocked,HttpStatus:200);
   if(!bp.TryGetProperty("to_Supplier",out var supplier))return new(ToledoSupplierStatus.InvalidResponse,HttpStatus:200);
   if(supplier.ValueKind==JsonValueKind.Null)return new(ToledoSupplierStatus.NotSupplier,HttpStatus:200);
   if(supplier.ValueKind!=JsonValueKind.Object||supplier.TryGetProperty("__deferred",out _))return new(ToledoSupplierStatus.InvalidResponse,HttpStatus:200);
   var supplierCode=Text(supplier,"Supplier");var name=Text(supplier,"SupplierName");var group=Text(supplier,"SupplierAccountGroup");
   if(ToledoDeliveryPartners.Numeric(supplierCode)!=normalized||string.IsNullOrWhiteSpace(name)||name.Length>80||name.Any(char.IsControl)||string.IsNullOrWhiteSpace(group)||group.Length>4)return new(ToledoSupplierStatus.InvalidResponse,HttpStatus:200);
   if(Blocked(supplier,"SupplierIsBlockedForPosting"))return new(ToledoSupplierStatus.Blocked,HttpStatus:200);
   return new(ToledoSupplierStatus.Found,identity,supplierCode,name,group,200,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(response.Body))));
  }
  catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
  catch(HttpRequestException){return new(ToledoSupplierStatus.Unavailable);}
  catch(OperationCanceledException){return new(ToledoSupplierStatus.Unavailable);}
  catch{return new(ToledoSupplierStatus.InvalidResponse);}
 }
 static bool Duplicate(JsonElement node)
 {if(node.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var p in node.EnumerateObject())if(!names.Add(p.Name)||Duplicate(p.Value))return true;}
  else if(node.ValueKind==JsonValueKind.Array)return node.EnumerateArray().Any(Duplicate);return false;}
}
