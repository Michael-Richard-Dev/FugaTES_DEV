using System.Net;
using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.WinForms.Transportadoras.MasterdataR3;
using FugaTES.Persistencia.Transportadoras;
using Microsoft.Extensions.Configuration;
namespace FugaTES.SingleCreate.R41.Tests;
public class CarrierMainIntegrationTests
{
 [Fact]public void Dynamic_read_selection_does_not_approve_create()
 {
  var d=Valid();d["Masterdata:Read:DynamicSapBpSelectionApproved"]="true";d["Masterdata:Read:ApprovalReference"]="SYNTHETIC_R163A_READ";
  d["Masterdata:Read:GetScopeApproved"]="true";d["Masterdata:Read:GetSignatureId"]="SIG_GET42";d["Masterdata:Read:AllowedSids:0"]="TEST_SID";
  d["FUGATES_CARRIER_SYNC_ENABLED"]="false";d["FUGATES_CARRIER_WORKER_ENABLED"]="false";
  var c=Config(d);CarrierTargetPolicy.ValidateRead(c,"0000123456","TEST_SID",Now);
  Assert.Equal("0000123456",CarrierTargetPolicy.ReadIdentity(c,"0000123456").Codigo);
  Assert.False(CarrierTargetPolicy.CanCreate(c,"0000123456","0000123456","TEST_SID",Now));
  Assert.ThrowsAny<Exception>(()=>CarrierTargetPolicy.ValidateRead(c,"0000123456","OTHER",Now));
 }
 [Theory][InlineData("CONFIRMED")][InlineData("PREPARED")][InlineData("UNKNOWN")]
 public void Journal_history_always_prevents_duplicate_create(string status)
 {
  Assert.False(CarrierTargetPolicy.HistoryAllowsCreate([new(Guid.NewGuid(),"14300001","14300001",status,Now,status=="CONFIRMED"?Now:null)]));
  Assert.True(CarrierTargetPolicy.HistoryAllowsCreate([]));
 }
    static Dictionary<string,string?> Valid()
    {
        var d=new Dictionary<string,string?>{
            ["Carrier:Environment"]="HML",["Carrier:DesktopProfileApproved"]="true",["Carrier:ApprovalReference"]="OFFLINE_PROFILE",
            ["Carrier:ManualReadOnly:Approved"]="true",["Carrier:ManualReadOnly:ApprovalReference"]="OFFLINE_READ_AUTH",["Carrier:ManualReadOnly:TimeoutSeconds"]="30",
            ["Sap:BaseUrl"]="https://vhfufds4ci.sap.fugacouros.com.br:44300/sap/opu/odata/sap/API_BUSINESS_PARTNER/",["Sap:SapClient"]="110",
            ["Guardian:BaseUrl"]="https://guardian.cloudprix.com.br/",["Carrier:ManualReadOnly:Sap:Proven"]="true",
            ["Carrier:ManualReadOnly:Sap:Origin"]="https://vhfufds4ci.sap.fugacouros.com.br:44300",["Carrier:ManualReadOnly:Sap:ApprovalReference"]="OFFLINE_INDIVIDUAL_ONLY",
            ["Carrier:Authorization:UntilUtc"]="2060-01-01T00:00:00Z",["Carrier:Authorization:AllowedBps:0"]="0000000042",["Carrier:Authorization:AllowedSids:0"]="TEST_SID"
        };
        foreach(var key in new[]{"FUGATES_INTEGRATION_WORKER_ENABLED","INC005_WORKER_ENABLED","Carrier:IntegrationWorkerEnabled","Carrier:DesktopGuardianWritesEnabled","Carrier:Authorization:IncrementalReleased"})d[key]="false";
        foreach(var key in new[]{"SapUser","SapCredentialTarget","GuardianUser","GuardianCredentialTarget","GuardianSignatureTarget","GuardianSignatureUser"})d["Carrier:Secrets:"+key]="OFFLINE_REFERENCE";
        var p="Carrier:Protocols:Guardian:";
        foreach(var pair in new Dictionary<string,string?>{
            ["Environment"]="HML",["ProfileVerified"]="true",["SignatureVerified"]="true",["ApprovalReference"]="OFFLINE_GET_CONTRACT",
            ["LookupRoute"]="/api/v0/Guardian/Transportadora/{bp}?idAssinatura=SIG_GET42",["CreateRoute"]="/api/v0/Guardian/Transportadora",["UpdateRoute"]="/api/v0/Guardian/Transportadora",
            ["ReadbackRoute"]="/api/v0/Guardian/Transportadora/{code}",["RowsPath"]="$",["CodeField"]="codigo",["NameField"]="descricao",["LegalNameField"]="razaoSocial",
            ["WriteCodeField"]="codigo",["WriteNameField"]="descricao",["WriteLegalNameField"]="razaoSocial",["ResponseCodePath"]="codigo",["AbsenceHttpStatus"]="404"})d[p+pair.Key]=pair.Value;
        return d;
    }

 static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-10-21T15:00:00Z");
 static Dictionary<string,string?> Approved()
 {
  var d=Valid();var p="Masterdata:Create:";var t=p+"Targets:0000000042:";
  foreach(var flag in new[]{"Enabled","SpecificCreateAuthorized","CallbackApproved","CallbackAuthenticationConfirmed"})d[p+flag]="true";
  d[p+"AllowedBps:0"]="0000000042";d[p+"AllowedSids:0"]="TEST_SID";
  d[p+"FromUtc"]=Now.AddMinutes(-1).ToString("O");d[p+"UntilUtc"]=Now.AddMinutes(4).ToString("O");
  d[p+"CallbackUrl"]="https://callback.example.invalid/transportadora";
  foreach(var flag in new[]{"HistoricalPostReconciled","CodeApproved","EstadoApproved","PayloadContractApproved"})d[t+flag]="true";
  d[t+"HistoricalEvidenceReference"]="SYNTHETIC_HISTORY";d[t+"ExecutionApprovalReference"]="SYNTHETIC_CREATE";
  d[t+"GuardianCode"]="CODE42";d[t+"Estado"]="1";
  var i="Carrier:ManualReadOnly:Identities:0000000042:";
  d[i+"Proven"]="true";d[i+"Code"]="CODE42";d[i+"GetSignatureId"]="SIG_GET42";d[i+"ApprovalReference"]="SYNTHETIC_TARGET";
  return d;
 }
 static IConfiguration Config(Dictionary<string,string?> d)=>new ConfigurationBuilder().AddInMemoryCollection(d).Build();
 [Fact] public void Explicit_target_can_be_authorized_without_compiled_bp()=>Assert.True(CarrierTargetPolicy.CanCreate(Config(Approved()),"0000000042","CODE42","TEST_SID",Now));
 [Fact] public void Approved_private_hml_callback_reuses_R159B_policy_without_global_http_bypass()
 {
  var d=Approved();d["Masterdata:Create:CallbackUrl"]="http://192.168.17.40/api/guardian/masterdata/transportadoras/callback";
  foreach(var flag in new[]{"Approved","PrivateTransportVerified","GuardianSourceVerified"})d["Carrier:SingleCreate:PrivateHttpCallback:"+flag]="true";
  d["Carrier:SingleCreate:PrivateHttpCallback:GuardianSourceAddress"]="192.168.17.91";d["Carrier:SingleCreate:PrivateHttpCallback:ApprovalReference"]="SYNTHETIC_PRIVATE_APPROVAL";
  Assert.True(CarrierTargetPolicy.CanCreate(Config(d),"0000000042","CODE42","TEST_SID",Now));
  d["Carrier:SingleCreate:PrivateHttpCallback:PrivateTransportVerified"]="false";Assert.False(CarrierTargetPolicy.CanCreate(Config(d),"0000000042","CODE42","TEST_SID",Now));
 }
 [Theory][InlineData("Enabled","false")][InlineData("CallbackAuthenticationConfirmed","false")][InlineData("SpecificCreateAuthorized","false")]
 [InlineData("UntilUtc","2026-10-21T14:59:00Z")][InlineData("UntilUtc","2026-10-21T15:11:00Z")][InlineData("FromUtc","2026-10-21T15:01:00Z")]
 [InlineData("CallbackUrl","http://10.0.0.1/callback")][InlineData("CallbackUrl","https://callback.invalid/?secret=x")]
 public void Missing_write_guards_block(string key,string value){var d=Approved();d["Masterdata:Create:"+key]=value;Assert.False(CarrierTargetPolicy.CanCreate(Config(d),"0000000042","CODE42","TEST_SID",Now));}
 [Theory][InlineData("HistoricalPostReconciled","false")][InlineData("HistoricalEvidenceReference","")][InlineData("ExecutionApprovalReference","")][InlineData("GuardianCode","OTHER")][InlineData("CodeApproved","false")][InlineData("EstadoApproved","false")][InlineData("PayloadContractApproved","false")]
 public void Target_specific_approval_is_required(string key,string value){var d=Approved();d["Masterdata:Create:Targets:0000000042:"+key]=value;Assert.False(CarrierTargetPolicy.CanCreate(Config(d),"0000000042","CODE42","TEST_SID",Now));}
 [Fact] public void Other_bp_code_or_sid_does_not_inherit_approval()
 {var c=Config(Approved());Assert.False(CarrierTargetPolicy.CanCreate(c,"14300001","CODE42","TEST_SID",Now));Assert.False(CarrierTargetPolicy.CanCreate(c,"0000000042","OTHER","TEST_SID",Now));Assert.False(CarrierTargetPolicy.CanCreate(c,"0000000042","CODE42","OTHER_SID",Now));}
 [Theory][InlineData("{}",false)][InlineData("{\"BusinessPartnerIsBlocked\":true}",false)][InlineData("{\"BusinessPartnerIsBlocked\":false}",true)]
 public void Sap_block_flag_is_fail_closed(string json,bool eligible){using var d=JsonDocument.Parse(json);Assert.Equal(eligible,CarrierTargetPolicy.SapUnblocked(d.RootElement));}
 sealed class Sink:HttpMessageHandler
 {public int Sends;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Sends++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));}}
 [Theory][InlineData("GET","https://guardian.cloudprix.com.br/api/v0/Guardian/Transportadora/CODE42?idAssinatura=SIG_GET42",false,true)]
 [InlineData("GET","https://guardian.cloudprix.com.br/api/v0/Guardian/Transportadora/14300001?idAssinatura=SIG_GET42",true,false)]
 [InlineData("GET","https://guardian.cloudprix.com.br/api/v0/Guardian/Transportadora/CODE42?idAssinatura=OTHER",true,false)]
 [InlineData("POST","https://guardian.cloudprix.com.br/api/v0/Guardian/Transportadora",false,false)]
 [InlineData("POST","https://guardian.cloudprix.com.br/api/v0/Guardian/Transportadora",true,true)]
 [InlineData("PUT","https://guardian.cloudprix.com.br/api/v0/Guardian/Transportadora",true,false)]
 [InlineData("POST","https://unapproved.invalid/api/v0/Guardian/Transportadora",true,false)]
 public async Task Http_guard_checks_exact_scope_and_live_gate(string method,string url,bool gate,bool allowed)
 {
  var sink=new Sink();using var http=new HttpClient(new CarrierMasterdataHttpGuard(new("https://vhfufds4ci.sap.fugacouros.com.br:44300"),new("https://guardian.cloudprix.com.br"),"0000000042","CODE42","SIG_GET42",()=>gate,sink));
  if(allowed){using var response=await http.SendAsync(new(new HttpMethod(method),url));Assert.Equal(1,sink.Sends);}
  else{await Assert.ThrowsAsync<CarrierGatewayException>(()=>http.SendAsync(new(new HttpMethod(method),url)));Assert.Equal(0,sink.Sends);}
 }
 [Theory][InlineData(15432)][InlineData(15433)][InlineData(5432)][InlineData(5433)]public void Masterdata_never_uses_acm_port(int port)=>Assert.Equal("MASTERDATA_HML_FORBIDDEN_ACM_PORT",MasterdataHmlDataSourceR147B.Validar($"Host=127.0.0.1;Port={port};Database=fugates_masterdata_hml;Username=fugates_masterdata_hml_app;Enlist=false"));
}
