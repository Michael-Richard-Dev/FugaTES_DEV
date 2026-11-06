using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.WinForms.Mm;
namespace FugaTES.WinForms;
public partial class FrmPedidosCompraMm
{
 MmRetryController retryController=new(null);
 Action? retryChanged;
 public void ConfigureRetry(MmRetryController controller,Action? changed=null){retryController=controller;retryChanged=changed;RefreshRetryButtons();}
 void ConfigureRetryUi()
 {
  grid.Columns.Insert(2,new DataGridViewButtonColumn{Name="reenviarGuardianMm",HeaderText="Recuperar Guardian",Text="Reenviar",UseColumnTextForButtonValue=true,Width=130,CellTemplate=new MmDisabledButtonCell()});
  grid.Columns.Insert(3,new DataGridViewTextBoxColumn{Name="reenviarEstadoMm",HeaderText="Comando de reenvio",Width=290});
  grid.DataBindingComplete+=(_,_)=>RefreshRetryButtons();
  grid.CellContentClick+=async(_,e)=>
  {
   if(e.RowIndex<0||e.ColumnIndex<0||grid.Columns[e.ColumnIndex].Name!="reenviarGuardianMm"||grid.Rows[e.RowIndex].DataBoundItem is not MmGuardianMonitorRow row||!retryController.CanClick(row))return;
   var work=retryController.ExecuteAsync(row,preview=>{using var confirm=new MmRetryConfirmation(preview);return confirm.ShowDialog(this)==DialogResult.OK?confirm.Justification:null;},readingLifetime.Token);
   RefreshRetryButtons();var result=await work;if(IsDisposed)return;
   rodape.Text=result.Message;rodape.ForeColor=result.Receipt?.Accepted==true?Color.DarkGreen:Color.DarkRed;retryChanged?.Invoke();
   await RefreshRetryStatusAsync();
  };
 }
 async Task RefreshRetryStatusAsync()
 {
  await retryController.RefreshAsync(readingLifetime.Token);if(!IsDisposed)RefreshRetryButtons();
 }
 void RefreshRetryButtons()
 {
  if(IsDisposed||!grid.Columns.Contains("reenviarGuardianMm"))return;
  foreach(DataGridViewRow row in grid.Rows)
   if(row.Cells["reenviarGuardianMm"] is MmDisabledButtonCell cell&&row.DataBoundItem is MmGuardianMonitorRow item)
   {cell.Enabled=retryController.CanClick(item);cell.ToolTipText=retryController.Status(item);row.Cells["reenviarEstadoMm"].Value=retryController.Status(item);}
  grid.Invalidate();
 }
 sealed class MmDisabledButtonCell:DataGridViewButtonCell
 {
  public MmDisabledButtonCell(){}
  public bool Enabled{get;set;}
  public override object Clone(){var c=(MmDisabledButtonCell)base.Clone();c.Enabled=Enabled;return c;}
  protected override void Paint(Graphics g,Rectangle clip,Rectangle bounds,int rowIndex,DataGridViewElementStates state,object? value,object? formattedValue,string? error,DataGridViewCellStyle style,DataGridViewAdvancedBorderStyle border,DataGridViewPaintParts parts)
  {
   if(Enabled){base.Paint(g,clip,bounds,rowIndex,state,value,formattedValue,error,style,border,parts);return;}
   using var brush=new SolidBrush(style.BackColor);g.FillRectangle(brush,bounds);var button=Rectangle.Inflate(bounds,-2,-2);ControlPaint.DrawButton(g,button,ButtonState.Inactive);TextRenderer.DrawText(g,formattedValue?.ToString()??"Reenviar",style.Font,button,SystemColors.GrayText,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
  }
 }
}
