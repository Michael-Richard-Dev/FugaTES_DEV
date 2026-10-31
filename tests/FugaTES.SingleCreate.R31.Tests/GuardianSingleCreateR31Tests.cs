using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Aplicacao.Transportadoras.SingleCreateR31;
using FugaTES.WinForms.Transportadoras;
using FugaTES.WinForms.Transportadoras.MasterdataR3;
namespace FugaTES.SingleCreate.R31.Tests;
public class GuardianSingleCreateR31Tests
{
    static readonly DateTimeOffset Now=new(2026,10,12,15,0,0,TimeSpan.Zero);
    sealed class Clock:TimeProvider {public override DateTimeOffset GetUtcNow()=>Now;}
    sealed class Gateway:IGuardianCarrierManualReadOnly,IGuardianCarrierGateway
    {
        public int Writes,Reads;public CarrierWriteAttempt? Sent;public List<CarrierManualIdentity> Identities=[];
        public bool Exists,Ambiguous,Unscoped,Timeout,ReadbackWrong,Wait,AlwaysAbsent,Rejected,WaitDuringWrite;
        public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Dispatched=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CarrierManualGuardian> ConsultarCodigoAsync(CarrierManualIdentity i,CancellationToken ct)
        {
            Reads++;Identities.Add(i);Entered.TrySetResult();if(Wait)await Task.Delay(System.Threading.Timeout.Infinite,ct);
            if(Ambiguous)return new(CarrierLookupState.Ambiguous,null,new Dictionary<string,string?>());
            if(Reads==1&&!Exists||AlwaysAbsent)return new(CarrierLookupState.Absent,null,new Dictionary<string,string?>(),!Unscoped);
            return new(CarrierLookupState.Unique,ReadbackWrong?"OFFLINE_OTHER_CODE":i.Codigo,new Dictionary<string,string?>{{"OFFLINE_FIELD","OFFLINE_VALUE"}});
        }
        public Task<CarrierLookup> ConsultarAsync(string bp,CancellationToken ct)=>throw new Exception("NO_BP_LOOKUP");
        public Task<CarrierLookup> LerConfirmacaoAsync(string code,CancellationToken ct)=>throw new Exception("NO_UNSCOPED_READBACK");
        public async Task<CarrierWriteResult> GravarAsync(CarrierWriteAttempt a,CancellationToken ct)
        {Writes++;Sent=a;Dispatched.TrySetResult();if(WaitDuringWrite)await Task.Delay(System.Threading.Timeout.Infinite,ct);if(Timeout)throw new TimeoutException("OFFLINE_SECRET_NOT_LOGGED");return new CarrierWriteResult(!Rejected,false,ReadbackWrong?"OFFLINE_OTHER_CODE":a.GuardianCode){HttpStatusCode=200};}
    }
    sealed class Journal:ISingleCreateJournalR31
    {
        public int Prepared,Confirmed;public bool Pending;public string? Code,Hash;public Action? AfterPrepare;
        public Task<bool> TryPrepareAsync(Guid id,string bp,string code,string hash,CancellationToken ct)
        {if(Pending)return Task.FromResult(false);Pending=true;Prepared++;Code=code;Hash=hash;AfterPrepare?.Invoke();return Task.FromResult(true);}
        public Task ConfirmAsync(Guid id,CancellationToken ct){Confirmed++;Pending=false;return Task.CompletedTask;}
    }
    sealed class Harness
    {
        public Gateway Gateway=new();public Journal Journal=new();public bool Gate=true;
        public GuardianSingleCreateUseCaseR31 Case;
        public SingleCreateRequestR31 Request;
        public Harness(bool defaultGate=false)
        {
            Case=new(Gateway,Gateway,Journal,defaultGate?null:()=>Gate,_=>true,(_,r)=>r.Campos.TryGetValue("OFFLINE_FIELD",out var v)&&v=="OFFLINE_VALUE",new Clock());
            Request=new(Guid.NewGuid(),"14100050",new("OFFLINE_NEW_CODE","OFFLINE_GET_SCOPE",true,"OFFLINE_ID_PROOF"),
                new("14100050","OFFLINE_NAME","OFFLINE_LEGAL_NAME"),Now.AddDays(-2),Now.AddDays(2),Now,
                new("14100050","OFFLINE_NEW_CODE","OFFLINE_GET_SCOPE","OFFLINE_SPECIFIC_APPROVAL",Now.AddHours(1),true,true,true,true,true,true,true));
        }
        public Task<SingleCreateResultR31> Run()=>Case.ExecuteAsync(Request);
        public async Task Blocked(string code)
        {var r=await Run();Assert.Equal(SingleCreateStateR31.BLOCKED,r.State);Assert.Equal(code,r.Code);Assert.Equal(0,Gateway.Writes);}
    }
    [Fact] public async Task MISSING_CODE_BLOCKS_POST()
    {var h=new Harness();h.Request=h.Request with{Identity=null};await h.Blocked("GUARDIAN_CODE_NOT_APPROVED");}
    [Fact] public async Task MISSING_SIGNATURE_BLOCKS_POST()
    {var h=new Harness();h.Request=h.Request with{Identity=h.Request.Identity! with{IdAssinatura=""}};await h.Blocked("GUARDIAN_SIGNATURE_NOT_BOUND");}
    [Fact] public async Task PARTIAL_PAYLOAD_BLOCKS_POST()
    {var h=new Harness();h.Request=h.Request with{Approval=h.Request.Approval! with{PayloadContractComplete=false}};await h.Blocked("CREATE_PAYLOAD_CONTRACT_PARTIAL");}
    [Fact] public async Task INVALID_CRM010_BLOCKS_POST()
    {var h=new Harness();h.Request=h.Request with{ValidFrom=Now.AddDays(-4),ValidTo=Now.AddDays(-2)};await h.Blocked("CRM010_NOT_VALID");}
    [Fact] public async Task INDETERMINATE_CRM010_BLOCKS_POST()
    {var h=new Harness();h.Request=h.Request with{ValidFrom=null};await h.Blocked("CRM010_INDETERMINATE");}
    [Fact] public async Task POSTGRESQL_UNATTESTED_BLOCKS_RUNTIME()
    {var h=new Harness();h.Request=h.Request with{Approval=h.Request.Approval! with{PostgreSqlBootstrapAttested=false}};await h.Blocked("POSTGRESQL_RUNTIME_NOT_ATTESTED");Assert.Equal(0,h.Gateway.Reads);}
    [Fact] public async Task DEFAULT_WRITE_GATE_OFF()
    {var h=new Harness(true);await h.Blocked("SINGLE_CREATE_WRITE_GATE_OFF");Assert.Equal(0,h.Gateway.Reads);Assert.Equal(0,h.Journal.Prepared);}
    [Fact] public async Task NO_BP_CODE_FALLBACK()
    {var h=new Harness();h.Request=h.Request with{Approval=h.Request.Approval! with{CodeApproved=false}};await h.Blocked("GUARDIAN_CODE_NOT_APPROVED");}
    [Fact] public async Task NO_AUTOMATIC_CREATE()
    {
        var h=new Harness();var life=CarrierDesktopComposition.Create();life.Start();await life.Completion;await life.StopAsync();
        Assert.Equal(0,h.Gateway.Writes);Assert.Equal(0,h.Gateway.Reads);Assert.False(life.Snapshot.Active);
    }
    [Fact] public async Task NO_EXTERNAL_HTTP_IN_OFFLINE_QA()
    {
        var h=new Harness();var r=await h.Run();Assert.Equal(SingleCreateStateR31.CONFIRMED,r.State);
        Assert.Equal(1,h.Gateway.Writes);Assert.Equal(1,h.Gateway.Reads);Assert.Equal(1,h.Journal.Prepared);Assert.Equal(1,h.Journal.Confirmed);
        Assert.Equal("OFFLINE_NEW_CODE",h.Gateway.Sent!.GuardianCode);Assert.NotEqual(h.Request.BusinessPartner,h.Gateway.Sent.GuardianCode);
        Assert.All(h.Gateway.Identities,i=>Assert.Equal(h.Request.Identity,i));Assert.Equal("CREATE",h.Gateway.Sent.Action);
    }
    [Fact] public async Task UNSCOPED_ABSENCE_BLOCKS_POST()
    {var h=new Harness();h.Gateway.Unscoped=true;await h.Blocked("GUARDIAN_CREATE_ABSENCE_NOT_PROVEN");}
    [Theory][InlineData(false)][InlineData(true)] public async Task EXISTING_OR_AMBIGUOUS_BLOCKS_CREATE(bool ambiguous)
    {var h=new Harness();h.Gateway.Exists=true;h.Gateway.Ambiguous=ambiguous;await h.Blocked("GUARDIAN_CREATE_ABSENCE_NOT_PROVEN");}
    [Fact] public async Task SPECIFIC_AUTHORIZATION_REQUIRED()
    {var h=new Harness();h.Request=h.Request with{Approval=h.Request.Approval! with{SpecificCreateAuthorized=false}};await h.Blocked("SINGLE_CREATE_NOT_AUTHORIZED");}
    [Fact] public async Task APPROVAL_BOUND_TO_SINGLE_IDENTITY()
    {var h=new Harness();h.Request=h.Request with{Approval=h.Request.Approval! with{BusinessPartner="OTHER_BP"}};await h.Blocked("SAP_BP_NOT_CONFIRMED");}
    [Fact] public async Task TIMEOUT_INDETERMINATE_NO_RETRY()
    {
        var h=new Harness();h.Gateway.Timeout=true;h.Gateway.AlwaysAbsent=true;
        Assert.Equal(SingleCreateStateR31.INDETERMINATE,(await h.Run()).State);Assert.True(h.Journal.Pending);
        h.Request=h.Request with{AttemptId=Guid.NewGuid()};Assert.Equal(SingleCreateStateR31.BLOCKED,(await h.Run()).State);Assert.Equal(1,h.Gateway.Writes);
    }
    [Fact] public async Task RESPONSE_CODE_MISMATCH_PRESERVES_PENDING()
    {var h=new Harness();h.Gateway.ReadbackWrong=true;Assert.Equal(SingleCreateStateR31.INDETERMINATE,(await h.Run()).State);Assert.True(h.Journal.Pending);Assert.Equal(0,h.Journal.Confirmed);}
    [Fact] public async Task WRITE_REVOKED_AFTER_PREPARE_NOT_SENT()
    {var h=new Harness();h.Journal.AfterPrepare=()=>h.Gate=false;Assert.Equal(SingleCreateStateR31.INDETERMINATE,(await h.Run()).State);Assert.Equal(0,h.Gateway.Writes);Assert.True(h.Journal.Pending);}
    [Fact] public async Task NO_SENSITIVE_VALUES_IN_RESULT_OR_JOURNAL()
    {var h=new Harness();var result=await h.Run();var body=JsonSerializer.Serialize(result);Assert.DoesNotContain("OFFLINE_NAME",body);Assert.DoesNotContain("OFFLINE_GET_SCOPE",body);Assert.Equal(64,h.Journal.Hash!.Length);}
    [Fact] public async Task SAME_SHARED_LOCK_BLOCKS_R15_AND_R20C()
    {
        var h=new Harness();h.Gateway.Wait=true;var shared=new OrquestradorCompartilhadoMasterdataR3(execucaoDeNegocioExplicitamenteAutorizada:true);
        var manual=new GuardianSingleCreateManualR31(shared,h.Case);var run=manual.ExecuteAsync(h.Request);await h.Gateway.Entered.Task;
        var read=await shared.ExecutarConsultaProtegidaAsync(_=>Task.FromResult(1));Assert.Equal(SituacaoConsultaProtegidaMasterdataR3.Ocupada,read.Situacao);
        var second=await manual.ExecuteAsync(h.Request with{AttemptId=Guid.NewGuid()});Assert.Equal("SHARED_LOCK_BUSY",second.Code);
        await shared.EncerrarAsync();await run;Assert.Equal(0,h.Gateway.Writes);
    }
    [Fact] public async Task MANUAL_RUNTIME_DEFAULT_BLOCKED()
    {var h=new Harness();var manual=new GuardianSingleCreateManualR31(new(),h.Case);Assert.Equal("SINGLE_CREATE_RUNTIME_NOT_APPROVED",(await manual.ExecuteAsync(h.Request)).Code);Assert.Equal(0,h.Gateway.Reads);}
    [Fact] public async Task MISSING_JOURNAL_BLOCKS_POST()
    {
        var h=new Harness();var c=new GuardianSingleCreateUseCaseR31(h.Gateway,h.Gateway,writeGate:()=>true,completePayloadValidator:_=>true,readbackMatcher:(_,_)=>true,clock:new Clock());
        Assert.Equal("POSTGRESQL_RUNTIME_NOT_ATTESTED",(await c.ExecuteAsync(h.Request)).Code);Assert.Equal(0,h.Gateway.Writes);
    }
    [Fact] public async Task MISSING_PAYLOAD_VALIDATOR_BLOCKS_POST()
    {var h=new Harness();var c=new GuardianSingleCreateUseCaseR31(h.Gateway,h.Gateway,h.Journal,()=>true,clock:new Clock());Assert.Equal("CREATE_PAYLOAD_CONTRACT_PARTIAL",(await c.ExecuteAsync(h.Request)).Code);Assert.Equal(0,h.Gateway.Writes);}
    [Fact] public async Task REJECTION_REQUIRES_RECONCILIATION()
    {var h=new Harness();h.Gateway.Rejected=true;Assert.Equal(SingleCreateStateR31.INDETERMINATE,(await h.Run()).State);Assert.True(h.Journal.Pending);Assert.Equal(0,h.Journal.Confirmed);}
    [Fact] public async Task APP_CLOSE_AFTER_DISPATCH_PRESERVES_INDETERMINATE()
    {
        var h=new Harness();h.Gateway.WaitDuringWrite=true;var shared=new OrquestradorCompartilhadoMasterdataR3(execucaoDeNegocioExplicitamenteAutorizada:true);
        var run=new GuardianSingleCreateManualR31(shared,h.Case).ExecuteAsync(h.Request);await h.Gateway.Dispatched.Task;
        await shared.EncerrarAsync();var result=await run;Assert.Equal(SingleCreateStateR31.INDETERMINATE,result.State);Assert.True(h.Journal.Pending);Assert.Equal(1,h.Gateway.Writes);
    }
    [Fact] public async Task EXPIRED_AUTHORIZATION_BLOCKS_BEFORE_GET()
    {var h=new Harness();h.Request=h.Request with{Approval=h.Request.Approval! with{UntilUtc=Now}};await h.Blocked("SINGLE_CREATE_NOT_AUTHORIZED");Assert.Equal(0,h.Gateway.Reads);}
}
