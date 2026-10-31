using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Aplicacao.Transportadoras.SingleCreateR31;
using FugaTES.Aplicacao.Transportadoras.SnapshotsR24;
using FugaTES.Integracao.Guardian;
using FugaTES.Integracao.Guardian.Transportadoras;
using FugaTES.Integracao.SAP.BusinessPartner;
namespace FugaTES.WinForms.Transportadoras.MasterdataR3;

// Exact caller-approved target. Preparation never enables runtime writing.
public sealed record SingleCarrierDecisionR41(string BusinessPartner,string GuardianCode,int Estado,string Callback,
 bool CodeApproved=false,bool EstadoApproved=false,bool CallbackApproved=false,bool ContractApproved=false,
 MasterdataPrivateHttpBindingR159B? PrivateHttpCallback=null)
{
 internal string? LegacyApprovedBp {get;init;}
 public static SingleCarrierDecisionR41 Approved20000014(string callback,bool callbackApproved=false)=>new("20000014","20000014",1,callback,true,true,callbackApproved,true){LegacyApprovedBp="20000014"};
 public override string ToString()=>nameof(SingleCarrierDecisionR41);
}
// Transient data only. Do not serialize this holder into a snapshot, journal or diagnostic log.
public sealed class PreparedSingleCreateR41 : IDisposable
{
 readonly string? guardianCode;readonly CarrierPayload? payload;readonly byte[]? body;readonly DateTimeOffset? from,to;readonly DateTimeOffset reference;
 internal PreparedSingleCreateR41(string code){Code=code;}
 internal PreparedSingleCreateR41(CarrierPayload payload,string guardianCode,byte[] body,DateTimeOffset? from,DateTimeOffset? to,DateTimeOffset reference)
 {this.payload=payload;this.guardianCode=guardianCode;this.body=body;this.from=from;this.to=to;this.reference=reference;Code="R41_BODY_PREPARED_NOT_RUNTIME_AUTHORIZATION";}
 bool disposed;
 public bool Ready=>!disposed&&body is not null;
 public string Code{get;}
 bool Matches(CarrierPayload p)=>Ready&&p==payload;
 public byte[] CopyBody(){if(!Ready)throw new CarrierGatewayException("R41_BODY_NOT_READY");return body!.ToArray();}
 public SingleCreateRequestR31 CreateRequest(Guid attempt,CarrierManualIdentity identity,SingleCreateApprovalR31 approval)
 {
  if(!Ready||identity.Codigo!=guardianCode||approval.BusinessPartner!=payload!.BusinessPartner||approval.GuardianCode!=guardianCode)
   throw new CarrierGatewayException("R41_FUNCTIONAL_SCOPE_INVALID");
  return new(attempt,payload!.BusinessPartner,identity,payload,from,to,reference,approval);
 }
 public GuardianSingleCreateUseCaseR31 CreateUseCase(IGuardianCarrierManualReadOnly reader,IGuardianCarrierGateway writer,
  ISingleCreateJournalR31? journal=null,Func<bool>? gate=null,TimeProvider? clock=null)=>
  new(reader,writer,journal,gate,Matches,clock:clock,wirePayload:p=>Matches(p)?CopyBody():throw new CarrierGatewayException("R41_BODY_IDENTITY_INVALID"));
 // Complete unitary composition; preserves existing HML/profile/signature checks and defaults off.
 public GuardianSingleCreateUseCaseR31 CreateBoundUseCase(IGuardianCarrierManualReadOnly reader,HttpClient http,
  GuardianHttpOptions options,GuardianTokenClient tokens,GuardianCarrierHmlContract proven,
  ISingleCreateJournalR31? journal=null,Func<bool>? gate=null,TimeProvider? clock=null)
 {
  bool Allowed(CarrierWriteAttempt a){try{return gate?.Invoke()==true&&a.Action=="CREATE"&&a.BusinessPartner==payload?.BusinessPartner&&a.GuardianCode==guardianCode&&Matches(a.Payload);}catch{return false;}}
  return CreateUseCase(reader,new GuardianCarrierSyncAdapter(http,options,tokens,BindSingleCreateContract(proven),Allowed),journal,gate,clock);
 }
 public object EncodeWrite(CarrierWriteAttempt attempt)
 {
  if(attempt.Action!="CREATE"||attempt.BusinessPartner!=payload?.BusinessPartner||attempt.GuardianCode!=guardianCode||!Matches(attempt.Payload))
   throw new CarrierGatewayException("R41_FUNCTIONAL_SCOPE_INVALID");
  using var json=JsonDocument.Parse(body!);return json.RootElement.Clone();
 }
 public GuardianCarrierHmlContract BindSingleCreateContract(GuardianCarrierHmlContract proven)=>proven with
 {CreateRoute="/api/v0/Guardian/Transportadora",EncodeWrite=EncodeWrite,DecodeWriteCode=DecodeResponseCode};
 public static string? DecodeResponseCode(JsonElement json)=>GuardianSingleCreatePreparationR41.Text(json,"codigo");
 public void Dispose(){disposed=true;if(body is not null)CryptographicOperations.ZeroMemory(body);}
 public override string ToString()=>Code;
}
// Reuses the existing SAP master reader, temporal parser and R37 codec. No timer, worker or profile I/O.
public sealed class GuardianSingleCreatePreparationR41(ISapBpMasterReaderR20B sap,TimeProvider? clock=null)
{
 public async Task<PreparedSingleCreateR41> PrepareAsync(SingleCarrierDecisionR41 decision,CancellationToken ct=default)
 {
  static PreparedSingleCreateR41 Block(string code)=>new(code);
  try{CarrierPersistencePolicy.ValidateBp(decision.BusinessPartner);}catch{return Block("R41_FUNCTIONAL_IDENTITY_NOT_APPROVED");}
  if(string.IsNullOrWhiteSpace(decision.GuardianCode)||decision.LegacyApprovedBp is {} legacy&&(decision.BusinessPartner!=legacy||decision.GuardianCode!=legacy)||decision.Estado!=1||!decision.CodeApproved||!decision.EstadoApproved)
   return Block("R41_FUNCTIONAL_IDENTITY_NOT_APPROVED");
  if(!decision.CallbackApproved||!decision.ContractApproved)return Block("R41_BODY_CONTRACT_NOT_APPROVED");
  if(ct.IsCancellationRequested)return Block("R41_CANCELLED");
  var reference=(clock??TimeProvider.System).GetUtcNow();
  try
  {
   var data=await sap.ReadAsync(decision.BusinessPartner,ct).ConfigureAwait(false);
   if(ct.IsCancellationRequested)return Block("R41_CANCELLED");
   if(data is null||!data.Complete||data.BusinessPartner!=decision.BusinessPartner||DuplicatesOrContinuation(data.RawSapData))return Block("R41_SAP_SOURCE_INCOMPLETE");
   var raw=data.RawSapData;
   if(Text(raw,"BusinessPartner")!=decision.BusinessPartner)return Block("R41_SAP_IDENTITY_MISMATCH");
   var roles=Rows(raw,"to_BusinessPartnerRole");var taxes=Rows(raw,"to_BusinessPartnerTax");
   if(roles is null||taxes is null)return Block("R41_SAP_NAVIGATION_INCOMPLETE");
   var crm=roles.Where(r=>Text(r,"BusinessPartnerRole")=="CRM010").ToArray();
   if(crm.Length!=1||Text(crm[0],"BusinessPartner")!=decision.BusinessPartner)return Block("R41_CRM010_UNVERIFIED");
   var from=CarrierManualODataDateParser.Parse(Text(crm[0],"ValidFrom"));var to=CarrierManualODataDateParser.Parse(Text(crm[0],"ValidTo"));
   if(new Crm010BusinessDatePolicyR25T().Evaluate(from,to,reference).Status!=AvaliacaoCrm010R24.EVALUATED_VALID)return Block("R41_CRM010_NOT_VALID");
   if(taxes.Any(t=>Text(t,"BusinessPartner")!=decision.BusinessPartner))return Block("R41_FISCAL_IDENTITY_MISMATCH");
   var br1=taxes.Where(t=>Text(t,"BPTaxType")=="BR1").ToArray();
   if(br1.Length!=1)return Block("R41_BR1_MISSING_OR_AMBIGUOUS");
   var shortValue=Text(br1[0],"BPTaxNumber");var longValue=Text(br1[0],"BPTaxLongNumber");
   if(!NullableText(br1[0],"BPTaxNumber")||!NullableText(br1[0],"BPTaxLongNumber"))return Block("R41_BR1_INVALID");
   if(!string.IsNullOrWhiteSpace(shortValue)&&!string.IsNullOrWhiteSpace(longValue)&&shortValue!=longValue)return Block("R41_BR1_CONFLICT");
   var fiscal=string.IsNullOrWhiteSpace(longValue)?shortValue:longValue;
   if(!CnpjValid(fiscal))return Block("R41_BR1_INVALID");
   var mapped=GuardianCarrierCreateBodyR37.Prepare(raw,new GuardianCarrierCreateFieldsR37
   {
    BusinessPartner=decision.BusinessPartner,GuardianCode=decision.GuardianCode,FiscalIdentifier=fiscal,
    Estado=decision.Estado,Sequencial=0,Callback=decision.Callback,PrivateHttpCallback=decision.PrivateHttpCallback,EvidenceReference="R41-FUNCTIONAL-IDENTITY-APPROVAL",
    SapIdentityConfirmed=true,CodeApproved=decision.CodeApproved,FiscalBindingApproved=true,EstadoBindingApproved=decision.EstadoApproved,
    SequencialBindingApproved=true,CallbackBindingApproved=decision.CallbackApproved,RequiredFieldContractApproved=decision.ContractApproved
   });
   if(!mapped.Ready||mapped.TransientBody is null)return Block(mapped.Code);
   return new(new(decision.BusinessPartner,Text(raw,"BusinessPartnerName")!,Text(raw,"BusinessPartnerFullName")!),decision.GuardianCode,mapped.TransientBody,from,to,reference);
  }
  catch(OperationCanceledException) when(ct.IsCancellationRequested){return Block("R41_CANCELLED");}
  catch{return Block("R41_SAP_PREPARATION_FAILED");}
 }
 internal static string? Text(JsonElement e,string field)
 {
  if(e.ValueKind!=JsonValueKind.Object)return null;
  var matches=e.EnumerateObject().Where(p=>p.Name==field).ToArray();
  return matches.Length==1&&matches[0].Value.ValueKind==JsonValueKind.String?matches[0].Value.GetString():null;
 }
 static bool NullableText(JsonElement e,string field)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(field,out var v)&&v.ValueKind is JsonValueKind.String or JsonValueKind.Null;
 static JsonElement[]? Rows(JsonElement e,string field)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(field,out var n)&&n.ValueKind==JsonValueKind.Object&&n.TryGetProperty("results",out var r)&&r.ValueKind==JsonValueKind.Array?r.EnumerateArray().ToArray():null;
 internal static bool DuplicatesOrContinuation(JsonElement e)
 {
  if(e.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var p in e.EnumerateObject())if(p.Name=="__next"||!names.Add(p.Name)||DuplicatesOrContinuation(p.Value))return true;}
  if(e.ValueKind==JsonValueKind.Array)foreach(var i in e.EnumerateArray())if(DuplicatesOrContinuation(i))return true;
  return e.ValueKind is JsonValueKind.Undefined;
 }
 internal static bool CnpjValid(string? raw)
 {
  if(raw is null)return false;
  string canonical;
  if(Regex.IsMatch(raw,"\\A[0-9A-Z]{12}[0-9]{2}\\z"))canonical=raw;
  else if(Regex.IsMatch(raw,"\\A[0-9A-Z]{2}\\.[0-9A-Z]{3}\\.[0-9A-Z]{3}/[0-9A-Z]{4}-[0-9]{2}\\z"))canonical=raw.Replace(".","").Replace("/","").Replace("-","");
  else return false;
  if(Regex.IsMatch(canonical,"\\A([0-9])\\1{13}\\z"))return false;
  int[] first=[5,4,3,2,9,8,7,6,5,4,3,2],second=[6,5,4,3,2,9,8,7,6,5,4,3,2];
  int sum=0;for(int i=0;i<12;i++)sum+=(canonical[i]-48)*first[i];int rem=sum%11;int d1=rem<2?0:11-rem;
  if(d1!=canonical[12]-48)return false;
  sum=0;for(int i=0;i<13;i++)sum+=(canonical[i]-48)*second[i];rem=sum%11;
  return (rem<2?0:11-rem)==canonical[13]-48;
 }
}
