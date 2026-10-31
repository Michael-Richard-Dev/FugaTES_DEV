using FugaTES.Aplicacao.Transportadoras;
using FugaTES.WinForms.Transportadoras;
namespace FugaTES.Testes;
public class CarrierDesktopPendingWriteTests
{
    [Fact] public async Task Cancel_during_mock_POST_keeps_durable_attempt_and_next_run_does_not_retry()
    {
        var payload=new CarrierPayload("0001","Carrier","Carrier Ltd");var store=new Memory();var gateway=new Gateway();
        var policy=new CarrierSyncPolicy(new("HML",true,true,DateTimeOffset.UtcNow.AddMinutes(5),["0001"],[]),TimeProvider.System);
        var motor=new SincronizarTransportadoras(new Discovery(),gateway,store,policy,TimeProvider.System,(_,_)=>Task.FromResult(payload));
        var lifecycle=new CarrierDesktopLifecycle(_=>Task.FromResult(new CarrierDesktopSession(async ct=>{await motor.ExecutarCicloAsync(ct);},(_,_,_,_)=>Task.FromResult(new FugaTES.HostIntegracao.CarrierManualResponse(false,"CARRIER_REQUEST_NOT_AUTHORIZED")))),TimeProvider.System);
        lifecycle.Start();await gateway.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));var pending=store.State!.PendingAttemptId;Assert.NotNull(pending);
        await lifecycle.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(pending,store.State.PendingAttemptId);Assert.True(store.LeaseDisposed);Assert.Equal(0,store.Checkpoints);
        await motor.ExecutarCicloAsync(default);Assert.Equal(1,gateway.Writes);Assert.Equal(pending,store.State.PendingAttemptId);Assert.Equal("INDETERMINATE",store.State.Status);Assert.Equal(0,store.Checkpoints);
    }
    [Fact] public async Task Read_only_desktop_policy_performs_GET_but_never_reserves_or_posts()
    {
        var store=new Memory();var gateway=new Gateway();var payload=new CarrierPayload("0001","Carrier","Carrier Ltd");var policy=new CarrierSyncPolicy(new("HML",true,true,DateTimeOffset.UtcNow.AddMinutes(5),["0001"],[]){WritesEnabled=false},TimeProvider.System);
        var motor=new SincronizarTransportadoras(new Discovery(),gateway,store,policy,TimeProvider.System,(_,_)=>Task.FromResult(payload));
        await motor.ExecutarCicloAsync(default);Assert.Equal(1,gateway.Gets);Assert.Equal(0,gateway.Writes);Assert.Null(store.State!.PendingAttemptId);Assert.Equal("BLOCKED",store.State.Status);
    }
    sealed class Discovery:ISapCarrierDiscovery{public Task<CarrierPage>LerPaginaAsync(CarrierCursor c,CancellationToken ct)=>Task.FromResult(new CarrierPage([],c,true));}
    sealed class Gateway:IGuardianCarrierGateway
    {
        public int Writes,Gets;public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CarrierLookup>ConsultarAsync(string bp,CancellationToken ct){Gets++;return Task.FromResult(new CarrierLookup(CarrierLookupState.Absent));}
        public async Task<CarrierWriteResult>GravarAsync(CarrierWriteAttempt a,CancellationToken ct){Writes++;Entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);throw new InvalidOperationException("NOT_REACHED");}
        public Task<CarrierLookup>LerConfirmacaoAsync(string code,CancellationToken ct)=>throw new InvalidOperationException("NOT_EXPECTED");
    }
    sealed class Memory:ICarrierSyncStore,ICarrierCycleLease
    {
        public CarrierStoredBp? State;public bool LeaseDisposed;public int Checkpoints;public CancellationToken PerdaToken=>default;
        public Task<ICarrierCycleLease?>TentarAdquirirCicloAsync(CancellationToken ct)=>Task.FromResult<ICarrierCycleLease?>(this);
        public Task<CarrierStoredBp?>CarregarEstadoAsync(string bp,CancellationToken ct)=>Task.FromResult(State);
        public Task<CarrierStoredCheckpoint>CarregarCheckpointAsync(CancellationToken ct)=>Task.FromResult(new CarrierStoredCheckpoint(null,null,null));
        public Task<bool>EnfileirarPedidoAsync(Guid id,string bp,string sid,DateTimeOffset at,CancellationToken ct)=>Task.FromResult(true);
        public Task VerificarAtivaAsync(CancellationToken ct)=>Task.CompletedTask;
        public Task<CarrierManualRequest?>TomarPedidoAsync(CancellationToken ct)=>Task.FromResult<CarrierManualRequest?>(null);
        public Task ConcluirPedidoAsync(Guid id,string code,CancellationToken ct)=>Task.CompletedTask;
        public Task IniciarAsync(Guid id,DateTimeOffset at,CancellationToken ct)=>Task.CompletedTask;
        public Task<bool>RegistrarAntesDoWriteAsync(CarrierPendingWrite a,CancellationToken ct){State=new(a.Bp,"",a.GuardianCode,a.ExpectedHash,"WRITE_IN_FLIGHT",a.Action,"WRITE_IN_FLIGHT",a.StartedUtc,null,a.AttemptId);return Task.FromResult(true);}
        public Task ConcluirBpAsync(CarrierStoredBp result,CancellationToken ct){State=result;return Task.CompletedTask;}
        public Task ConfirmarPaginaAsync(CarrierStoredCheckpoint c,CancellationToken ct){Checkpoints++;return Task.CompletedTask;}
        public Task RegistrarEtapaAsync(CarrierStoredStep s,CancellationToken ct)=>Task.CompletedTask;
        public Task HeartbeatAsync(DateTimeOffset a,DateTimeOffset b,CancellationToken ct)=>Task.CompletedTask;
        public Task ConcluirAsync(CarrierStoredCycle c,CancellationToken ct)=>Task.CompletedTask;
        public ValueTask DisposeAsync(){LeaseDisposed=true;return ValueTask.CompletedTask;}
    }
}