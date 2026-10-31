using FugaTES.WinForms;
using FugaTES.WinForms.Transportadoras;
using FugaTES.Aplicacao.Consultas.ContratosAcm;
public class R57UiTests
{
 [Fact] public void Offline_execution_screen_has_distinct_stages_and_no_send_actions()
 {
  Exception? error=null;var thread=new Thread(()=>{try{using var form=new FrmContratosAcm(new ContratosAcmConsulta(new NeverRead()),execucaoR57:true);var controls=All(form).ToArray();var tabs=Assert.Single(controls.OfType<TabControl>());Assert.Equal(new[]{"Execução / Correlação","XML SOAP","JSON REST mínimo"},tabs.TabPages.Cast<TabPage>().Select(t=>t.Text));Assert.All(controls.OfType<Button>().Where(b=>b.Text.StartsWith("Enviar")),b=>Assert.False(b.Enabled));var grid=Assert.Single(controls.OfType<DataGridView>());Assert.Equal(6,grid.Rows.Count);Assert.Equal("NÃO RECEBIDO NESTA SESSÃO",grid.Rows[0].Cells[1].Value);Assert.Contains("NÃO COMPOSTO",Assert.Single(controls.OfType<TextBox>(),t=>t.AccessibleName=="XML SOAP").Text);}catch(Exception ex){error=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(20)));if(error is not null)throw error;
 }
 [Fact] public async Task R57_session_does_not_load_credentials_or_start_background_work()
 {
  var path=Path.Combine(Path.GetTempPath(),"R57-offline-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
  try{File.WriteAllText(Path.Combine(path,"Acm.execution.R57.json"),"{}");using var session=SafeDemoReadOnlySessionR50.Create(path);Assert.Null(session.GuardianRead);Assert.False(session.CanQuery("20000014"));var r=await session.ConsultarSapAsync("20000014");Assert.Equal("R50_BP_NOT_AUTHORIZED",r.State);Assert.Null(r.HttpStatus);}finally{File.Delete(Path.Combine(path,"Acm.execution.R57.json"));Directory.Delete(path);}
 }
 static IEnumerable<Control> All(Control c){yield return c;foreach(Control child in c.Controls)foreach(var x in All(child))yield return x;}
 sealed class NeverRead:IContratosAcmReadRepository {public Task<IReadOnlyList<ContratoAcmLeitura>> ConsultarAsync(string c,CancellationToken ct)=>throw new InvalidOperationException("No operational DB access in UI test");}
}
