using System.Net;
using System.Text;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Integracao.Guardian;
using FugaTES.WinForms;
namespace FugaTES.Testes;
public sealed class MmRetryDeliveryTests
{
 [Fact] public async Task Guardian400_retains_safe_cause_without_exposing_secrets()
 {
  using var http=new HttpClient(new Wire());var sender=new GuardianProtocoloMmClient(http,_=>Task.FromResult("SECRET_TOKEN_123"),TimeSpan.FromSeconds(2));
  var outcome=await sender.EnviarAsync(Encoding.UTF8.GetBytes("{}"),default);
  Assert.Equal(EstadoProtocoloMm.Recusado,outcome.Estado);Assert.Contains("ITEM_NOT_FOUND",outcome.Mensagem);Assert.DoesNotContain("SECRET_TOKEN_123",outcome.Mensagem);
 }
 [Fact] public void MM_grid_contains_retry_action_next_to_existing_JSON_actions()
 {
  ToledoSapJsonButtonTests.OnSta(()=>{
   using var form=new FrmPedidosCompraMm(()=>new(),()=>{});form.Show();Application.DoEvents();
   var grid=ToledoSapJsonButtonTests.Children(form).OfType<DataGridView>().Single();
   Assert.Contains(grid.Columns.Cast<DataGridViewColumn>(),c=>c.Name=="reenviarGuardianMm");
   Assert.Contains(grid.Columns.Cast<DataGridViewColumn>(),c=>c.Name=="jsonGuardianMm");form.Close();
  });
 }
 sealed class Wire:HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest){Content=new StringContent("{\"code\":\"E_ITEM_NOT_FOUND\",\"token\":\"SECRET_TOKEN_123\"}")});}
}
