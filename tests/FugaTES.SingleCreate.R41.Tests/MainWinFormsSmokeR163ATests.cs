using System.Reflection;
using System.Windows.Forms;
using FugaTES.WinForms;
using FugaTES.WinForms.Transportadoras.MasterdataR3;
namespace FugaTES.SingleCreate.R41.Tests;
public class MainWinFormsSmokeR163ATests
{
 static void Sta(Action action)
 {
  Exception? failure=null;var thread=new Thread(()=>{try{action();}catch(Exception e){failure=e;}}){IsBackground=true};
  thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(12)),"UI smoke timeout");if(failure is not null)throw failure;
 }
 static IEnumerable<Control> Controls(Control c)=>c.Controls.Cast<Control>().SelectMany(x=>new[]{x}.Concat(Controls(x)));
 [Fact] public void Carrier_form_starts_without_io_or_armed_create()=>Sta(()=>
 {
  using var form=new FrmTransportadoraMasterdata();form.Show();Application.DoEvents();
  var controls=Controls(form).ToArray();var bp=Assert.Single(controls.OfType<TextBox>(),c=>c.Name=="BusinessPartner");
  Assert.Equal("",bp.Text);Assert.False(Assert.Single(controls.OfType<Button>(),c=>c.Name=="Cadastrar").Enabled);
  Assert.NotNull(Assert.Single(controls.OfType<DataGridView>(),c=>c.Name=="JournalMasterdata"));
  form.Close();Application.DoEvents();
 });
 [Fact] public void Main_integrations_menu_opens_integrated_carrier_form()=>Sta(()=>
 {
  using var menu=new FrmIntegracoes();menu.Show();Application.DoEvents();
  var button=(Button)typeof(FrmIntegracoes).GetField("transportadoras",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(menu)!;
  bool observed=false;var until=DateTime.UtcNow.AddSeconds(5);
  using var timer=new System.Windows.Forms.Timer{Interval=100};
  timer.Tick+=(_,_)=>
  {
   var child=Application.OpenForms.OfType<FrmTransportadoraMasterdata>().SingleOrDefault();
   if(child is not null){observed=true;timer.Stop();Assert.False(Controls(child).OfType<Button>().Single(x=>x.Name=="Cadastrar").Enabled);child.Close();}
   else if(DateTime.UtcNow>=until){timer.Stop();foreach(Form f in Application.OpenForms.Cast<Form>().ToArray())if(f!=menu)f.Close();}
  };
  timer.Start();button.PerformClick();timer.Stop();Assert.True(observed,"Main menu must open the integrated carrier form, not CarrierSync legacy monitor");menu.Close();Application.DoEvents();
 });
}
