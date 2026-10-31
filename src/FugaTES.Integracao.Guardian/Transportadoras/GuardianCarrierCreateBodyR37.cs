using System.Text.Json;
namespace FugaTES.Integracao.Guardian.Transportadoras;
public sealed class GuardianCarrierCreateFieldsR37
{
 public MasterdataPrivateHttpBindingR159B? PrivateHttpCallback{get;init;}
 public string? BusinessPartner{get;init;} public string? GuardianCode{get;init;} public string? FiscalIdentifier{get;init;} public int? Estado{get;init;} public int? Sequencial{get;init;} public string? Callback{get;init;} public string? EvidenceReference{get;init;}
 public bool SapIdentityConfirmed{get;init;} public bool CodeApproved{get;init;} public bool FiscalBindingApproved{get;init;} public bool EstadoBindingApproved{get;init;} public bool SequencialBindingApproved{get;init;} public bool CallbackBindingApproved{get;init;} public bool RequiredFieldContractApproved{get;init;}
 public override string ToString()=>nameof(GuardianCarrierCreateFieldsR37);
}
public sealed class GuardianCarrierCreateBodyResultR37(bool ready,string code,byte[]? transientBody=null)
{
 public bool Ready{get;}=ready;public string Code{get;}=code;public byte[]? TransientBody{get;}=transientBody;
 public override string ToString()=>Code;
}
public static class GuardianCarrierCreateBodyR37
{
 // Pure transient projection. No HTTP, journal, scheduler, credential access or write authorization.
 // Approvals must bind the exact caller-supplied values to this BP; this codec does not infer them.
 public static GuardianCarrierCreateBodyResultR37 Prepare(JsonElement sap,GuardianCarrierCreateFieldsR37? fields)
 {
  static GuardianCarrierCreateBodyResultR37 Block(string code)=>new(false,code);
  if(fields is null||!fields.SapIdentityConfirmed||!fields.CodeApproved||!fields.FiscalBindingApproved||
     !fields.EstadoBindingApproved||!fields.SequencialBindingApproved||!fields.CallbackBindingApproved||
     !fields.RequiredFieldContractApproved||string.IsNullOrWhiteSpace(fields.EvidenceReference))
   return Block("CREATE_BODY_BINDING_UNPROVEN");
  if(string.IsNullOrWhiteSpace(fields.BusinessPartner)||string.IsNullOrWhiteSpace(fields.GuardianCode)||
     string.IsNullOrWhiteSpace(fields.FiscalIdentifier)||fields.Estado is null||fields.Sequencial is null)
   return Block("CREATE_BODY_REQUIRED_FIELD_MISSING");
  if(!MasterdataCallbackUrlPolicyR159B.IsAllowed(fields.Callback,fields.PrivateHttpCallback))
   return Block("CREATE_BODY_CALLBACK_INVALID");
  if(!TryText(sap,"BusinessPartner",out var bp)||bp!=fields.BusinessPartner)
   return Block("CREATE_BODY_SAP_IDENTITY_INVALID");
  if(!TryText(sap,"BusinessPartnerName",out var name)||!TryText(sap,"BusinessPartnerFullName",out var legal)||
     name!.Length>50||legal!.Length>50)return Block("CREATE_BODY_SAP_NAME_INVALID");
  // Preserve exact SAP text and explicit Guardian code. No BP fallback, trimming, tax/UF normalization,
  // numeric defaults, tipoDocumento, or callback inference. Integer semantics require external approval.
  var body=JsonSerializer.SerializeToUtf8Bytes(new
  {
   voConfiguracao=new {urlParaRetorno=fields.Callback},
   entidadeTransportadora=new {codigo=fields.GuardianCode,descricao=name,sequencial=fields.Sequencial.Value,
      razaoSocial=legal,cnpj=fields.FiscalIdentifier,estado=fields.Estado.Value}
  });
  return new(true,"CREATE_BODY_SHAPE_PREPARED_NOT_WRITE_AUTHORIZATION",body);
 }
 static bool TryText(JsonElement element,string field,out string? value)
 {
  value=null;
  if(element.ValueKind!=JsonValueKind.Object)return false;
  var count=0;
  foreach(var property in element.EnumerateObject())
  {
   if(property.Name!=field)continue;
   count++;
   if(property.Value.ValueKind!=JsonValueKind.String)return false;
   value=property.Value.GetString();
  }
  return count==1&&!string.IsNullOrWhiteSpace(value);
 }
}
