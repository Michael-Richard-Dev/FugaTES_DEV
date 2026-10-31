using System.Net;
using System.Text;
using System.Text.Json;
using FugaTES.Callback.Http;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
public class R57InboundTests
{
 private const string BasicUsername = "SYNTHETIC_R165AH_R57_USER";
 private const string BasicPassword = "SYNTHETIC_R165AH_R57_NOT_A_CREDENTIAL";
 [Fact] public async Task Actual_inbound_handler_retains_item_unit_precision_and_only_accepts_durably()
 {
  IConfiguration config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
  {
   ["SapContractInbound:SecurityMode"]="LoopbackOnly",
   ["SapInbound:BasicAuth:Username"]=BasicUsername,
   ["SapInbound:BasicAuth:Password"]=BasicPassword
  }).Build();
  var store=new Recorder();using var provider=new ServiceCollection().AddSingleton<IConfiguration>(config).AddSingleton<IContratoSapToledoVersaoStore>(store).AddSingleton<ContratoSapToledoCanonicalizer>().AddSingleton<AceitarContratoSapToledoDuravelCasoDeUso>().BuildServiceProvider();
  var context=new DefaultHttpContext{RequestServices=provider};context.Connection.RemoteIpAddress=IPAddress.Loopback;
  context.Request.Headers.Authorization="Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes(BasicUsername+":"+BasicPassword));
  context.Request.ContentType="application/json";context.Request.Body=new MemoryStream(Encoding.UTF8.GetBytes("{\"Contrato\":[{\"NumeroContrato\":\"0000001106\",\"Item\":\"000020\",\"TipoContrato\":\"AC3P\",\"DescricaoTipoContrato\":\"SYNTHETIC-ONLY\",\"IdParceiro\":\"000020000184\",\"Centro\":\"3011\",\"Material\":\"000000000008000001\",\"QuantidadeContrato\":12.345,\"UnidCont\":\"KG\",\"OperacaoContrato\":\"10\",\"StatusContrato\":\"T\"}]}"));context.Response.Body=new MemoryStream();
  await SapContractInboundEndpoint.HandleDurableAsync(context);Assert.Equal(201,context.Response.StatusCode);Assert.Equal("000020",store.Value!.Item);Assert.Equal("KG",store.Value.UnidCont);Assert.Equal(12.345m,store.Value.QuantidadeContrato);Assert.Equal("000020000184",store.Value.IdParceiro);
  context.Response.Body.Position=0;using var response=await JsonDocument.ParseAsync(context.Response.Body);Assert.Equal(17,response.RootElement.GetProperty("versao").GetInt32());Assert.Equal(1,store.Calls);
 }
 sealed class Recorder:IContratoSapToledoVersaoStore
 {
  public ContratoSapToledoSnapshot? Value;public int Calls;
  public Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot s,string f,string p,string c,CancellationToken ct){Value=s;Calls++;return Task.FromResult(new VersaoContratoSapToledo(ResultadoRegistroVersaoContrato.Criada,s.NumeroContrato,17,f,Guid.NewGuid(),s));}
  public Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id,CancellationToken ct)=>throw new NotSupportedException();public Task<bool> TemPredecessorNaoTerminalAsync(string n,long v,CancellationToken ct)=>throw new NotSupportedException();
 }
}
