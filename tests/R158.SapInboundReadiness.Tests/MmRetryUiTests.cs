using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
using FugaTES.WinForms;
using FugaTES.WinForms.Mm;
namespace FugaTES.Testes;
public sealed class MmRetryUiTests
{
 static MmGuardianMonitorRow Row(bool success=false)=>new(MmRetryPolicyTests.Source(),MmRetryPolicyTests.Record() with{Estado=success?EstadoProtocoloMm.Criado:EstadoProtocoloMm.Recusado,ProtocoloId=success?42:null,Http=success?201:null},"https://example.invalid/mm","MM_GUARDIAN_VERIFICADO");
 [Fact] public async Task Double_click_does_not_submit_two_commands()
 {
  var fake=new Client{Pause=true};var controller=new MmRetryController(fake);var row=Row();fake.CurrentReservation=row.Registro!.ReservaId;
  var work=controller.ExecuteAsync(row,_=>"Cadastros conferidos",default);await fake.Entered.Task;
  Assert.False(controller.CanClick(row));var second=await controller.ExecuteAsync(row,_=>"duplicate",default);Assert.Equal("MM_RETRY_UI_BLOCKED",second.Code);
  fake.Release.SetResult();await work;Assert.Equal(1,fake.Submits);Assert.False(controller.CanClick(row));
 }
 [Fact] public async Task Unknown_command_result_stays_latched_even_when_status_cannot_be_read()
 {var fake=new Client{Result="UNKNOWN"};var controller=new MmRetryController(fake);var row=Row();fake.CurrentReservation=row.Registro!.ReservaId;await controller.ExecuteAsync(row,_=>"Cadastros conferidos",default);fake.StatusThrows=true;await controller.RefreshAsync(default);Assert.False(controller.CanClick(row));Assert.Equal(1,fake.Submits);}
 [Fact] public async Task Completed_protocol_is_disabled_and_never_calls_command_preview()
 {var fake=new Client();var controller=new MmRetryController(fake);Assert.False(controller.CanClick(Row(true)));await controller.ExecuteAsync(Row(true),_=>"must not run",default);Assert.Equal(0,fake.Previews);Assert.Equal(0,fake.Submits);}
 [Theory][InlineData(true)][InlineData(false)] public void Populated_grid_paints_success_disabled_and_failed_item_enabled(bool success)
 {
  ToledoSapJsonButtonTests.OnSta(()=>{
   var row=Row(success);var snap=new MmPurchaseOrderSnapshot{Items=[row.Source],GuardianRecords=[row.Registro!],LastSuccessUtc=DateTimeOffset.UtcNow,State="MM_SAP_COMPLETE",GuardianCallback="https://example.invalid/mm"};
   using var form=new FrmPedidosCompraMm(()=>snap,()=>{});form.ConfigureRetry(new(new Client()));form.Show();Application.DoEvents();
   var grid=ToledoSapJsonButtonTests.Children(form).OfType<DataGridView>().Single();Assert.Single(grid.Rows.Cast<DataGridViewRow>());
   var cell=grid.Rows[0].Cells["reenviarGuardianMm"];Assert.Equal(!success,(bool)cell.GetType().GetProperty("Enabled")!.GetValue(cell)!);Assert.NotEmpty(cell.ToolTipText);form.Close();
  });
 }
 [Fact] public async Task Correctible_terminal_command_releases_click_only_after_verified_status()
 {var fake=new Client();var controller=new MmRetryController(fake);var row=Row();fake.CurrentReservation=row.Registro!.ReservaId;await controller.ExecuteAsync(row,_=>"Cadastros conferidos",default);Assert.False(controller.CanClick(row));fake.StatusResult="BLOCKED";await controller.RefreshAsync(default);Assert.True(controller.CanClick(row));}
 [Fact] public async Task Terminal_block_reason_remains_visible_after_the_button_is_released()
 {var fake=new Client();var controller=new MmRetryController(fake);var row=Row();fake.CurrentReservation=row.Registro!.ReservaId;await controller.ExecuteAsync(row,_=>"Cadastros conferidos",default);fake.StatusResult="BLOCKED";fake.StatusCode="MM_PRECHECK_MATERIAL_MISSING";await controller.RefreshAsync(default);Assert.True(controller.CanClick(row));Assert.Contains("Material",controller.Status(row));}
 [Fact] public async Task Unexpected_submit_exception_keeps_unknown_command_latched()
 {var fake=new Client{ThrowSubmit=true};var controller=new MmRetryController(fake);var row=Row();fake.CurrentReservation=row.Registro!.ReservaId;await controller.ExecuteAsync(row,_=>"Cadastros conferidos",default);Assert.False(controller.CanClick(row));Assert.Equal(1,fake.Submits);}
 public sealed class Client:IMmRetryCommandClient
 {
  public bool Configured=>true;public int Previews,Submits;public bool Pause,StatusThrows,ThrowSubmit;public string Result="QUEUED",StatusResult="QUEUED",StatusCode="SYNTHETIC";
  public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);MmRetryRequest? request;
  public async Task<MmRetryPreview> PreviewMmAsync(string p,string i,CancellationToken ct){Previews++;Entered.TrySetResult();if(Pause)await Release.Task;return new(p,i,CurrentReservation,new string('A',64),MmRetryExecutionTests.Sid,new(true,"ALLOW","offline synthetic"));}
  public Guid CurrentReservation;
  public Task<MmRetryReceipt> SubmitMmAsync(MmRetryRequest c,CancellationToken ct){Submits++;request=c;if(ThrowSubmit)throw new IOException("SYNTHETIC");return Task.FromResult(new MmRetryReceipt(c.CommandId,c.Pedido,c.Item,Result,"SYNTHETIC"));}
  public Task<MmRetryReceipt?> StatusMmAsync(Guid id,CancellationToken ct)=>StatusThrows?Task.FromException<MmRetryReceipt?>(new IOException("SYNTHETIC")):Task.FromResult<MmRetryReceipt?>(request is null?null:new(id,request.Pedido,request.Item,StatusResult,StatusCode));
 }
}
