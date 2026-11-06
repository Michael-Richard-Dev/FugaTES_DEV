using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.WinForms;
using FugaTES.WinForms.Mm;
namespace FugaTES.Testes;
public sealed class MmVmMonitorTests
{
 [Fact] public void Remote_screen_reads_asynchronously_and_identifies_VM_without_local_worker()
 {
  ToledoSapJsonButtonTests.OnSta(()=>
  {
   var calls=0;var pending=new TaskCompletionSource<MmRemoteReadback>();
   using var form=new FrmPedidosCompraMm(ct=>{calls++;return pending.Task.WaitAsync(ct);});
   form.Show();Application.DoEvents();Assert.Equal(1,calls);
   form.AtualizarLeitura();Application.DoEvents();Assert.Equal(1,calls);
   pending.SetResult(new(new(){RuntimeMode="VM",Host="vm-fugatesbio",State="MM_SAP_COMPLETE",LastSuccessUtc=DateTimeOffset.UtcNow},"VM_SNAPSHOT_NOT_JOURNAL",DateTimeOffset.UtcNow,true));
   var until=DateTime.UtcNow.AddSeconds(3);while(DateTime.UtcNow<until&&!ToledoSapJsonButtonTests.Children(form).OfType<Label>().Any(x=>x.Text.Contains("vm-fugatesbio"))){Application.DoEvents();Thread.Sleep(5);}
   Assert.Contains(ToledoSapJsonButtonTests.Children(form).OfType<Label>(),x=>x.Text.Contains("vm-fugatesbio")&&x.Text.Contains("3 minutos"));
   Assert.Contains(ToledoSapJsonButtonTests.Children(form).OfType<DataGridView>().Single().Columns.Cast<DataGridViewColumn>(),x=>x.Name=="jsonGuardianMm");
   form.Close();
  });
 }
 [Fact] public void Historical_results_remain_accessible_when_no_order_passes_new_creation_cutoff()
 {
  ToledoSapJsonButtonTests.OnSta(()=>
  {
   using var form=new FrmPedidosCompraMm(()=>new(){State="MM_SAP_COMPLETE",LastSuccessUtc=DateTimeOffset.UtcNow},()=>{});
   form.Show();Application.DoEvents();
   Assert.Contains(ToledoSapJsonButtonTests.Children(form).OfType<Button>(),x=>x.Name=="historicoGuardianMm");
   var method=typeof(FrmPedidosCompraMm).GetMethod("CriarDialogoHistorico");Assert.NotNull(method);
   var stored=new RegistroProtocoloMm(Guid.NewGuid(),"3000/4500000001/10","4500000001","10","3011","{\"id\":13}","HISTORICAL",EstadoProtocoloMm.Criado,200,13,null,DateTimeOffset.UtcNow);
   using var dialog=(Form)method.Invoke(null,[new[]{stored}])!;
   var grid=ToledoSapJsonButtonTests.Children(dialog).OfType<DataGridView>().Single();dialog.Show();Application.DoEvents();
   Assert.Equal(1,grid.Rows.Count);Assert.Contains(grid.Columns.Cast<DataGridViewColumn>(),x=>x.Name=="jsonHistoricoMm");
   dialog.Close();form.Close();
  });
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void Shared_Desktop_release_uses_VM_only_after_explicit_cutover_profile(bool vm)
 {
  var dir=Path.Combine(Path.GetTempPath(),"mm-transition-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
  try
  {
   if(vm)File.WriteAllText(Path.Combine(dir,"FugaTES.mm-host.json"),"{\"Mode\":\"VmObserver\"}");
   ToledoSapJsonButtonTests.OnSta(()=>
   {
    using var session=FugaTES.WinForms.Monitor.UnifiedDesktopSession.Load(dir);using var form=session.CreateMm();
    Assert.Equal(vm,form.Text.Contains("motor na VM"));
   });
  }
  finally{Directory.Delete(dir,true);}
 }
 [Fact] public void VM_profile_does_not_fall_back_to_Windows_when_it_cannot_read_server()
 {
  var dir=Path.Combine(Path.GetTempPath(),"mm-profile-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
  try
  {
   File.WriteAllText(Path.Combine(dir,"FugaTES.mm-host.json"),"{\"Mode\":\"VmObserver\"}");Assert.True(MmPurchaseOrderComposition.IsVmObserver(dir));
   File.WriteAllText(Path.Combine(dir,"FugaTES.mm-host.json"),"{\"Mode\":\"UNKNOWN\"}");Assert.Throws<InvalidOperationException>(()=>MmPurchaseOrderComposition.IsVmObserver(dir));
  }
  finally{Directory.Delete(dir,true);}
 }
}
