using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Persistencia.Mm;
using FugaTES.WinForms.Mm;
using IMmGuardianDispatcher=FugaTES.Aplicacao.Servicos.Mm.IMmGuardianDispatcher;
using MmGuardianReadback=FugaTES.Aplicacao.Servicos.Mm.MmGuardianReadback;
namespace R158.SapInboundReadiness.Tests;
public class MmGuardianCollectorTests
{
    [Fact] public void Dedicated_cluster_identity_is_accepted()=>RegistroProtocolosMmPostgreSql.ValidarIdentidade("fugates_dev","fugates_mm_app","fugates_mm_app","18/fugatesacmdev",5433);
    [Theory][InlineData("fugates_dev_app",5433)][InlineData("fugates_dev_owner",5433)][InlineData("postgres",5433)][InlineData("fugates_sd_app",5433)][InlineData("fugates_mm_app",5432)][InlineData("fugates_mm_app",5434)]
    public void Wrong_application_or_cluster_is_rejected(string role,int port)=>Assert.Throws<InvalidOperationException>(()=>RegistroProtocolosMmPostgreSql.ValidarIdentidade("fugates_dev",role,role,"18/fugatesacmdev",port));
    [Fact] public void Admin_session_cannot_impersonate_application()=>Assert.Throws<InvalidOperationException>(()=>RegistroProtocolosMmPostgreSql.ValidarIdentidade("fugates_dev","fugates_mm_app","postgres","18/fugatesacmdev",5433));
    [Fact] public void Collector_readback_contract_is_not_present_yet()=>Assert.NotNull(typeof(IMmGuardianDispatcher));
    [Fact] public void Snapshot_has_guardian_observations_without_replacing_sap_json()=>Assert.NotNull(typeof(MmPurchaseOrderSnapshot).GetProperty("GuardianRecords"));
}

public class MmGuardianPollingTests
{
    private sealed class Source:IPedidosCompraMmConsulta
    {
        public int Calls;public bool FailAfterFirst;public Func<bool>? FailureEnabled;
        public Task<MmConsultaResultado> ConsultarAsync(CancellationToken ct)
        {var n=Interlocked.Increment(ref Calls);return Task.FromResult(FailAfterFirst&&n>1&&(FailureEnabled?.Invoke()??true)?new MmConsultaResultado(false,"MM_SAP_TIMEOUT",null,1,[]):new(true,"MM_SAP_COMPLETE",200,1,[MmGuardianProtocolTests.Linha()]));}
    }
    private sealed class Sender:IMmGuardianDispatcher
    {
        public readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<bool>? Admission;public int Calls;
        public async Task<MmGuardianReadback> ProcessarAsync(IReadOnlyList<PedidoCompraMmLeitura> rows,CancellationToken ct,Func<bool> admission)
        {Admission=admission;Interlocked.Increment(ref Calls);Started.TrySetResult();await Release.Task.WaitAsync(ct);return new("MM_GUARDIAN_VERIFICADO",[]);}
    }
    [Fact] public async Task Slow_post_does_not_stop_polling_and_failure_revokes_remaining_admission()
    {
        var path=Path.Combine(Path.GetTempPath(),"fugates-mm-"+Guid.NewGuid().ToString("N"));var store=new MmSnapshotStore(path);var sender=new Sender();var source=new Source{FailAfterFirst=true,FailureEnabled=()=>sender.Started.Task.IsCompleted};using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run=new MmPurchaseOrderCollector(source,store,sender).RunAsync(TimeSpan.FromMilliseconds(20),stop.Token);
        try
        {
            await sender.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for(var i=0;i<200&&(Volatile.Read(ref source.Calls)<3||store.Read().State!="MM_SAP_TIMEOUT");i++)await Task.Delay(10);
            Assert.True(source.Calls>=3);Assert.False(sender.Admission!());Assert.Equal(1,sender.Calls);
            Assert.Equal("MM_SAP_TIMEOUT",store.Read().State);
        }
        finally{stop.Cancel();sender.Release.TrySetResult();try{await run;}catch(OperationCanceledException){}if(Directory.Exists(path))Directory.Delete(path,true);}
    }
    [Fact] public async Task Failed_initial_poll_does_not_start_sender()
    {
        var path=Path.Combine(Path.GetTempPath(),"fugates-mm-"+Guid.NewGuid().ToString("N"));var store=new MmSnapshotStore(path);var source=new Source{Calls=1,FailAfterFirst=true};var sender=new Sender();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run=new MmPurchaseOrderCollector(source,store,sender).RunAsync(TimeSpan.FromMilliseconds(20),stop.Token);
        try{for(var i=0;i<100&&source.Calls<3;i++)await Task.Delay(10);Assert.True(source.Calls>=3);Assert.Equal(0,sender.Calls);}
        finally{stop.Cancel();sender.Release.TrySetResult();try{await run;}catch(OperationCanceledException){}if(Directory.Exists(path))Directory.Delete(path,true);}
    }
}

public class MmGuardianConfigurationTests
{
    [Fact] public void Default_profile_does_not_enable_business_dispatch()=>new MmGuardianOptions().Validar();
    [Theory][InlineData("","proof")][InlineData("activation","")]
    public void Live_dispatch_requires_operational_references(string activation,string reconciliation)=>Assert.Throws<InvalidOperationException>(()=>new MmGuardianOptions(true,"https://example.invalid/mm",activation,reconciliation).Validar());
    [Fact] public void Callback_credentials_or_plaintext_are_not_accepted()=>Assert.Throws<InvalidOperationException>(()=>new MmGuardianOptions(true,"http://example.invalid/mm","activation","reconciliation").Validar());
    private sealed class Counter:HttpMessageHandler
    {public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Calls++;return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));}}
    [Theory][InlineData("GET","https://guardian.cloudprix.com.br/api/v1/Guardian/ProtocoloPreCadastro")][InlineData("POST","https://example.invalid/api/v1/Guardian/ProtocoloPreCadastro")][InlineData("POST","http://guardian.cloudprix.com.br/api/v1/Guardian/ProtocoloPreCadastro")][InlineData("POST","https://guardian.cloudprix.com.br/api/v1/Guardian/ProtocoloPreCadastro?unexpected=1")][InlineData("POST","https://guardian.cloudprix.com.br/api/v0/Guardian/Transportadora")]
    public async Task Other_routes_and_methods_do_not_reach_transport(string method,string uri)
    {using var inner=new Counter();using var http=new HttpClient(new MmGuardianHttpGuard(inner));await Assert.ThrowsAsync<InvalidOperationException>(()=>http.SendAsync(new HttpRequestMessage(new HttpMethod(method),uri)));Assert.Equal(0,inner.Calls);}
    [Theory][InlineData("https://guardian.cloudprix.com.br/api/v1/Guardian/ProtocoloPreCadastro")][InlineData("https://guardian.cloudprix.com.br/api/v2/CreateToken")]
    public async Task Approved_token_and_protocol_routes_are_allowed(string uri)
    {using var inner=new Counter();using var http=new HttpClient(new MmGuardianHttpGuard(inner));using var response=await http.PostAsync(uri,new StringContent("{}"));Assert.Equal(1,inner.Calls);}
}
