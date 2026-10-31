using System.Text;
using FugaTES.Callback.Http;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace FugaTES.Testes;
public sealed class ToledoUnifiedSafetyRedTests
{
 [Fact] public void Enabled_Toledo_never_borrows_the_ambient_legacy_datasource()
 {
  var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
   ["ToledoInbound:Enabled"]="true",["ConnectionStrings:FugaTES"]="Host=127.0.0.1;Port=5432;Database=legacy;Username=legacy"}).Build();
  Assert.Throws<InvalidOperationException>(()=>ToledoCallbackComposition.Configure(new ServiceCollection(),config,"PostgreSql"));
 }
 [Fact] public async Task Missing_source_authentication_never_persists_or_ACKs_a_Toledo_document()
 {
  var store=new Store();var services=new ServiceCollection();services.AddSingleton<IRegistroPesagemToledoStore>(store);services.AddScoped<RegistrarPesagemToledoCasoDeUso>();
  using var provider=services.BuildServiceProvider();var response=await ToledoCallbackTests.Post(ToledoCallbackTests.ValidJson,provider);
  Assert.Equal(503,response.Status);Assert.Contains("AUTHENTICATION",response.Body);Assert.Equal(0,store.Calls);
 }
 sealed class Store:IRegistroPesagemToledoStore
 {public int Calls;public Task<ReciboPesagemToledo> RegistrarAsync(PesagemToledoRecebida d,string f,CancellationToken ct){Calls++;return Task.FromResult(new ReciboPesagemToledo(Guid.NewGuid(),DateTimeOffset.UtcNow,false));}}
}
