using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
using FugaTES.HostIntegracao;
using FugaTES.HostIntegracao.Mm;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Registry=R158.SapInboundReadiness.Tests.MmGuardianDispatchTests.Registry;
using Gateway=R158.SapInboundReadiness.Tests.MmGuardianDispatchTests.Gateway;
using Fixture=R158.SapInboundReadiness.Tests.MmGuardianProtocolTests;
namespace FugaTES.Testes;
public sealed class MmVmSafetyTests
{
 [Theory][InlineData(false)][InlineData(true)]
 public async Task Lost_session_lock_blocks_POST_before_or_after_reservation(bool afterReserve)
 {
  var db=new Registry();var gateway=new Gateway();var checks=0;
  var sender=new EnvioAutomaticoMm(db,gateway,"https://example.invalid/mm",_=>Task.FromResult(afterReserve&&++checks==1));
  await sender.ProcessarAsync([Fixture.Linha()],default);
  Assert.Equal(0,gateway.Calls);Assert.Equal(afterReserve?1:0,db.Rows.Count);
  if(afterReserve)Assert.Equal(EstadoProtocoloMm.Indeterminado,Assert.Single(db.Rows.Values).Estado);
 }
 [Fact] public async Task Existing_five_created_and_161_refused_keys_survive_restart_without_any_dispatch()
 {
  var db=new Registry();var gateway=new Gateway();var rows=Enumerable.Range(1,166).Select(i=>Fixture.Linha(pedido:(4500000000L+i).ToString())).ToArray();
  for(var i=0;i<rows.Length;i++)
  {
   var row=rows[i];var key=MapeadorProtocoloMm.ChaveNatural(row);var created=i<5;
   db.Rows[key]=new(Guid.NewGuid(),key,row.Pedido,row.Item,row.Centro,"{}","HISTORICAL_TEST",created?EstadoProtocoloMm.Criado:EstadoProtocoloMm.Recusado,created?200:400,created?13L+i:null,"preserved",DateTimeOffset.UtcNow);
  }
  var before=db.Rows.ToDictionary(x=>x.Key,x=>x.Value);
  for(var restart=0;restart<2;restart++)await new EnvioAutomaticoMm(db,gateway,"https://example.invalid/mm",_=>Task.FromResult(true)).ProcessarAsync(rows,default);
  Assert.Equal(0,gateway.Calls);Assert.Equal(166,db.Rows.Count);foreach(var old in before)Assert.Equal(old.Value,db.Rows[old.Key]);
 }
 [Fact] public async Task Shutdown_after_external_success_still_closes_the_same_reservation()
 {
  var db=new Registry();using var stop=new CancellationTokenSource();var gateway=new Gateway{Handle=_=>{stop.Cancel();return Task.FromResult(new ResultadoProtocoloMm(EstadoProtocoloMm.Criado,200,111,"ok"));}};
  await new EnvioAutomaticoMm(db,gateway,"https://example.invalid/mm",_=>Task.FromResult(true)).ProcessarAsync([Fixture.Linha()],stop.Token);
  Assert.Equal(1,gateway.Calls);Assert.Equal(EstadoProtocoloMm.Criado,Assert.Single(db.Rows.Values).Estado);
 }
 [Fact] public async Task Inert_host_can_start_and_stop_without_database_secret_or_network()
 {
  var b=Host.CreateApplicationBuilder([]);b.Configuration.Sources.Clear();b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{{"HostIntegracao:Mode","MmPurchaseOrders"}});
  HostIntegracaoComposition.Configurar(b);using var host=b.Build();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(3));await host.StartAsync(stop.Token);await host.StopAsync(stop.Token);
 }
 [Theory][InlineData("2026-10-31",false)][InlineData("2026-11-01",true)][InlineData("2026-11-02",true)][InlineData(null,false)]
 public void Admission_uses_fixed_proven_creation_date(string? date,bool expected)
 {var row=Fixture.Linha() with{DataCriacao=date is null?null:DateOnly.ParseExact(date,"yyyy-MM-dd")};Assert.Equal(expected,MmCollectionPolicy.Eligible(row));Assert.Equal(new DateOnly(2026,11,1),MmCollectionPolicy.CreationCutoff);}
}
