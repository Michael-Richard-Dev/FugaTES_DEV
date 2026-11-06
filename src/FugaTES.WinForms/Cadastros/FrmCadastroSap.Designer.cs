#nullable enable
namespace FugaTES.WinForms.Cadastros;
partial class FrmCadastroSap
{
 private System.ComponentModel.IContainer? components;
 private TextBox codigo=null!,nome=null!,json=null!;
 private Button consultar=null!,anterior=null!,proxima=null!;
 private Label titulo=null!,escopo=null!,codigoLabel=null!,status=null!,pagina=null!,rodape=null!,detalheStatus=null!;
 private DataGridView grid=null!,dados=null!;
 private TabControl detalhes=null!;
 protected override void Dispose(bool disposing)
 {if(disposing){Liberar();components?.Dispose();}base.Dispose(disposing);}
 private void InitializeComponent()
 {
  components=new System.ComponentModel.Container();Name="FrmCadastroSap";Text="Cadastros — consulta SAP / integração Guardian";
  ClientSize=new Size(1320,820);MinimumSize=new Size(1080,680);StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",10);
  var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1,RowCount=7};
  layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
  foreach(var h in new[]{46,44,32,62})layout.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
  layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
  titulo=new Label{Text="Cadastros — dados do SAP",Font=new Font("Segoe UI",18,FontStyle.Bold),Dock=DockStyle.Fill};layout.Controls.Add(titulo,0,0);
  var filters=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
  codigoLabel=new Label{Text="Código SAP",AutoSize=true,Margin=new Padding(0,8,8,0)};filters.Controls.Add(codigoLabel);
  codigo=new TextBox{Name="codigoCadastro",Width=130,MaxLength=10,Margin=new Padding(0,4,18,0)};filters.Controls.Add(codigo);
  filters.Controls.Add(new Label{Text="Nome / descrição",AutoSize=true,Margin=new Padding(0,8,8,0)});
  nome=new TextBox{Name="nomeCadastro",Width=280,MaxLength=80,Margin=new Padding(0,4,18,0)};filters.Controls.Add(nome);
  consultar=new Button{Name="consultarCadastro",Text="Consultar / Atualizar",Width=175,Height=32};filters.Controls.Add(consultar);layout.Controls.Add(filters,0,1);
  escopo=new Label{Text="SAP: origem do cadastro  •  Guardian: não consultado nesta etapa",AutoSize=true,Dock=DockStyle.Fill};layout.Controls.Add(escopo,0,2);
  status=new Label{Name="statusCadastro",Dock=DockStyle.Fill,AutoEllipsis=true,Text="DESCONECTADO — clique em Consultar para ler o SAP.",Padding=new Padding(0,6,0,0)};layout.Controls.Add(status,0,3);
  var split=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,Size=new Size(1250,470),SplitterDistance=230,Panel1MinSize=140,Panel2MinSize=140};
  grid=new DataGridView{Name="cadastrosGrid",Dock=DockStyle.Fill,ReadOnly=true,AutoGenerateColumns=false,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,BackgroundColor=Color.White};
  grid.Columns.Add(new DataGridViewButtonColumn{Name="Detalhes",HeaderText="Dados recebidos",Text="Detalhes SAP",UseColumnTextForButtonValue=true,Width=110});
  grid.Columns.Add(new DataGridViewButtonColumn{Name="Json",HeaderText="JSON de origem",Text="JSON SAP",UseColumnTextForButtonValue=true,Width=100});
  foreach(var c in new[]{("Codigo","Código SAP",150),("Nome","Nome / descrição",280),("Tipo","Tipo SAP",100),("Informacoes","Informações SAP",170),("Situacao","Situação SAP",210),("Grupo","Grupo SAP",90)})grid.Columns.Add(new DataGridViewTextBoxColumn{Name=c.Item1,DataPropertyName=c.Item1,HeaderText=c.Item2,Width=c.Item3});
  grid.Columns["Nome"]!.AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;grid.Columns["Nome"]!.MinimumWidth=220;split.Panel1.Controls.Add(grid);
  var detailLayout=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1};detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,30));detailLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
  detalheStatus=new Label{Name="detalheStatusCadastro",Text="Selecione Detalhes SAP ou JSON SAP para consultar todos os dados disponíveis deste cadastro.",Dock=DockStyle.Fill,AutoEllipsis=true};detailLayout.Controls.Add(detalheStatus,0,0);
  detalhes=new TabControl{Name="detalhesCadastro",Dock=DockStyle.Fill};var page=new TabPage("Dados SAP");var jp=new TabPage("JSON SAP");
  dados=new DataGridView{Name="dadosCadastro",Dock=DockStyle.Fill,ReadOnly=true,AutoGenerateColumns=false,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,BackgroundColor=Color.White};
  foreach(var c in new[]{("Grupo","Grupo",220),("Campo","Campo SAP",270),("Valor","Valor recebido",400)})dados.Columns.Add(new DataGridViewTextBoxColumn{Name=c.Item1,DataPropertyName=c.Item1,HeaderText=c.Item2,Width=c.Item3});dados.Columns["Valor"]!.AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;dados.Columns["Valor"]!.MinimumWidth=250;
  json=new TextBox{Name="jsonCadastro",Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,WordWrap=false,ScrollBars=ScrollBars.Both,MaxLength=0,Font=new Font("Consolas",10)};page.Controls.Add(dados);jp.Controls.Add(json);detalhes.TabPages.Add(page);detalhes.TabPages.Add(jp);detailLayout.Controls.Add(detalhes,0,1);split.Panel2.Controls.Add(detailLayout);layout.Controls.Add(split,0,4);
  var nav=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Padding=new Padding(0,6,0,0)};anterior=new Button{Name="anteriorCadastro",Text="Anterior",Width=100,Height=30,Enabled=false};proxima=new Button{Name="proximaCadastro",Text="Próxima",Width=100,Height=30,Enabled=false};pagina=new Label{Name="paginaCadastro",AutoSize=true,Margin=new Padding(15,7,0,0)};nav.Controls.Add(anterior);nav.Controls.Add(proxima);nav.Controls.Add(pagina);layout.Controls.Add(nav,0,5);
  rodape=new Label{Name="rodapeCadastro",Dock=DockStyle.Fill,AutoEllipsis=true,Text="Somente leitura. O cadastro permanece no SAP; nenhum envio ou alteração Guardian é executado nesta tela.",Padding=new Padding(0,6,0,0)};layout.Controls.Add(rodape,0,6);Controls.Add(layout);AcceptButton=consultar;
 }
}
