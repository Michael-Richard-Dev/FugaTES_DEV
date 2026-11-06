#nullable enable
namespace FugaTES.WinForms;
partial class FrmCadastros
{
    private System.ComponentModel.IContainer? components;
    private Button emissor=null!;
    private Button material=null!;
    private Button motorista=null!;
    private Button transportadora=null!;
    private Button veiculo=null!;
    private Button voltar=null!;
    protected override void Dispose(bool disposing){if(disposing)components?.Dispose();base.Dispose(disposing);}
    private void InitializeComponent()
    {
        components=new System.ComponentModel.Container();
        Text="Cadastros — Integrações SAP / Guardian";
        ClientSize=new Size(620,500);
        MinimumSize=new Size(520,500);
        StartPosition=FormStartPosition.CenterParent;
        Font=new Font("Segoe UI",10F);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(24),ColumnCount=1,RowCount=7};
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,60));
        for(var i=0;i<5;i++)layout.RowStyles.Add(new RowStyle(SizeType.Percent,20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
        layout.Controls.Add(new Label{Text="Cadastros",Dock=DockStyle.Fill,Font=new Font("Segoe UI",18F,FontStyle.Bold)},0,0);
        emissor=new Button{Name="emissor",Text="Emissor",TabIndex=0,Dock=DockStyle.Fill,Margin=new Padding(0,4,0,4)};
        material=new Button{Name="material",Text="Material",TabIndex=1,Dock=DockStyle.Fill,Margin=new Padding(0,4,0,4)};
        motorista=new Button{Name="motorista",Text="Motorista",TabIndex=2,Dock=DockStyle.Fill,Margin=new Padding(0,4,0,4)};
        transportadora=new Button{Name="transportadora",Text="Transportadora",TabIndex=3,Dock=DockStyle.Fill,Margin=new Padding(0,4,0,4)};
        veiculo=new Button{Name="veiculo",Text="Veículo",TabIndex=4,Dock=DockStyle.Fill,Margin=new Padding(0,4,0,4)};
        layout.Controls.Add(emissor,0,1);layout.Controls.Add(material,0,2);layout.Controls.Add(motorista,0,3);
        layout.Controls.Add(transportadora,0,4);layout.Controls.Add(veiculo,0,5);
        voltar=new Button{Name="voltar",Text="Voltar",TabIndex=5,Anchor=AnchorStyles.Right|AnchorStyles.Bottom,Width=120,Height=34};
        layout.Controls.Add(voltar,0,6);
        Controls.Add(layout);CancelButton=voltar;
    }
}
