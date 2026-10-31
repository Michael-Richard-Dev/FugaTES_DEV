#nullable enable
namespace FugaTES.WinForms;
partial class FrmPedidosCompraMm
{
    private System.ComponentModel.IContainer? components;
    private DataGridView grid=null!;
    private Label status=null!,rodape=null!,quantidade=null!;
    private Button atualizar=null!;
    private TextBox filtro=null!;
    private ComboBox centros=null!;
    protected override void Dispose(bool disposing){if(disposing)components?.Dispose();base.Dispose(disposing);}
    private void InitializeComponent()
    {
        components=new System.ComponentModel.Container();
        Text="Pedidos de compra (MM) — SAP · empresa 3000";Name="FrmPedidosCompraMm";
        ClientSize=new Size(1310,740);MinimumSize=new Size(1050,620);StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",10);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=1,RowCount=6};
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,90));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,64));
        layout.Controls.Add(new Label{Text="Pedidos de compra (MM)",Font=new Font("Segoe UI",18,FontStyle.Bold),Dock=DockStyle.Fill},0,0);
        var filters=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
        filters.Controls.Add(new Label{Text="Pedido, material ou fornecedor",AutoSize=true,Margin=new Padding(0,9,8,0)});
        filtro=new TextBox{Name="filtroMm",Width=245,Margin=new Padding(0,5,14,0)};filters.Controls.Add(filtro);
        filters.Controls.Add(new Label{Text="Centro",AutoSize=true,Margin=new Padding(0,9,8,0)});
        centros=new ComboBox{Name="centrosMm",DropDownStyle=ComboBoxStyle.DropDownList,Width=85,Margin=new Padding(0,5,14,0)};
        centros.Items.AddRange(new object[]{"Todos","3011","3008"});centros.SelectedIndex=0;filters.Controls.Add(centros);
        atualizar=new Button{Name="atualizarMm",Text="Atualizar leitura",Width=155,Height=32};filters.Controls.Add(atualizar);layout.Controls.Add(filters,0,1);
        status=new Label{Name="statusMm",Dock=DockStyle.Fill,Text="DESCONECTADO — aguardando leitura do coletor MM.",AutoEllipsis=true};layout.Controls.Add(status,0,2);
        var scope=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
        scope.Controls.Add(new Label{Text="Empresa 3000 · centros 3011/3008 · todos os tipos · status 05 · sem bloqueio/exclusão",AutoSize=true});
        quantidade=new Label{AutoSize=true,Margin=new Padding(16,0,0,0)};scope.Controls.Add(quantidade);layout.Controls.Add(scope,0,3);
        grid=new DataGridView{Name="gridMm",Dock=DockStyle.Fill,AutoGenerateColumns=false,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,RowHeadersVisible=false,BackgroundColor=Color.White,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.None};layout.Controls.Add(grid,0,4);
        rodape=new Label{Name="rodapeMm",Dock=DockStyle.Fill,Padding=new Padding(0,8,0,0),AutoEllipsis=true};layout.Controls.Add(rodape,0,5);
        Controls.Add(layout);
    }
}
