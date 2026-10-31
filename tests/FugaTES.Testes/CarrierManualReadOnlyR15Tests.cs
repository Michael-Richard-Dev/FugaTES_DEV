using FugaTES.Aplicacao.Transportadoras;
namespace FugaTES.Testes;
public class CarrierManualReadOnlyR15Tests
{
    static readonly DateTimeOffset Now=new(2026,10,11,12,0,0,TimeSpan.Zero);
    sealed class Clock:TimeProvider {public override DateTimeOffset GetUtcNow()=>Now;}
    sealed class Sap:ISapCarrierManualReadOnly {
        public int Calls;public string? Bp;
        public CarrierManualSap Data=new("0000000042",new Dictionary<string,string?>{{"BusinessPartnerFullName","Razao SAP"}},[new("CRM010",Now.AddDays(-30),Now.AddDays(30))]);
        public Task<CarrierManualSap> ConsultarBpAsync(string bp,CancellationToken ct){ct.ThrowIfCancellationRequested();Calls++;Bp=bp;return Task.FromResult(Data);}
    }
    sealed class Guardian:IGuardianCarrierManualReadOnly {
        public int Calls;public CarrierManualIdentity? Identity;
        public CarrierManualGuardian Data=new(CarrierLookupState.Absent,null,new Dictionary<string,string?>(),true);
        public Task<CarrierManualGuardian> ConsultarCodigoAsync(CarrierManualIdentity i,CancellationToken ct){ct.ThrowIfCancellationRequested();Calls++;Identity=i;return Task.FromResult(Data);}
    }
    static ConsultarTransportadoraManualReadOnlyCasoDeUso Case(Sap sap,Guardian guardian,bool authorized=true,bool identity=true,bool complete=false)
        =>new(sap,guardian,_=>authorized,_=>new("GUARDIAN42","SIG_GET42",identity,"OFFLINE_IDENTITY"),new([new("BusinessPartnerFullName","razaoSocial",true)],complete,"OFFLINE_COMPARISON"),new Clock());
    [Theory][InlineData("")][InlineData("42,43")][InlineData("42 43")][InlineData("[42]")]
    public async Task MANUAL_BP_ONLY_rejects_batch_without_query(string bp){var s=new Sap();var g=new Guardian();Assert.Equal(DecisaoCarrierManual.BLOCKED,(await Case(s,g).ExecutarAsync(bp,default)).Decisao);Assert.Equal(0,s.Calls);Assert.Equal(0,g.Calls);}
    [Fact] public async Task MANUAL_BP_ONLY_preserves_leading_zero_and_distinct_Guardian_code(){var s=new Sap();var g=new Guardian();var r=await Case(s,g).ExecutarAsync("0000000042",default);Assert.Equal("0000000042",s.Bp);Assert.Equal("GUARDIAN42",g.Identity!.Codigo);Assert.Equal(DecisaoCarrierManual.CREATE_CANDIDATE,r.Decisao);}
    [Fact] public async Task CRM010_ELIGIBILITY_expired_role_is_blocked(){var s=new Sap{Data=new("0000000042",new Dictionary<string,string?>(),[new("CRM010",Now.AddYears(-2),Now.AddYears(-1))])};var g=new Guardian();Assert.Equal(DecisaoCarrierManual.BLOCKED,(await Case(s,g).ExecutarAsync("0000000042",default)).Decisao);Assert.Equal(0,g.Calls);}
    [Fact] public async Task CRM010_DATE_INDETERMINATE_does_not_assume_end_of_day(){var s=new Sap{Data=new("0000000042",new Dictionary<string,string?>(),[new("CRM010",Now.AddYears(-1),Now)])};var g=new Guardian();Assert.Equal(DecisaoCarrierManual.INDETERMINATE,(await Case(s,g).ExecutarAsync("0000000042",default)).Decisao);Assert.Equal(0,g.Calls);}
    [Fact] public async Task CRM010_DATE_INDETERMINATE_invalid_dates(){var s=new Sap{Data=new("0000000042",new Dictionary<string,string?>(),[new("CRM010",null,null)])};Assert.Equal(DecisaoCarrierManual.INDETERMINATE,(await Case(s,new Guardian()).ExecutarAsync("0000000042",default)).Decisao);}
    [Fact] public async Task CANDIDATE_CREATE_NO_WRITE_requires_proven_scoped_404(){var s=new Sap();var g=new Guardian();Assert.Equal(DecisaoCarrierManual.CREATE_CANDIDATE,(await Case(s,g).ExecutarAsync("0000000042",default)).Decisao);Assert.Equal(1,s.Calls);Assert.Equal(1,g.Calls);g.Data=g.Data with{Ausencia404Comprovada=false};Assert.Equal(DecisaoCarrierManual.INDETERMINATE,(await Case(s,g).ExecutarAsync("0000000042",default)).Decisao);}
    [Fact] public async Task CANDIDATE_UPDATE_NO_WRITE_uses_documented_legal_name_difference(){var g=new Guardian{Data=new(CarrierLookupState.Unique,"GUARDIAN42",new Dictionary<string,string?>{{"razaoSocial","Outra razao"}})};Assert.Equal(DecisaoCarrierManual.UPDATE_CANDIDATE,(await Case(new Sap(),g).ExecutarAsync("0000000042",default)).Decisao);}
    [Fact] public async Task NO_ACTION_requires_complete_approved_comparison(){var g=new Guardian{Data=new(CarrierLookupState.Unique,"GUARDIAN42",new Dictionary<string,string?>{{"razaoSocial","Razao SAP"}})};Assert.Equal(DecisaoCarrierManual.INDETERMINATE,(await Case(new Sap(),g).ExecutarAsync("0000000042",default)).Decisao);Assert.Equal(DecisaoCarrierManual.NO_ACTION,(await Case(new Sap(),g,complete:true).ExecutarAsync("0000000042",default)).Decisao);}
    [Fact] public async Task GUARDIAN_GET_CLASSIFICATION_identity_mismatch_or_ambiguity_is_indeterminate(){var g=new Guardian{Data=new(CarrierLookupState.Unique,"OTHER",new Dictionary<string,string?>())};Assert.Equal(DecisaoCarrierManual.INDETERMINATE,(await Case(new Sap(),g).ExecutarAsync("0000000042",default)).Decisao);g.Data=g.Data with{State=CarrierLookupState.Ambiguous};Assert.Equal(DecisaoCarrierManual.INDETERMINATE,(await Case(new Sap(),g).ExecutarAsync("0000000042",default)).Decisao);}
    [Fact] public async Task WRITE_GUARD_unauthorized_profile_performs_zero_reads(){var s=new Sap();var g=new Guardian();Assert.Equal(DecisaoCarrierManual.BLOCKED,(await Case(s,g,authorized:false).ExecutarAsync("0000000042",default)).Decisao);Assert.Equal(0,s.Calls);Assert.Equal(0,g.Calls);}
    [Fact] public async Task Identity_not_proven_does_not_treat_404_as_global_absence(){var g=new Guardian();Assert.Equal(DecisaoCarrierManual.INDETERMINATE,(await Case(new Sap(),g,identity:false).ExecutarAsync("0000000042",default)).Decisao);Assert.Equal(0,g.Calls);}

    [Fact] public async Task SHARED_LOCK_manual_refused_while_automatic_owns_same_semaphore()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int manualCalls=0;
        var action=new FugaTES.WinForms.Transportadoras.MasterdataR3.AcaoCategoriaDelegadaMasterdataR3(
            FugaTES.WinForms.Transportadoras.MasterdataR3.CategoriaMasterdataR3.Transportadoras,true,true,async ct=>{
                entered.TrySetResult();await release.Task.WaitAsync(ct);return new(FugaTES.WinForms.Transportadoras.MasterdataR3.EstadoMasterdataR3.Parcial,0,0,0,0,null,false);});
        var shared=new FugaTES.WinForms.Transportadoras.MasterdataR3.OrquestradorCompartilhadoMasterdataR3([action],true);
        var automatic=shared.ExecutarAsync(FugaTES.WinForms.Transportadoras.MasterdataR3.OrigemCicloMasterdataR3.Automatica);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try{var result=await shared.ConsultarReadOnlyAsync("0000000042",_=>{manualCalls++;return Task.FromResult(new ResultadoCarrierManual("0000000042",DecisaoCarrierManual.NO_ACTION,"COMPLETE_EQUIVALENCE_PROVEN"));},default);
            Assert.Equal("SHARED_LOCK_BUSY",result.Motivo);Assert.Equal(0,manualCalls);}
        finally{release.TrySetResult();await automatic;await shared.EncerrarAsync();}
    }
    [Fact] public async Task SHARED_LOCK_readonly_owns_lock_and_close_cancels_it()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int automaticCalls=0;
        var action=new FugaTES.WinForms.Transportadoras.MasterdataR3.AcaoCategoriaDelegadaMasterdataR3(
            FugaTES.WinForms.Transportadoras.MasterdataR3.CategoriaMasterdataR3.Transportadoras,true,true,_=>{automaticCalls++;return Task.FromResult(new FugaTES.WinForms.Transportadoras.MasterdataR3.ResultadoCategoriaMasterdataR3(FugaTES.WinForms.Transportadoras.MasterdataR3.EstadoMasterdataR3.Parcial,0,0,0,0,null,false));});
        var shared=new FugaTES.WinForms.Transportadoras.MasterdataR3.OrquestradorCompartilhadoMasterdataR3([action],true);
        var read=shared.ConsultarReadOnlyAsync("0000000042",async ct=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);return new("0000000042",DecisaoCarrierManual.INDETERMINATE,"CANCELLED");},default);
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal(FugaTES.WinForms.Transportadoras.MasterdataR3.SituacaoCicloMasterdataR3.Ocupado,(await shared.ExecutarAsync(FugaTES.WinForms.Transportadoras.MasterdataR3.OrigemCicloMasterdataR3.Automatica)).Situacao);Assert.Equal(0,automaticCalls);}
        finally{await shared.EncerrarAsync();await read;}
    }
    [Fact] public async Task NO_BACKGROUND_WORKER_and_no_persistent_enqueue_on_manual_entrypoint()
    {
        int factoryCalls=0,enqueueCalls=0;var s=new Sap();var g=new Guardian();var useCase=Case(s,g);
        var lifecycle=new FugaTES.WinForms.Transportadoras.CarrierDesktopLifecycle(
            _=>{factoryCalls++;return Task.FromResult(new FugaTES.WinForms.Transportadoras.CarrierDesktopSession(_=>Task.CompletedTask,(_,_,_,_)=>{enqueueCalls++;return Task.FromResult(new FugaTES.HostIntegracao.CarrierManualResponse(true,"CARRIER_REQUEST_ACCEPTED"));}));},
            new Clock(),useCase.ExecutarAsync);
        try{var result=await lifecycle.ConsultarManualReadOnlyAsync("0000000042",default);Assert.Equal(DecisaoCarrierManual.CREATE_CANDIDATE,result.Decisao);Assert.Equal(1,s.Calls);Assert.Equal(1,g.Calls);Assert.Equal(0,factoryCalls);Assert.Equal(0,enqueueCalls);}
        finally{await lifecycle.StopAsync();}
    }

    [Fact] public async Task MANUAL_DELTA_INDEPENDENT_survives_failed_automatic_bootstrap()
    {
        int queries=0;
        var lifecycle=new FugaTES.WinForms.Transportadoras.CarrierDesktopLifecycle(
            _=>throw new FugaTES.WinForms.Runtime.DesktopStartupException("SAP_DELTA_CONTRACT_NOT_PROVEN"),
            new Clock(),(bp,_)=>{queries++;return Task.FromResult(new ResultadoCarrierManual(bp,DecisaoCarrierManual.INDETERMINATE,"PARTIAL_COMPARISON_ONLY"));});
        try
        {
            lifecycle.Start(); // Only a failing fake factory; no Carrier session/worker/HTTP is started.
            await lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(lifecycle.Snapshot.Active);
            Assert.Equal("SAP_DELTA_CONTRACT_NOT_PROVEN",lifecycle.Snapshot.Code);
            Assert.Equal(DecisaoCarrierManual.INDETERMINATE,(await lifecycle.ConsultarManualReadOnlyAsync("0000000042",default)).Decisao);
            Assert.Equal(1,queries);
        }
        finally{await lifecycle.StopAsync();}
        Assert.Equal(DecisaoCarrierManual.BLOCKED,(await lifecycle.ConsultarManualReadOnlyAsync("0000000042",default)).Decisao);
        Assert.Equal(1,queries);
    }
    [Fact] public async Task SHARED_LOCK_sanitizes_failed_query_and_is_released()
    {
        var shared=new FugaTES.WinForms.Transportadoras.MasterdataR3.OrquestradorCompartilhadoMasterdataR3();
        try
        {
            var result=await shared.ConsultarReadOnlyAsync("0000000042",_=>throw new InvalidOperationException("password=OFFLINE_SECRET"),default);
            Assert.Equal("MANUAL_READ_FAILED",result.Motivo);
            Assert.DoesNotContain("OFFLINE_SECRET",result.ToString());
            Assert.Empty(shared.Logs());
            var next=await shared.ConsultarReadOnlyAsync("0000000042",_=>Task.FromResult(new ResultadoCarrierManual("0000000042",DecisaoCarrierManual.INDETERMINATE,"PARTIAL_COMPARISON_ONLY")),default);
            Assert.Equal("PARTIAL_COMPARISON_ONLY",next.Motivo);
        }
        finally{await shared.EncerrarAsync();}
    }
}
