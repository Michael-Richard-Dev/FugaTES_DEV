namespace FugaTES.WinForms.Toledo;
public sealed partial class FrmRecebimentoToledoJson
{
 private TextBox json=null!;private Label titulo=null!,rodape=null!;private Button copiar=null!,fechar=null!;
 private void InitializeComponent()
 {
  ClientSize=new Size(1080,700);MinimumSize=new Size(850,520);StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",9F);
  var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(12)};
  layout.RowStyles.Add(new RowStyle(SizeType.Absolute,52));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,36));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,52));
  titulo=new Label{Dock=DockStyle.Fill,AutoEllipsis=true};layout.Controls.Add(titulo,0,0);
  var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};copiar=new Button{Name="ToledoCopyReceivedJson",Text="Copiar JSON exibido",Width=165,Height=28};fechar=new Button{Text="Fechar",Width=90,Height=28};buttons.Controls.Add(copiar);buttons.Controls.Add(fechar);layout.Controls.Add(buttons,0,1);
  json=new TextBox{Name="ToledoReceivedJson",AccessibleName="JSON recebido persistido",Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false,Font=new Font("Consolas",10F)};layout.Controls.Add(json,0,2);
  rodape=new Label{Dock=DockStyle.Fill,AutoEllipsis=true};layout.Controls.Add(rodape,0,3);Controls.Add(layout);
 }
}
