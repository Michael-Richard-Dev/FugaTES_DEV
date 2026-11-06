using FugaTES.HostIntegracao;
using FugaTES.HostIntegracao.Mm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
namespace FugaTES.Testes;
public sealed class MmLinuxHostTests
{
 [Fact] public void Inert_mode_does_not_resolve_providers_or_register_other_workers()
 {
  var builder=Host.CreateApplicationBuilder([]);builder.Configuration.Sources.Clear();
  builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{{"HostIntegracao:Mode","MmPurchaseOrders"}});
  HostIntegracaoComposition.Configurar(builder);using var host=builder.Build();
  var workers=host.Services.GetServices<IHostedService>().ToArray();Assert.Single(workers);Assert.IsType<MmLinuxWorker>(workers[0]);
 }
 [Fact] public void Default_configuration_is_inert_and_has_fixed_scope()
 {var options=new MmLinuxOptions();Assert.False(options.Enabled);Assert.Equal(180,options.IntervalSeconds);Assert.Equal(new DateOnly(2026,11,1),options.CreationCutoff);options.Validate();}
 [Theory][InlineData(5432,"fugates_mm_app")][InlineData(5434,"fugates_mm_app")][InlineData(5433,"postgres")][InlineData(5433,"fugates_dev_app")][InlineData(5433,"fugates_acm_hml_app")]
 public void Wrong_identity_or_cluster_is_refused_even_when_disabled(int port,string role)
 {Assert.Throws<InvalidOperationException>(()=>new MmLinuxOptions{Port=port,Role=role}.Validate());}
 [Theory][InlineData(30)][InlineData(90)][InlineData(181)]
 public void Unapproved_poll_interval_is_refused(int seconds)=>Assert.Throws<InvalidOperationException>(()=>new MmLinuxOptions{IntervalSeconds=seconds}.Validate());
 [Fact] public void Enabled_mode_requires_cutover_approval_callback_and_private_state()
 {Assert.Throws<InvalidOperationException>(()=>new MmLinuxOptions{Enabled=true}.Validate());}
 [Theory][InlineData("carrier-hml-sap-user")][InlineData("carrier-hml-sap-password")][InlineData("carrier-hml-postgres-password")][InlineData("carrier-hml-guardian-email")][InlineData("carrier-hml-guardian-password")][InlineData("carrier-hml-guardian-signature")]
 public void Group_read_credential_only_in_exact_MM_systemd_scope(string name)
 {
  var dir="/run/credentials/fugates-mm-purchase-orders.service";var owner=UnixFileMode.UserRead|UnixFileMode.UserExecute;
  Assert.True(CarrierSystemdCredentials.IsApprovedMmRuntimeDirectory(name,dir,dir,owner,owner,false));
  Assert.False(CarrierSystemdCredentials.IsApprovedMmRuntimeDirectory(name,dir,"/tmp",owner,owner,false));
  Assert.False(CarrierSystemdCredentials.IsApprovedMmRuntimeDirectory(name,"/tmp",dir,owner,owner,false));
  Assert.False(CarrierSystemdCredentials.IsApprovedMmRuntimeDirectory(name,dir,dir,owner,owner,true));
  Assert.False(CarrierSystemdCredentials.IsApprovedMmRuntimeDirectory(name,dir,dir,owner,owner|UnixFileMode.OtherWrite,false));
 }
 [Fact] public void Unknown_credential_not_approved()
 {var d="/run/credentials/fugates-mm-purchase-orders.service";Assert.False(CarrierSystemdCredentials.IsApprovedMmRuntimeDirectory("unrelated",d,d,UnixFileMode.UserRead|UnixFileMode.UserExecute,0,false));}
}
