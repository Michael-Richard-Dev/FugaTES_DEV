using System.Net;
using System.Windows.Forms;
using FugaTES.WinForms.Toledo;
using FugaTES.Aplicacao.Consultas.RecebimentosToledo;
using FugaTES.Callback.Http;
using FugaTES.Persistencia.Consultas.UnifiedDev;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
namespace FugaTES.Testes;
public sealed class ToledoSapJsonButtonTests
{
 [Fact] public void Existing_grid_keeps_received_JSON_and_retry_and_adds_SAP_JSON()
 {OnSta(()=>{using var form=new FrmRecebimentosToledo(new Empty());var grid=Children(form).OfType<DataGridView>().Single();
  Assert.True(grid.Columns.Contains("json"));Assert.True(grid.Columns.Contains("reenviarSap"));
  Assert.True(grid.Columns.Contains("jsonSap"));Assert.Equal("JSON SAP",grid.Columns["jsonSap"]!.HeaderText);
  Assert.True(grid.Columns["jsonSap"]!.DisplayIndex<grid.Columns["reenviarSap"]!.DisplayIndex);Assert.True(grid.ReadOnly);});}
 [Fact] public async Task SAP_document_proof_resource_is_authenticated_GET_only()
 {
  var builder=DevUnifiedReadApi.CreateReadOnlyHostBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");
  await using var app=builder.Build();var store=new Store();DevUnifiedReadApi.Map(app,store,new string('A',64));await app.StartAsync();
  using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
  Assert.Equal(HttpStatusCode.Unauthorized,(await http.GetAsync("/dev/read/toledo-sap-json")).StatusCode);Assert.Equal(0,store.Calls);
  http.DefaultRequestHeaders.Add("X-R165AL-Read-Key",new string('A',64));
  Assert.Equal(HttpStatusCode.OK,(await http.GetAsync("/dev/read/toledo-sap-json?selector=12345678-1111-2222-3333-444444444444")).StatusCode);
  Assert.Equal("toledo-sap-json",store.Resource);Assert.Equal(1,store.Calls);
  Assert.Equal(HttpStatusCode.MethodNotAllowed,(await http.PostAsync("/dev/read/toledo-sap-json",new StringContent("{}"))).StatusCode);Assert.Equal(1,store.Calls);await app.StopAsync();
 }
 internal static IEnumerable<Control> Children(Control c){foreach(Control x in c.Controls){yield return x;foreach(var child in Children(x))yield return child;}}
 internal static void OnSta(Action action){Exception? error=null;var t=new Thread(()=>{try{action();}catch(Exception e){error=e;}});t.SetApartmentState(ApartmentState.STA);t.Start();Assert.True(t.Join(TimeSpan.FromSeconds(15)));if(error!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();}
 sealed class Empty:IRecebimentosToledoReadRepository
 {public Task<RecebimentosToledoSnapshot> ConsultarRecebimentosAsync(string? b,CancellationToken c)=>Task.FromResult(new RecebimentosToledoSnapshot([],false,DateTimeOffset.UtcNow));public Task<RecebimentoToledoDocumento> ObterJsonRecebidoAsync(Guid id,CancellationToken c)=>throw new NotSupportedException();}
 sealed class Store:IDevUnifiedReadStore
 {public int Calls;public string? Resource;public Task<DevDatabaseIdentity> IdentityAsync(CancellationToken c)=>Task.FromResult(new DevDatabaseIdentity("18/fugatesacmdev",5433,"fugates_dev","fugates_dev_read_app",true,DateTimeOffset.UtcNow));public Task<object> ReadAsync(string r,string? s,CancellationToken c){Calls++;Resource=r;return Task.FromResult<object>(new {source="OFFLINE_TEST"});}}
}
