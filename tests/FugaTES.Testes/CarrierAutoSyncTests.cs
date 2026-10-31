using FugaTES.Aplicacao.Transportadoras;
namespace FugaTES.Testes;
public class CarrierAutoSyncTests
{
    static readonly CarrierPayload Payload = new("000001","Transportadora","Transportadora Ltda");
    static CarrierSyncPolicy Policy(bool allowed=true) => new(new("HML",true,true,DateTimeOffset.UtcNow.AddMinutes(5),allowed?["000001"]:[],[]),TimeProvider.System);
    static SincronizarTransportadoras Motor(Memory store,Gateway gateway,bool allowed=true) => new(new Discovery(),gateway,store,Policy(allowed),TimeProvider.System,(_,_)=>Task.FromResult(Payload));
    [Fact] public async Task Absent_creates_readback_then_second_run_no_action()
    { var store=new Memory(); var gateway=new Gateway(); var motor=Motor(store,gateway); var first=await motor.ExecutarCicloAsync(default); Assert.Equal(1,first!.Created); Assert.Equal(1,gateway.Writes); var second=await motor.ExecutarCicloAsync(default); Assert.Equal(1,second!.Unchanged); Assert.Equal(1,gateway.Writes); Assert.Null(store.State!.PendingAttemptId); }
    [Fact] public async Task Different_updates_after_persisting_attempt()
    { var store=new Memory(); var gateway=new Gateway {Record=Payload with {Name="Antigo"}}; gateway.OnWrite=()=>Assert.NotNull(store.State!.PendingAttemptId); var result=await Motor(store,gateway).ExecutarCicloAsync(default); Assert.Equal(1,result!.Updated); Assert.Equal("UPDATE",gateway.Action); }
    [Fact] public async Task Timeout_survives_restart_without_blind_retry()
    { var store=new Memory(); var gateway=new Gateway {Timeout=true}; await Motor(store,gateway).ExecutarCicloAsync(default); Assert.NotNull(store.State!.PendingAttemptId); var again=await Motor(store,gateway).ExecutarCicloAsync(default); Assert.Equal(1,gateway.Writes); Assert.Equal(1,again!.Pending); }
    [Fact] public async Task Readback_mismatch_retains_indeterminate()
    { var store=new Memory(); var gateway=new Gateway {Mismatch=true}; var result=await Motor(store,gateway).ExecutarCicloAsync(default); Assert.Equal(0,result!.Created); Assert.Equal(1,result.Pending); Assert.NotNull(store.State!.PendingAttemptId); }
    [Fact] public async Task Ambiguous_get_blocks_write()
    { var store=new Memory(); var gateway=new Gateway {Ambiguous=true}; var result=await Motor(store,gateway).ExecutarCicloAsync(default); Assert.Equal(0,gateway.Writes); Assert.Equal(1,result!.Failed); }
    [Fact] public async Task Unauthorized_mass_blocks_write()
    { var store=new Memory(); var gateway=new Gateway(); var result=await Motor(store,gateway,false).ExecutarCicloAsync(default); Assert.Equal(0,gateway.Writes); Assert.Equal(0,result!.Created); }
    [Fact] public async Task Second_instance_without_lock_does_no_http()
    { var store=new Memory {Busy=true}; var gateway=new Gateway(); Assert.Null(await Motor(store,gateway).ExecutarCicloAsync(default)); Assert.Equal(0,gateway.Gets); }
    [Fact] public async Task Pending_update_does_not_reconcile_different_guardian_identity() {var store=new Memory {State=new("000001","Transportadora","G_OLD",SincronizarTransportadoras.Hash(Payload),"INDETERMINATE","UPDATE","TIMEOUT",DateTimeOffset.UtcNow,null,Guid.NewGuid())};var gateway=new Gateway {Record=Payload};var result=await Motor(store,gateway).ExecutarCicloAsync(default);Assert.NotNull(store.State!.PendingAttemptId);Assert.Equal(1,result!.Pending);Assert.Equal(0,gateway.Writes);}
    [Fact] public async Task Long_cycle_renews_heartbeat_while_sap_is_pending()
    {
        var clock=new CarrierTestClock();var store=new Memory();var gateway=new Gateway();var entered=new TaskCompletionSource();var release=new TaskCompletionSource();
        var policy=new CarrierSyncPolicy(new("HML",true,true,clock.GetUtcNow().AddMinutes(5),["000001"],[]),clock);
        var motor=new SincronizarTransportadoras(new Discovery(),gateway,store,policy,clock,async (_,ct)=>{entered.SetResult();await release.Task.WaitAsync(ct);return Payload;});
        var task=motor.ExecutarCicloAsync(default);await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for(var tick=0;tick<3;tick++){clock.Advance(TimeSpan.FromSeconds(30));for(var wait=0;wait<40&&store.Heartbeats<tick+2;wait++)await Task.Delay(10);Assert.True(store.Heartbeats>=tick+2);Assert.Equal(clock.GetUtcNow(),store.LastHeartbeat);}
        release.SetResult();await task.WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact] public async Task Cycle_finishing_between_ticks_predicts_next_scheduled_tick()
    {
        var clock=new CarrierTestClock();var start=clock.GetUtcNow();var store=new Memory();var release=new TaskCompletionSource();var entered=new TaskCompletionSource();
        var policy=new CarrierSyncPolicy(new("HML",true,true,start.AddMinutes(5),["000001"],[]),clock);
        var motor=new SincronizarTransportadoras(new Discovery(),new Gateway(),store,policy,clock,async(_,ct)=>{entered.SetResult();await release.Task.WaitAsync(ct);return Payload;});
        var task=motor.ExecutarCicloAsync(default);await entered.Task;clock.Advance(TimeSpan.FromSeconds(35));release.SetResult();await task.WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal(start.AddSeconds(60),store.NextHeartbeat);
    }
    sealed class Discovery:ISapCarrierDiscovery { public Task<CarrierPage> LerPaginaAsync(CarrierCursor c,CancellationToken ct)=>Task.FromResult(new CarrierPage([],c,true)); }
    sealed class Gateway:IGuardianCarrierGateway
    { public CarrierPayload? Record; public bool Timeout,Mismatch,Ambiguous; public int Writes,Gets; public string? Action; public Action? OnWrite;
      public Task<CarrierLookup> ConsultarAsync(string bp,CancellationToken ct) { Gets++; return Task.FromResult(Ambiguous?new(CarrierLookupState.Ambiguous):Record is null?new CarrierLookup(CarrierLookupState.Absent):new(CarrierLookupState.Unique,"G001",Record)); }
      public Task<CarrierWriteResult> GravarAsync(CarrierWriteAttempt a,CancellationToken ct) { OnWrite?.Invoke(); Writes++; Action=a.Action; if(Timeout)return Task.FromResult(new CarrierWriteResult(false,true,ErrorCode:"TIMEOUT")); Record=a.Payload; return Task.FromResult(new CarrierWriteResult(true,false,"G001")); }
      public Task<CarrierLookup> LerConfirmacaoAsync(string code,CancellationToken ct)=>Task.FromResult(new CarrierLookup(CarrierLookupState.Unique,code,Mismatch?Payload with {Name="Divergente"}:Record));
    }
    sealed class Memory:ICarrierSyncStore,ICarrierCycleLease
    { public CarrierStoredBp? State; public bool Busy; public int Heartbeats; public DateTimeOffset? LastHeartbeat; public DateTimeOffset? NextHeartbeat; public CancellationToken PerdaToken=>default;
      public Task<ICarrierCycleLease?> TentarAdquirirCicloAsync(CancellationToken ct)=>Task.FromResult<ICarrierCycleLease?>(Busy?null:this);
      public Task<CarrierStoredBp?> CarregarEstadoAsync(string bp,CancellationToken ct)=>Task.FromResult(State);
      public Task<CarrierStoredCheckpoint> CarregarCheckpointAsync(CancellationToken ct)=>Task.FromResult(new CarrierStoredCheckpoint(null,null,null));
      public Task<bool> EnfileirarPedidoAsync(Guid id,string bp,string sid,DateTimeOffset at,CancellationToken ct)=>Task.FromResult(true);
      public Task VerificarAtivaAsync(CancellationToken ct)=>Task.CompletedTask;
      public Task<CarrierManualRequest?> TomarPedidoAsync(CancellationToken ct)=>Task.FromResult<CarrierManualRequest?>(null);
      public Task ConcluirPedidoAsync(Guid id,string code,CancellationToken ct)=>Task.CompletedTask;
      public Task IniciarAsync(Guid id,DateTimeOffset at,CancellationToken ct)=>Task.CompletedTask;
      public Task<bool> RegistrarAntesDoWriteAsync(CarrierPendingWrite a,CancellationToken ct) { if(State?.PendingAttemptId is not null)return Task.FromResult(false); State=new(a.Bp,"",a.GuardianCode,a.ExpectedHash,"WRITE_IN_FLIGHT",a.Action,"WRITE_IN_FLIGHT",a.StartedUtc,null,a.AttemptId); return Task.FromResult(true); }
      public Task ConcluirBpAsync(CarrierStoredBp s,CancellationToken ct){State=s;return Task.CompletedTask;}
      public Task ConfirmarPaginaAsync(CarrierStoredCheckpoint c,CancellationToken ct)=>Task.CompletedTask;
      public Task RegistrarEtapaAsync(CarrierStoredStep s,CancellationToken ct)=>Task.CompletedTask;
      public Task HeartbeatAsync(DateTimeOffset a,DateTimeOffset b,CancellationToken ct){LastHeartbeat=a;NextHeartbeat=b;Interlocked.Increment(ref Heartbeats);return Task.CompletedTask;}
      public Task ConcluirAsync(CarrierStoredCycle c,CancellationToken ct)=>Task.CompletedTask;
      public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}