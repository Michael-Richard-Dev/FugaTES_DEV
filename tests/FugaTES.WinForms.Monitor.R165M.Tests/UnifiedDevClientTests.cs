using System.Net;
using System.Net.Http.Json;
using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.WinForms.Monitor;
using FugaTES.Persistencia.Consultas.UnifiedDev;
using R165AL;
using R165AL.Desktop;
using FugaTES.Aplicacao.Consultas.Monitor;

public sealed class UnifiedDevClientTests
{
    [Fact]
    public async Task Pending_filter_keeps_completed_motor_waiting_for_callback()
    {
        var snapshot=new MonitorSnapshot(true,false,[new MonitorIntegracao{IntegracaoId=Guid.NewGuid(),EstadoMotor="CONCLUIDA",StatusFuncional="AGUARDANDO_CALLBACK"}]);
        var wire=new Wire(new DevReadEnvelope<MonitorSnapshot>(Identity(),snapshot));
        using var client=new PhysicalMonitorClient(new("http://127.0.0.1:18761"),"TEST_READ_KEY",PilotPlan.Create(),DevReadScope.PilotId,wire,unified:true);
        var result=await client.ConsultarAsync(new(){Grupo="PENDENTES"},default);
        Assert.Equal("AGUARDANDO_CALLBACK",Assert.Single(result.Integracoes).StatusFuncional);
    }
    [Fact]
    public async Task Existing_reader_loads_contract_Item_and_persisted_JSON_from_the_private_DEV_API()
    {
        var id=Guid.NewGuid();const string payload="{\"NumeroContrato\":\"0000001147\",\"Item\":\"10\",\"Centro\":\"3011\"}";
        var row=new ContratoAcmLeitura("3011","0000001147",1,DateTime.UtcNow,"T","PENDENTE",id,"recorded-correlation",true){Item="10",PayloadContratoJson=payload};
        var wire=new Wire(new DevReadEnvelope<IReadOnlyList<ContratoAcmLeitura>>(Identity(),[row]));
        using var client=new PhysicalMonitorClient(new("http://127.0.0.1:18761"),"TEST_READ_KEY",PilotPlan.Create(),DevReadScope.PilotId,wire,unified:true);
        var result=await client.ReadContractsAsync("3011",default);var value=Assert.Single(result);
        Assert.Equal("10",value.Item);Assert.Equal(payload,value.PayloadContratoJson);Assert.False(client.IsPilotOnly);
        Assert.True(client.Connection.Connected);Assert.False(client.Configured);Assert.Equal("/dev/read/contracts",wire.Path);
    }
    [Fact]
    public async Task Wrong_cluster_in_contract_read_is_blocked_without_silent_empty_list()
    {
        var wire=new Wire(new DevReadEnvelope<IReadOnlyList<ContratoAcmLeitura>>(Identity() with {Port=5432,Cluster="18/main"},[]));
        using var client=new PhysicalMonitorClient(new("http://127.0.0.1:18761"),"TEST_READ_KEY",PilotPlan.Create(),DevReadScope.PilotId,wire,unified:true);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>client.ReadContractsAsync("3011",default));Assert.False(client.Connection.Connected);
    }
    [Fact]
    public void A_denied_private_file_is_distinguished_from_a_missing_file_without_reading_a_secret()
    {
        Assert.Equal("READ_KEY_ACCESS_DENIED",PrivateReadKeyMetadata.Classify(new UnauthorizedAccessException()));
        Assert.Equal("READ_KEY_UNAVAILABLE",PrivateReadKeyMetadata.Classify(new FileNotFoundException()));
    }
    static DevDatabaseIdentity Identity()=>new("18/fugatesacmdev",5433,"fugates_dev","fugates_acm_hml_app",true,DateTimeOffset.UtcNow);
    sealed class Wire(object value):HttpMessageHandler
    {
        public string? Path;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Assert.Equal(HttpMethod.Get,request.Method);Path=request.RequestUri!.AbsolutePath;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(value)});}
    }
}
