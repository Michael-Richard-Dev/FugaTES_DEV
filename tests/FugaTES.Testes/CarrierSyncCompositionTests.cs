using FugaTES.HostIntegracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
namespace FugaTES.Testes;
public class CarrierSyncCompositionTests
{
    static IConfiguration Config(params (string,string)[] items)=>new ConfigurationBuilder().AddInMemoryCollection(items.Select(x=>new KeyValuePair<string,string?>(x.Item1,x.Item2))).Build();
    [Fact] public void Flag_off_does_not_register_worker(){var services=new ServiceCollection();CarrierSyncComposition.ConfigurarServicos(services,Config());Assert.DoesNotContain(services,x=>x.ServiceType==typeof(IHostedService));}
    [Fact] public void Exclusive_flag_registers_only_carrier_worker(){var services=new ServiceCollection();CarrierSyncComposition.ConfigurarServicos(services,Config(("FUGATES_CARRIER_SYNC_ENABLED","true"),("Carrier:Environment","HML"),("Carrier:ServiceProfile","C:\\safe\\profile.json")));Assert.Contains(services,x=>x.ServiceType==typeof(IHostedService)&&x.ImplementationType==typeof(CarrierSyncWorker));Assert.DoesNotContain(services,x=>x.ImplementationType==typeof(HospedagemIntegracaoWorker));}
    [Fact] public void General_worker_is_rejected_in_exclusive_mode(){Assert.Throws<InvalidOperationException>(()=>CarrierSyncComposition.ConfigurarServicos(new ServiceCollection(),Config(("FUGATES_INTEGRATION_WORKER_ENABLED","true"))));}
    [Fact] public void Enabled_non_hml_is_rejected(){Assert.Throws<InvalidOperationException>(()=>CarrierSyncComposition.ConfigurarServicos(new ServiceCollection(),Config(("FUGATES_CARRIER_SYNC_ENABLED","true"),("Carrier:Environment","PRD"))));}
}