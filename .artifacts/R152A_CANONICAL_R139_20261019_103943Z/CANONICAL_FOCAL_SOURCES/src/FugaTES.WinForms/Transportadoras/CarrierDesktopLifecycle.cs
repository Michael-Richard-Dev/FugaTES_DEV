using System.Security.Principal;
using FugaTES.WinForms.Transportadoras.MasterdataR3;
using FugaTES.HostIntegracao;
using FugaTES.Aplicacao.Transportadoras;
namespace FugaTES.WinForms.Transportadoras;
public sealed record CarrierDesktopStatus(bool Active,bool Cycling,DateTimeOffset? LastQueryUtc,DateTimeOffset? NextQueryUtc,string? Code);
public sealed class CarrierDesktopSession(Func<CancellationToken,Task> cycle,Func<Guid,string,string,CancellationToken,Task<CarrierManualResponse>> manual,Func<ValueTask>? dispose=null, Func<CancellationToken,Task<ResultadoCategoriaMasterdataR3>>? result=null, Func<CancellationToken,Task<IReadOnlyList<CarrierStoredCycle>>>? logs=null):IAsyncDisposable
{
    public Task RunAsync(CancellationToken ct)=>cycle(ct);
    public Task<ResultadoCategoriaMasterdataR3> ResultAsync(CancellationToken ct)=>result?.Invoke(ct)??Task.FromResult(new ResultadoCategoriaMasterdataR3(EstadoMasterdataR3.Bloqueado,0,0,0,0,null,false));
    public Task<IReadOnlyList<CarrierStoredCycle>> LogsAsync(CancellationToken ct)=>logs?.Invoke(ct)??Task.FromResult<IReadOnlyList<CarrierStoredCycle>>([]);
    public Task<CarrierManualResponse> RequestAsync(Guid id,string bp,string sid,CancellationToken ct)=>manual(id,bp,sid,ct);
    public ValueTask DisposeAsync()=>dispose?.Invoke()??ValueTask.CompletedTask;
}
public sealed class CarrierDesktopLifecycle
{
    private readonly Func<CancellationToken,Task<CarrierDesktopSession>> factory;
    private readonly TimeProvider clock;
    private readonly Func<string,CancellationToken,Task<ResultadoCarrierManual>> manualReadOnly;
    public bool ManualReadOnlyAvailable(string bp)=>CarrierManualReadOnlyComposition.Available(bp);
    public Task<ResultadoCarrierManual> ConsultarManualReadOnlyAsync(string bp,CancellationToken ct)
        =>Masterdata.ConsultarReadOnlyAsync(bp,token=>manualReadOnly(bp,token),ct);

    // R122: CREATE unitario. Desabilitado por ausencia: a prontidao e reavaliada a cada
    // chamada e qualquer bloqueio mantem o botao desligado. Nao dispara nada por si.
    public ProntidaoSingleCreateR122 ProntidaoSingleCreate(string bp,bool? journalFisicoComprovado=null)
    {
        try{return SingleCreate?.Readiness(bp)??SingleCreateDesktopGateR122.Avaliar(bp,clock.GetUtcNow(),journalFisicoComprovado:journalFisicoComprovado);}
        catch{return new(false,["SINGLE_CREATE_READINESS_FAILED"]);}
    }
    public SingleCreateDesktopControllerR139? SingleCreate {get;}
    public bool SingleCreateAvailable(string bp)=>ProntidaoSingleCreate(bp).Pronto;
    public OrquestradorCompartilhadoMasterdataR3 Masterdata {get;}
    public MasterdataDesktopReconciliationR28? Reconciliation {get;}
    public event Action? ReconciliationUpdated;
    public MasterdataReconciliationCycleResultR20C? LastReconciliation {get;private set;}
    private readonly HashSet<Task> reconciliationCalls=[];
    public Task<MasterdataReconciliationCycleResultR20C> ConsultarCadastrosAsync(CancellationToken ct=default)
    {
        lock(gate)
        {
            if(stopped)return Task.FromResult(new MasterdataReconciliationCycleResultR20C(MasterdataReconciliationCycleStateR20C.AppClosed,"APP_CLOSED",null,0,0,0,0,[]));
            var task=ConsultarCadastrosCoreAsync(ct);reconciliationCalls.Add(task);
            _=task.ContinueWith(done=>{lock(gate)reconciliationCalls.Remove(done);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            return task;
        }
    }
    private async Task<MasterdataReconciliationCycleResultR20C> ConsultarCadastrosCoreAsync(CancellationToken ct)
    {
        if(Reconciliation is null)return new(MasterdataReconciliationCycleStateR20C.Blocked,"MASTERDATA_NOT_WIRED",null,0,0,0,0,[]);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,cancellation.Token);
        var result=await Reconciliation.ExecuteAsync(linked.Token).ConfigureAwait(false);
        LastReconciliation=result;ReconciliationUpdated?.Invoke();return result;
    }
    public CarrierDesktopLifecycle(Func<CancellationToken,Task<CarrierDesktopSession>> factory,TimeProvider clock,Func<string,CancellationToken,Task<ResultadoCarrierManual>>? manualReadOnly=null,
        Func<OrquestradorCompartilhadoMasterdataR3,MasterdataDesktopReconciliationR28>? reconciliationFactory=null,
        Func<OrquestradorCompartilhadoMasterdataR3,SingleCreateDesktopControllerR139>? singleCreateFactory=null)
    {
        this.factory=factory;this.clock=clock;this.manualReadOnly=manualReadOnly??CarrierManualReadOnlyComposition.ConsultarAsync;
        Masterdata=new([new AcaoCategoriaDelegadaMasterdataR3(CategoriaMasterdataR3.Transportadoras,true,true,RunCarrierAsync)],tempo:clock,runtimeAutorizado:()=>Snapshot.Active,singleCreateAutorizado:()=>SingleCreate?.Ready??Snapshot.Active);
        SingleCreate=singleCreateFactory?.Invoke(Masterdata);
        Reconciliation=reconciliationFactory?.Invoke(Masterdata);
    }
    private async Task<ResultadoCategoriaMasterdataR3> RunCarrierAsync(CancellationToken ct)
    {
        CarrierDesktopSession session;
        lock(gate)session=live??throw new Runtime.DesktopStartupException("CARRIER_REQUEST_NOT_READY");
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,cancellation.Token);
        lock(gate)state=state with {Cycling=true,LastQueryUtc=clock.GetUtcNow()};
        try {await session.RunAsync(linked.Token).ConfigureAwait(false);return await session.ResultAsync(linked.Token).ConfigureAwait(false);}
        finally {lock(gate)state=state with {Cycling=false};}
    }
    private readonly object gate=new();
    private readonly CancellationTokenSource cancellation=new();
    private readonly SemaphoreSlim manualGate=new(1,1);
    private Task? running;
    private CarrierDesktopSession? live;
    private bool stopped;
    private CarrierDesktopStatus state=new(false,false,null,null,"CARRIER_DESKTOP_NOT_STARTED");
    public CarrierDesktopStatus Snapshot{get{lock(gate)return state;}}
    public Task Completion{get{lock(gate)return running??Task.CompletedTask;}}
    public void Start()
    {
        lock(gate)
        {
            if(running is not null||stopped)return;
            if(SingleCreate?.UnitaryOnly==true){state=state with{Code="SINGLE_CREATE_MANUAL_ONLY_NO_POLL_LOOP"};return;}
            state=state with {Code="CARRIER_DESKTOP_VALIDATING"};
            running=Task.Run(RunAsync);
        }
    }
    private async Task RunAsync()
    {
        CarrierDesktopSession? session=null;
        try
        {
            if(Reconciliation is not null)
            {
                if(!Reconciliation.CanActivate)throw new Runtime.DesktopStartupException(MasterdataDesktopReconciliationR28.Blocker);
                var origin=clock.GetUtcNow();
                lock(gate){if(stopped)return;state=state with{Active=true,NextQueryUtc=origin,Code=null};}
                await new CarrierPollLoop(async token=>
                {
                    lock(gate)state=state with{Cycling=true,LastQueryUtc=clock.GetUtcNow()};
                    try{await ConsultarCadastrosAsync(token).ConfigureAwait(false);}
                    finally{lock(gate)state=state with{Cycling=false,NextQueryUtc=stopped?null:clock.GetUtcNow()+CarrierPollLoop.Interval};}
                },clock).RunAsync(cancellation.Token).ConfigureAwait(false);
                return;
            }
            session=await factory(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            var start=clock.GetUtcNow();
            lock(gate){if(stopped)return;live=session;state=state with {Active=true,NextQueryUtc=start,Code=null};}
            await new CarrierPollLoop(async ct=>
            {
                try{await Masterdata.ExecutarAsync(OrigemCicloMasterdataR3.Automatica,ct).ConfigureAwait(false);}
                finally
                {
                    var ticks=TimeSpan.FromSeconds(30).Ticks;
                    var next=start.AddTicks(((clock.GetUtcNow()-start).Ticks/ticks+1)*ticks);
                    lock(gate)state=state with {NextQueryUtc=stopped?null:next};
                }
            },clock,code=>{lock(gate)state=state with {Code=code};}).RunAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested){}
        catch(Exception ex)
        {
            var code=ex is Runtime.DesktopStartupException known?known.Code:"CARRIER_DESKTOP_STARTUP_FAILED";
            try{CarrierPersistencePolicy.ValidateCode(code);}catch{code="CARRIER_DESKTOP_STARTUP_FAILED";}
            lock(gate)state=state with {Code=code};
        }
        finally
        {
            lock(gate){live=null;state=state with {Active=false,Cycling=false,NextQueryUtc=null};}
            if(stopped)await Masterdata.EncerrarAsync().ConfigureAwait(false);
            else await Masterdata.AguardarOciosidadeAsync().ConfigureAwait(false);
            await manualGate.WaitAsync().ConfigureAwait(false);
            try{if(session is not null)await session.DisposeAsync().ConfigureAwait(false);}
            catch{lock(gate)state=state with {Code="CARRIER_DESKTOP_DISPOSE_FAILED"};}
            finally{manualGate.Release();}
        }
    }
    public async Task StopAsync()
    {
        Task task;Task[] pendingReads;
        lock(gate){pendingReads=reconciliationCalls.ToArray();stopped=true;state=state with {Active=false,NextQueryUtc=null};task=running??Task.CompletedTask;}
        cancellation.Cancel();Masterdata.Encerrar();
        await task.ConfigureAwait(false);
        await Masterdata.EncerrarAsync().ConfigureAwait(false);
        if(SingleCreate is not null)await SingleCreate.DisposeAsync().ConfigureAwait(false);
        try{await Task.WhenAll(pendingReads).ConfigureAwait(false);}catch(OperationCanceledException) when(cancellation.IsCancellationRequested){}
    }
    public Task<CarrierManualResult> RequestMasterdataAsync(Guid id,string bp,CancellationToken ct)
        =>Masterdata.SolicitarAsync(id,token=>RequestAsync(id,bp,token),ct);
    public async Task<IReadOnlyList<CarrierStoredCycle>> ReadLogsAsync(CancellationToken ct)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,cancellation.Token);
        await manualGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try {CarrierDesktopSession? session;lock(gate)session=state.Active&&!stopped?live:null;
            if(session is null)throw new Runtime.DesktopStartupException("CARRIER_REQUEST_NOT_READY");
            return await session.LogsAsync(linked.Token).ConfigureAwait(false);
        } finally{manualGate.Release();}
    }
    public async Task<CarrierManualResult> RequestAsync(Guid id,string bp,CancellationToken ct)
    {
        if(id==Guid.Empty)throw new ArgumentException("CARRIER_REQUEST_INVALID");CarrierPersistencePolicy.ValidateBp(bp);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,cancellation.Token);
        await manualGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            CarrierDesktopSession? session;lock(gate)session=state.Active&&!stopped?live:null;
            if(session is null)return new(id,false,"CARRIER_REQUEST_NOT_READY");
            var sid=WindowsIdentity.GetCurrent().User?.Value;
            if(sid is null)return new(id,false,"CARRIER_REQUEST_NOT_AUTHORIZED");
            var result=await session.RequestAsync(id,bp,sid,linked.Token).ConfigureAwait(false);
            return new(id,result.Accepted,result.Code);
        }
        catch(OperationCanceledException){return CarrierManualResult.Unknown(id);}
        catch{return CarrierManualResult.Unknown(id);}
        finally{manualGate.Release();}
    }
}
public sealed class CarrierDesktopManualClient(CarrierDesktopLifecycle? lifecycle):ICarrierSyncManualCommandClient
{
    public Task<CarrierManualResult> RequestAsync(Guid id,string bp,CancellationToken ct)=>lifecycle?.RequestAsync(id,bp,ct)??Task.FromResult(new CarrierManualResult(id,false,"CARRIER_REQUEST_NOT_READY"));
}