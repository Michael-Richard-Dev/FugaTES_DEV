using System.Net;
using FugaTES.Integracao.Guardian;
using FugaTES.Aplicacao.Contratos.Recebimento;
using System.Text.Json;
using System.Security.Cryptography;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Aplicacao.Transportadoras.SingleCreateR31;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.WinForms.Transportadoras.MasterdataR3;
namespace FugaTES.SingleCreate.R41.Tests;
public class SingleCreateR41Tests
{
 static readonly DateTimeOffset Now=new(2026,10,12,15,0,0,TimeSpan.Zero);
 sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>Now;}
 sealed class Sap:ISapBpMasterReaderR20B
 {
  public int Calls;public string Bp="20000014",TaxBp="20000014";public bool MissingBr1,InvalidFiscal,Incomplete;
  public Task<SapBpReadResultR20B> ReadAsync(string bp,CancellationToken ct){Calls++;var raw=JsonSerializer.SerializeToElement(new{BusinessPartner=Bp,BusinessPartnerName="SYNTHETIC_NAME",BusinessPartnerFullName="SYNTHETIC_LEGAL",Region="NOT_GUARDIAN_ESTADO",to_BusinessPartnerRole=new{results=new[]{new{BusinessPartner=Bp,BusinessPartnerRole="CRM010",ValidFrom="/Date(1609459200000)/",ValidTo="/Date(1893456000000)/"}}},to_BusinessPartnerTax=new{results=MissingBr1?Array.Empty<object>():new object[]{new{BusinessPartner=TaxBp,BPTaxType="BR1",BPTaxNumber=InvalidFiscal?"INVALID_SYNTHETIC":"12ABC34501DE35",BPTaxLongNumber=InvalidFiscal?"INVALID_SYNTHETIC":"12ABC34501DE35"}}}});return Task.FromResult(new SapBpReadResultR20B(bp,raw,!Incomplete));}
 }
 sealed class Gateway:IGuardianCarrierManualReadOnly,IGuardianCarrierGateway
 {
  public int Reads,Writes;public int? Status=200;public bool WrongCode,Timeout,Exists;
  public Task<CarrierManualGuardian> ConsultarCodigoAsync(CarrierManualIdentity i,CancellationToken ct){Reads++;return Task.FromResult(new CarrierManualGuardian(Exists?CarrierLookupState.Unique:CarrierLookupState.Absent,null,new Dictionary<string,string?>(),!Exists));}
  public Task<CarrierWriteResult> GravarAsync(CarrierWriteAttempt a,CancellationToken ct){Writes++;if(Timeout)throw new TimeoutException("SYNTHETIC_SECRET_DO_NOT_LOG");return Task.FromResult(new CarrierWriteResult(true,false,WrongCode?"OTHER_CODE":a.GuardianCode){HttpStatusCode=Status});}
  public Task<CarrierLookup> ConsultarAsync(string bp,CancellationToken ct)=>throw new Exception("UNSCOPED_NOT_ALLOWED");
  public Task<CarrierLookup> LerConfirmacaoAsync(string code,CancellationToken ct)=>throw new Exception("NO_POST_CREATE_GET_REQUIRED");
 }
 sealed class Journal:ISingleCreateJournalR31
 {
  public int Prepared,Confirmed;public bool Pending,ThrowCommit;public string? Digest;
  public Task<bool> TryPrepareAsync(Guid id,string bp,string code,string hash,CancellationToken ct){if(Pending)return Task.FromResult(false);Prepared++;Pending=true;Digest=hash;return Task.FromResult(true);}
  public Task ConfirmAsync(Guid id,CancellationToken ct){if(ThrowCommit)throw new Exception("SYNTHETIC_PRIVATE");Confirmed++;Pending=false;return Task.CompletedTask;}
 }
 static SingleCarrierDecisionR41 Decision()=>SingleCarrierDecisionR41.Approved20000014("https://callback.example.invalid/fixed",true);
 static SingleCreateApprovalR31 Approval()=>new("20000014","20000014","SYNTHETIC_SCOPE","SYNTHETIC_EXECUTION_APPROVAL",Now.AddHours(1),true,true,true,true,true,true,true);
 static CarrierManualIdentity Identity()=>new("20000014","SYNTHETIC_SCOPE",true,"SYNTHETIC_CODE_APPROVAL");
 static Task<PreparedSingleCreateR41> Prepare(Sap? sap=null,SingleCarrierDecisionR41? d=null)=>new GuardianSingleCreatePreparationR41(sap??new(),new Clock()).PrepareAsync(d??Decision());
 [Theory][InlineData("14100050")][InlineData("14300002")]
 public async Task APPROVED_DYNAMIC_BP_USES_EXACT_SAP_AND_WIRE_IDENTITY(string bp)
 {
  using var p=await Prepare(new(){Bp=bp,TaxBp=bp},new(bp,bp,1,"https://callback.example.invalid/fixed",true,true,true,true));
  Assert.True(p.Ready,p.Code);
  var identity=new CarrierManualIdentity(bp,"SYNTHETIC_SCOPE",true,"SYNTHETIC_APPROVAL");
  var approval=Approval() with {BusinessPartner=bp,GuardianCode=bp};
  var req=p.CreateRequest(Guid.NewGuid(),identity,approval);
  Assert.Equal(bp,req.BusinessPartner);
  using var wire=JsonDocument.Parse(p.CopyBody());Assert.Equal(bp,wire.RootElement.GetProperty("entidadeTransportadora").GetProperty("codigo").GetString());
  Assert.Throws<CarrierGatewayException>(()=>p.CreateRequest(Guid.NewGuid(),Identity(),Approval()));
 }
 [Fact]public async Task CODE_UNAPPROVED_BLOCKS_WRITE(){var sap=new Sap();using var p=await Prepare(sap,Decision() with{CodeApproved=false});Assert.False(p.Ready);Assert.Equal(0,sap.Calls);}
 [Fact]public async Task ESTADO_UNAPPROVED_BLOCKS_WRITE(){var sap=new Sap();using var p=await Prepare(sap,Decision() with{EstadoApproved=false});Assert.False(p.Ready);Assert.Equal(0,sap.Calls);}
 [Fact]public async Task APPROVALS_NOT_TRANSFERRED(){var sap=new Sap();using var p=await Prepare(sap,Decision() with{BusinessPartner="14100050"});Assert.False(p.Ready);Assert.Equal(0,sap.Calls);}
 [Fact]public async Task BR1_MISSING_BLOCKS_WRITE(){using var p=await Prepare(new(){MissingBr1=true});Assert.False(p.Ready);}
 [Fact]public async Task INVALID_BR1_BLOCKS_WRITE(){using var p=await Prepare(new(){InvalidFiscal=true});Assert.False(p.Ready);}
 [Fact]public async Task SAP_BP_MISMATCH_BLOCKS_WRITE(){using var p=await Prepare(new(){Bp="OTHER_BP"});Assert.False(p.Ready);}
 [Fact]public async Task TAX_BP_MISMATCH_BLOCKS_WRITE(){using var p=await Prepare(new(){TaxBp="OTHER_BP"});Assert.False(p.Ready);}
 [Fact]public async Task PARTIAL_SAP_BLOCKED(){using var p=await Prepare(new(){Incomplete=true});Assert.False(p.Ready);}
 [Fact]public async Task NESTED_PAYLOAD_VALID(){using var p=await Prepare();Assert.True(p.Ready);using var j=JsonDocument.Parse(p.CopyBody());Assert.Equal(2,j.RootElement.EnumerateObject().Count());var t=j.RootElement.GetProperty("entidadeTransportadora");Assert.Equal("20000014",t.GetProperty("codigo").GetString());Assert.Equal("SYNTHETIC_NAME",t.GetProperty("descricao").GetString());Assert.Equal("SYNTHETIC_LEGAL",t.GetProperty("razaoSocial").GetString());Assert.Equal("12ABC34501DE35",t.GetProperty("cnpj").GetString());}
 [Fact]public async Task SEQUENCIAL_ALWAYS_ZERO(){using var p=await Prepare();using var j=JsonDocument.Parse(p.CopyBody());Assert.Equal(0,j.RootElement.GetProperty("entidadeTransportadora").GetProperty("sequencial").GetInt32());}
 [Fact]public async Task ESTADO_SEPARATE_FROM_REGION(){using var p=await Prepare();using var j=JsonDocument.Parse(p.CopyBody());Assert.Equal(1,j.RootElement.GetProperty("entidadeTransportadora").GetProperty("estado").GetInt32());}
 [Fact]public async Task OMITTED_FIELDS_NOT_NULL(){using var p=await Prepare();using var j=JsonDocument.Parse(p.CopyBody());var t=j.RootElement.GetProperty("entidadeTransportadora");Assert.Equal(6,t.EnumerateObject().Count());Assert.False(t.TryGetProperty("tipoDocumento",out _));Assert.All(t.EnumerateObject(),x=>Assert.NotEqual(JsonValueKind.Null,x.Value.ValueKind));}
 [Fact]public async Task CALLBACK_NOT_APPROVED_BLOCKS(){using var p=await Prepare(d:Decision() with{CallbackApproved=false});Assert.False(p.Ready);}
 [Fact]public async Task HTTP200_MATCH_CONFIRMS_CREATE(){using var p=await Prepare();var g=new Gateway();var journal=new Journal();var result=await p.CreateUseCase(g,g,journal,()=>true,new Clock()).ExecuteAsync(p.CreateRequest(Guid.NewGuid(),Identity(),Approval()));Assert.Equal(SingleCreateStateR31.CONFIRMED,result.State);Assert.Equal(1,g.Reads);Assert.Equal(1,g.Writes);Assert.Equal(1,journal.Confirmed);Assert.Equal(Convert.ToHexString(SHA256.HashData(p.CopyBody())).ToLowerInvariant(),journal.Digest);}
 [Fact]public async Task HTTP200_CODE_MISMATCH_BLOCKS_CONFIRMATION(){using var p=await Prepare();var g=new Gateway{WrongCode=true};var journal=new Journal();var result=await p.CreateUseCase(g,g,journal,()=>true,new Clock()).ExecuteAsync(p.CreateRequest(Guid.NewGuid(),Identity(),Approval()));Assert.Equal(SingleCreateStateR31.INDETERMINATE,result.State);Assert.True(journal.Pending);Assert.Equal(0,journal.Confirmed);}
 [Theory][InlineData(201)][InlineData(202)][InlineData(null)]public async Task NON200_CANNOT_CONFIRM(int? status){using var p=await Prepare();var g=new Gateway{Status=status};var journal=new Journal();var result=await p.CreateUseCase(g,g,journal,()=>true,new Clock()).ExecuteAsync(p.CreateRequest(Guid.NewGuid(),Identity(),Approval()));Assert.Equal(SingleCreateStateR31.INDETERMINATE,result.State);Assert.Equal(0,journal.Confirmed);}
 [Fact]public async Task TIMEOUT_INDETERMINATE_NO_AUTO_RETRY_AFTER_DISPATCH(){using var p=await Prepare();var g=new Gateway{Timeout=true};var journal=new Journal();var use=p.CreateUseCase(g,g,journal,()=>true,new Clock());var r=p.CreateRequest(Guid.NewGuid(),Identity(),Approval());Assert.Equal(SingleCreateStateR31.INDETERMINATE,(await use.ExecuteAsync(r)).State);Assert.Equal(SingleCreateStateR31.BLOCKED,(await use.ExecuteAsync(r with{AttemptId=Guid.NewGuid()})).State);Assert.Equal(1,g.Writes);Assert.True(journal.Pending);}
 [Fact]public async Task JOURNAL_NOT_BYPASSED(){using var p=await Prepare();var g=new Gateway();var result=await p.CreateUseCase(g,g,gate:()=>true,clock:new Clock()).ExecuteAsync(p.CreateRequest(Guid.NewGuid(),Identity(),Approval()));Assert.Equal("POSTGRESQL_RUNTIME_NOT_ATTESTED",result.Code);Assert.Equal(0,g.Writes);}
 [Fact]public async Task COMMIT_UNCERTAIN_REMAINS_INDETERMINATE(){using var p=await Prepare();var g=new Gateway();var journal=new Journal{ThrowCommit=true};var result=await p.CreateUseCase(g,g,journal,()=>true,new Clock()).ExecuteAsync(p.CreateRequest(Guid.NewGuid(),Identity(),Approval()));Assert.Equal(SingleCreateStateR31.INDETERMINATE,result.State);Assert.True(journal.Pending);Assert.Equal(1,g.Writes);}
 [Fact]public async Task DEFAULT_GATE_OFF_NO_REAL_GUARDIAN_HTTP(){using var p=await Prepare();var g=new Gateway();var result=await p.CreateUseCase(g,g).ExecuteAsync(p.CreateRequest(Guid.NewGuid(),Identity(),Approval()));Assert.Equal("SINGLE_CREATE_WRITE_GATE_OFF",result.Code);Assert.Equal(0,g.Reads);Assert.Equal(0,g.Writes);}
 [Fact]public async Task POSTGRESQL_UNATTESTED_BLOCKS(){using var p=await Prepare();var g=new Gateway();var journal=new Journal();var result=await p.CreateUseCase(g,g,journal,()=>true,new Clock()).ExecuteAsync(p.CreateRequest(Guid.NewGuid(),Identity(),Approval() with{PostgreSqlBootstrapAttested=false}));Assert.Equal("POSTGRESQL_RUNTIME_NOT_ATTESTED",result.Code);Assert.Equal(0,g.Reads);}
 [Fact]public async Task EXISTS_PRECHECK_BLOCKS(){using var p=await Prepare();var g=new Gateway{Exists=true};var journal=new Journal();var result=await p.CreateUseCase(g,g,journal,()=>true,new Clock()).ExecuteAsync(p.CreateRequest(Guid.NewGuid(),Identity(),Approval()));Assert.Equal("GUARDIAN_CREATE_ABSENCE_NOT_PROVEN",result.Code);Assert.Equal(0,g.Writes);}
 [Fact]public async Task PREPARED_DIAGNOSTIC_SANITIZED(){using var p=await Prepare();Assert.DoesNotContain("SYNTHETIC_NAME",p.ToString());Assert.DoesNotContain("12ABC",p.ToString());}

 sealed class FakeCredentials:ICredenciaisGuardianSet26Provider
 {public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct)=>Task.FromResult(new CredenciaisGuardianSet26("SYNTHETIC_EMAIL","SYNTHETIC_PASSWORD","SYNTHETIC_SIGNATURE"));}
 sealed class HttpFake:HttpMessageHandler
 {
  public int Tokens,Posts;public int Status=200;public byte[]? Sent;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
  {
   Assert.Equal("offline.invalid",r.RequestUri!.Host);Assert.Equal(HttpMethod.Post,r.Method);
   if(r.RequestUri.AbsolutePath=="/api/v2/CreateToken"){Tokens++;return new(HttpStatusCode.OK){Content=new StringContent("{\"token\":\"SYNTHETIC_TOKEN\"}")};}
   Assert.Equal("/api/v0/Guardian/Transportadora",r.RequestUri.AbsolutePath);Posts++;Sent=await r.Content!.ReadAsByteArrayAsync(ct);
   return new((HttpStatusCode)Status){Content=new StringContent("{\"codigo\":\"20000014\"}")};
  }
 }
 static GuardianCarrierHmlContract Contract(bool profile=true,bool signature=true)=>new(profile,signature,"SYNTHETIC_CONTRACT","/lookup/{bp}","/ignored","/api/v0/Guardian/Transportadora","/lookup/{code}",404,_=>new(CarrierLookupState.Failed),_=>throw new Exception("LEGACY_CODEC_NOT_USED"),_=>null);
 [Theory][InlineData(200,SingleCreateStateR31.CONFIRMED)][InlineData(201,SingleCreateStateR31.INDETERMINATE)]
 public async Task REAL_ADAPTER_OFFLINE_BOUND_TO_NESTED_BODY_AND_ACTUAL_STATUS(int status,SingleCreateStateR31 expected)
 {
  using var p=await Prepare();var transport=new HttpFake{Status=status};using var http=new HttpClient(transport);
  var options=new GuardianHttpOptions{BaseUrl=new("https://offline.invalid"),Timeout=TimeSpan.FromSeconds(10)};
  var tokens=new GuardianTokenClient(http,options,new FakeCredentials());var journal=new Journal();var reader=new Gateway();
  var use=p.CreateBoundUseCase(reader,http,options,tokens,Contract(),journal,()=>true,new Clock());
  var result=await use.ExecuteAsync(p.CreateRequest(Guid.NewGuid(),Identity(),Approval()));Assert.Equal(expected,result.State);Assert.Equal(1,reader.Reads);Assert.Equal(1,transport.Posts);
  Assert.Equal(p.CopyBody(),transport.Sent);Assert.Equal(Convert.ToHexString(SHA256.HashData(transport.Sent!)).ToLowerInvariant(),journal.Digest);
 }
 [Theory][InlineData(false,true)][InlineData(true,false)]public async Task REAL_ADAPTER_PROFILE_FLAGS_NOT_FORCED(bool profile,bool signature)
 {
  using var p=await Prepare();var transport=new HttpFake();using var http=new HttpClient(transport);var options=new GuardianHttpOptions{BaseUrl=new("https://offline.invalid"),Timeout=TimeSpan.FromSeconds(10)};
  var tokens=new GuardianTokenClient(http,options,new FakeCredentials());var reader=new Gateway();var journal=new Journal();var c=Contract(profile,signature);var bound=p.BindSingleCreateContract(c);
  Assert.Equal(profile,bound.ProfileVerified);Assert.Equal(signature,bound.SignatureVerified);
  var result=await p.CreateBoundUseCase(reader,http,options,tokens,c,journal,()=>true,new Clock()).ExecuteAsync(p.CreateRequest(Guid.NewGuid(),Identity(),Approval()));
  Assert.Equal(SingleCreateStateR31.INDETERMINATE,result.State);Assert.Equal(0,transport.Posts);Assert.Equal(0,transport.Tokens);
 }
 [Fact]public async Task SINGLE_CREATE_CODEC_REJECTS_UPDATE_OR_OTHER_IDENTITY(){using var p=await Prepare();var request=p.CreateRequest(Guid.NewGuid(),Identity(),Approval());Assert.Throws<CarrierGatewayException>(()=>p.EncodeWrite(new(Guid.NewGuid(),"20000014","20000014","UPDATE",request.Payload!)));Assert.Throws<CarrierGatewayException>(()=>p.EncodeWrite(new(Guid.NewGuid(),"20000014","14100050","CREATE",request.Payload!)));}
 [Fact]public void RESPONSE_CODE_DECODER_REJECTS_DUPLICATE(){using var j=JsonDocument.Parse("{\"codigo\":\"20000014\",\"codigo\":\"OTHER\"}");Assert.Null(PreparedSingleCreateR41.DecodeResponseCode(j.RootElement));}
}
