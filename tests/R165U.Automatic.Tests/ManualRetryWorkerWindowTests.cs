using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.HostIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using Xunit;

public sealed class ManualRetryWorkerWindowTests
{
    [Theory][InlineData(true)][InlineData(false)]
    public async Task Window_is_revalidated_and_only_one_scoped_motor_claim_is_executed(bool closeDuringStart)
    {
        var fixture=new AutomaticFixture();var (version,_)=fixture.Add("987700");var clock=new Clock();var calls=new Processor();
        var repo=new RepositorioMotorIntegracaoFake();repo.Semear(new(version.IntegracaoId,"SAP","TOLEDO","SAP_CONTRATO_TOLEDO",version.NumeroContrato,
            version.FingerprintSha256,"OFFLINE_RETRY",EstadoIntegracao.AguardandoReprocessamento,TipoDisparoPendente.Reprocessamento,clock.GetUtcNow(),1,null,null,FaseEnvioIntegracao.ResultadoRecebido,Guid.NewGuid()));
        var motor=new MotorIntegracaoServico(repo,calls,new Detector(),new ValidadorGenericoIntegracao(),new Identity(),new Timeout(),clock,
            new(){LeaseDuration=TimeSpan.FromMinutes(1),LeaseRenewInterval=TimeSpan.FromSeconds(10),RetryBackoff=TimeSpan.FromSeconds(1),AcmPilotNumeroContrato=version.NumeroContrato,AcmPilotCentro="3011",AcmPilotItem="10"},fixture.Versions);
        var approval=new AcmRetryApproval{IntegrationId=version.IntegracaoId,Contract=version.NumeroContrato,Item="10",Homologated=true,ExecutionAuthorized=true,ExclusiveWorker=true,NotBeforeUtc=clock.GetUtcNow().AddMinutes(-1),UntilUtc=clock.GetUtcNow().AddSeconds(5)};
        var store=new Store(clock,approval,closeDuringStart);var worker=new AcmManualRetryWorker(store,motor,clock,globalWorkerEnabled:false);
        var result=await worker.ExecuteApprovedAsync(Guid.NewGuid(),approval,"OFFLINE_WORKER",default);
        Assert.Equal(closeDuringStart?0:1,calls.Calls);Assert.Equal(closeDuringStart?"WINDOW_CLOSED_NO_DISPATCH":"Processado",result);Assert.Equal(1,store.Captures);
        if(!closeDuringStart){Assert.Single(repo.Tentativas);Assert.Equal(2,repo.Tentativas.Single().FencingToken);Assert.Equal(version.IntegracaoId,repo.Tentativas.Single().IntegracaoId);}
    }
    sealed class Clock:TimeProvider{public DateTimeOffset Now=DateTimeOffset.Parse("2026-10-27T20:00:00Z");public override DateTimeOffset GetUtcNow()=>Now;}
    sealed class Processor:IProcessadorExternoIntegracao{public int Calls;public Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor c,CancellationToken ct){Calls++;return Task.FromResult(ResultadoProcessamentoExterno.Sucesso);}}
    sealed class Detector:IDetectorCandidatosIntegracao{public Task<IReadOnlyList<CandidatoIntegracao>> DetectarAsync(CancellationToken ct)=>throw new InvalidOperationException("NO_GLOBAL_WORKER");}
    sealed class Identity:IIdentidadeWorkerProvider{public IdentidadeWorkerIntegracao Obter()=>new("OFFLINE_WORKER");}
    sealed class Timeout:IPoliticaTimeoutProcessamentoIntegracao{public TimeSpan ObterTimeout(ClaimIntegracaoMotor c)=>TimeSpan.FromSeconds(1);}
    sealed class Store(Clock clock,AcmRetryApproval approval,bool closeDuringStart):IAcmManualRetryStore
    {
        public int Captures;
        public Task<AcmRetrySnapshot?> ReadAsync(Guid id,CancellationToken ct)=>throw new InvalidOperationException();
        public Task<AcmRetryAdmission> AdmitAsync(AcmRetryRequest r,AcmRetryActor a,AcmRetryApproval p,DateTimeOffset n,CancellationToken ct)=>throw new InvalidOperationException();
        public Task<AcmRetryWork?> StartAsync(Guid id,AcmRetryApproval a,string worker,DateTimeOffset n,CancellationToken ct)
        {if(closeDuringStart)clock.Now=approval.UntilUtc.AddSeconds(1);return Task.FromResult<AcmRetryWork?>(new(id,a.IntegrationId,1,worker));}
        public Task CompleteAsync(AcmRetryWork w,string result,DateTimeOffset n,CancellationToken ct){Captures++;return Task.CompletedTask;}
        public Task<Guid?> PendingCommandAsync(AcmRetryApproval a,CancellationToken ct)=>throw new InvalidOperationException();
    }
}
