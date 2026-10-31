using FugaTES.Aplicacao.Configuracao;
using FugaTES.Callback.Http;
using FugaTES.HostIntegracao;
using FugaTES.Persistencia.MotorIntegracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

public sealed class GuardianUnifiedDevBindingTests
{
    [Fact] public void Existing_receiver_and_worker_use_distinct_identities_on_same_DEV_and_only_one_worker()
    {
        var receiverConfig=R165AGInboundWindowTests.Config(new(){
            ["ConnectionStrings:FugaTES"]="Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_sap_inbound_app;Search Path=fugates;Passfile=/synthetic/inbound/pgpass;Enlist=false",
            ["FUGATES_INTEGRATION_WORKER_ENABLED"]="false"});
        var receiver=new ServiceCollection();receiver.AddSingleton<IConfiguration>(receiverConfig);
        Assert.True(InboundHttpHostComposition.Configurar(receiver,receiverConfig,false).SapProductive);
        Assert.Empty(receiver.Where(s=>s.ServiceType==typeof(IHostedService)));
        var workerConfig=new ConfigurationBuilder().AddConfiguration(receiverConfig).AddInMemoryCollection(new Dictionary<string,string?>{
            ["SapContractInbound:ProductiveEnabled"]="false",
            ["SapContractInbound:AutomaticProcessingEnabled"]="false",
            ["ConnectionStrings:FugaTES"]="Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_acm_hml_app;Search Path=fugates;Passfile=/synthetic/guardian/pgpass;Enlist=false",
            ["FUGATES_INTEGRATION_WORKER_ENABLED"]="true"}).Build();
        var worker=new ServiceCollection();worker.AddSingleton<IConfiguration>(workerConfig);
        HostIntegracaoComposition.ConfigurarServicos(worker,workerConfig);
        Assert.Equal(typeof(HospedagemIntegracaoWorker),Assert.Single(worker.Where(s=>s.ServiceType==typeof(IHostedService))).ImplementationType);
        using var receiverProvider=receiver.BuildServiceProvider();using var receiverScope=receiverProvider.CreateScope();
        using var workerProvider=worker.BuildServiceProvider();using var workerScope=workerProvider.CreateScope();
        var receiverDs=new NpgsqlConnectionStringBuilder(receiverScope.ServiceProvider.GetRequiredService<NpgsqlDataSource>().ConnectionString);
        var workerDs=new NpgsqlConnectionStringBuilder(workerScope.ServiceProvider.GetRequiredService<NpgsqlDataSource>().ConnectionString);
        Assert.Equal("fugates_sap_inbound_app",receiverDs.Username);Assert.Equal("fugates_acm_hml_app",workerDs.Username);
        Assert.Equal(5433,receiverDs.Port);Assert.Equal(5433,workerDs.Port);Assert.Equal(receiverDs.Database,workerDs.Database);
        Assert.NotEqual(receiverDs.Passfile,workerDs.Passfile);Assert.False(workerDs.Enlist);
        var incoming=receiverScope.ServiceProvider.GetRequiredService<IOptions<MotorIntegracaoOptions>>().Value.AcmInboundWindow;
        var outgoing=workerScope.ServiceProvider.GetRequiredService<IOptions<MotorIntegracaoOptions>>().Value.AcmInboundWindow;
        Assert.Equal(incoming.WindowId,outgoing.WindowId);Assert.Equal(incoming.CorrelationPrefix,outgoing.CorrelationPrefix);
        Assert.Equal(incoming.NotBeforeUtc,outgoing.NotBeforeUtc);Assert.Equal(incoming.UntilUtc,outgoing.UntilUtc);
        Assert.Equal(incoming.Item,outgoing.Item);Assert.Equal(incoming.AllowedSapPartners,outgoing.AllowedSapPartners);
        // Resolving a datasource is not opening it. No host/worker is started by this test.
    }
    [Fact] public void Guardian_worker_candidate_is_inert_and_canonical_and_contains_no_credentials()
    {
        var config=new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory,"configuration/guardian-worker.inert.json")).Build();
        Assert.False(config.GetValue<bool>("FUGATES_INTEGRATION_WORKER_ENABLED"));
        Assert.False(config.GetValue<bool>("SapContractInbound:ProductiveEnabled"));
        Assert.False(config.GetValue<bool>("AcmR57:Normal:ExecutionAuthorized"));
        Assert.False(config.GetValue<bool>("MotorIntegracao:AcmInboundWindow:Enabled"));
        Assert.False(config.GetValue<bool>("AcmProtocolCommand:Enabled"));
        Assert.Null(config["Guardian:Password"]);Assert.Null(config["Guardian:Email"]);Assert.Null(config["Guardian:Signature"]);
        var cs=new NpgsqlConnectionStringBuilder(config.GetConnectionString("FugaTES"));
        Assert.Equal("fugates_acm_hml_app",cs.Username);Assert.Equal(5433,cs.Port);Assert.Equal("fugates_dev",cs.Database);
        Assert.Equal("fugates",cs.SearchPath);Assert.False(cs.Enlist);Assert.True(NpgsqlDataSourceFactory.HasExplicitAcmPassfile(cs));
    }
}
