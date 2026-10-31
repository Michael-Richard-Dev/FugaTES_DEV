#nullable enable
namespace FugaTES.WinForms.Toledo;
public sealed partial class FrmRecebimentosToledo
{
 private TextBox busca=null!;private Button consultar=null!;private DataGridView grid=null!;private Label status=null!,fonte=null!;
 protected override void Dispose(bool disposing){if(disposing)ReleaseResources();base.Dispose(disposing);}
 private void InitializeComponent()
 {
  Text="Recebimentos Guardian / Toledo — devolutiva SAP — PostgreSQL DEV";ClientSize=new Size(1320,650);MinimumSize=new Size(980,520);
  StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",9F);
  var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Padding=new Padding(12)};
  layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
  layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
  layout.Controls.Add(new Label{Text="Recebimentos Guardian / Toledo",Dock=DockStyle.Fill,Font=new Font("Segoe UI",16F,FontStyle.Bold)},0,0);
  var bar=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};bar.Controls.Add(new Label{Text="Contrato, ID da pesagem ou ReciboId",AutoSize=true,Margin=new Padding(0,7,8,0)});
  busca=new TextBox{Name="ToledoReceiptSearch",AccessibleName="Contrato, pesagem ou ReciboId",Width=360};bar.Controls.Add(busca);
  consultar=new Button{Name="ToledoReceiptRefresh",Text="Consultar / Atualizar",Width=160,Height=28,Margin=new Padding(12,0,0,0)};bar.Controls.Add(consultar);layout.Controls.Add(bar,0,1);
  fonte=new Label{Name="ToledoReceiptSource",Dock=DockStyle.Fill,Text="PostgreSQL DEV5433 · JSON recebido é consultado somente ao clicar. Recebimento não significa devolutiva SAP concluída.",AutoEllipsis=true};layout.Controls.Add(fonte,0,2);
  grid=new DataGridView{Name="ToledoReceiptsGrid",Dock=DockStyle.Fill,ReadOnly=true,AutoGenerateColumns=false,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.DisplayedCells};
  grid.Columns.Add(new DataGridViewButtonColumn{Name="json",HeaderText="JSON recebido",Text="JSON",UseColumnTextForButtonValue=true,Width=95,AutoSizeMode=DataGridViewAutoSizeColumnMode.None});
  grid.Columns.Add(new DataGridViewButtonColumn{Name="jsonSap",HeaderText="JSON SAP",Text="JSON SAP",UseColumnTextForButtonValue=true,Width=95,AutoSizeMode=DataGridViewAutoSizeColumnMode.None});
  void Col(string field,string title,string? format=null){var column=new DataGridViewTextBoxColumn{Name=field,DataPropertyName=field,HeaderText=title};if(format is not null)column.DefaultCellStyle.Format=format;grid.Columns.Add(column);}
  Col("IdPesagem","ID da pesagem");Col("Contratos","Contrato(s) informado(s)");Col("Centro","Centro");
  Col("RecebidoEm","Recebido (UTC)","dd/MM/yyyy HH:mm:ss");Col("DataPesagemLocal","Pesagem (hora informada)","dd/MM/yyyy HH:mm:ss");
  Col("Estado","Estado do recebimento");Col("Correlacao","Correlação H4");Col("Itens","Itens");Col("Origem","Sistema informado");Col("Admissao","Admissão da entrada");Col("ReciboId","ReciboId");
    grid.Columns.Add(new DataGridViewButtonColumn{Name="reenviarSap",HeaderText="Recuperar falha SAP",Text="Reenviar",UseColumnTextForButtonValue=true,Width=95,AutoSizeMode=DataGridViewAutoSizeColumnMode.None});
  Col("EstadoSap","Devolutiva SAP");Col("HttpSap","HTTP SAP");Col("ResultadoSap","Resultado registrado SAP");Col("MotivoReenvio","Condição para reenvio");
  string[] visibleOrder=["json","jsonSap","reenviarSap","IdPesagem","Contratos","EstadoSap","HttpSap","Correlacao","RecebidoEm","ResultadoSap"];
  for(var n=0;n<visibleOrder.Length;n++)grid.Columns[visibleOrder[n]]!.DisplayIndex=n;
  grid.Columns["EstadoSap"]!.AutoSizeMode=DataGridViewAutoSizeColumnMode.None;grid.Columns["EstadoSap"]!.Width=230;
  grid.Columns["HttpSap"]!.AutoSizeMode=DataGridViewAutoSizeColumnMode.None;grid.Columns["HttpSap"]!.Width=135;
  grid.Columns["ResultadoSap"]!.AutoSizeMode=DataGridViewAutoSizeColumnMode.None;grid.Columns["ResultadoSap"]!.Width=280;
  layout.Controls.Add(grid,0,3);status=new Label{Name="ToledoReceiptStatus",Dock=DockStyle.Fill,Text="Aguardando consulta. Primeiro envio é automático quando o backend estiver autorizado e a pesagem elegível.",TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};layout.Controls.Add(status,0,4);Controls.Add(layout);
 }
}
