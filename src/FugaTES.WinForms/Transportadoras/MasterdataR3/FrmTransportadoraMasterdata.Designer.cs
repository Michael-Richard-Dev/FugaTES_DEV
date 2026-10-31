namespace FugaTES.WinForms.Transportadoras.MasterdataR3;

public partial class FrmTransportadoraMasterdata
{
    TextBox bp=null!,detalhes=null!;
    Button consultar=null!,cadastrar=null!;
    Label status=null!;
    DataGridView historico=null!;
    void InitializeComponent()
    {
        Text="Transportadoras — SAP → Guardian";StartPosition=FormStartPosition.CenterParent;
        Size=new(1050,760);MinimumSize=new(820,620);AutoScaleMode=AutoScaleMode.Font;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new(12)};
        layout.RowStyles.Add(new(SizeType.Absolute,46));layout.RowStyles.Add(new(SizeType.Percent,60));
        layout.RowStyles.Add(new(SizeType.Percent,40));layout.RowStyles.Add(new(SizeType.Absolute,84));
        var toolbar=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
        bp=new(){Name="BusinessPartner",Width=150,MaxLength=20,AccessibleName="BP SAP"};
        consultar=new(){Name="Consultar",Text="Consultar SAP / Guardian / histórico",AutoSize=true};
        cadastrar=new(){Name="Cadastrar",Text="Cadastrar alvo autorizado",AutoSize=true,Enabled=false};
        toolbar.Controls.Add(new Label{Text="BP SAP",AutoSize=true,Margin=new(0,7,8,0)});
        toolbar.Controls.Add(bp);toolbar.Controls.Add(consultar);toolbar.Controls.Add(cadastrar);
        detalhes=new(){Name="DadosTransportadora",Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Both,WordWrap=false};
        historico=new(){Name="JournalMasterdata",ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,
            Dock=DockStyle.Fill,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.DisplayedCells,RowHeadersVisible=false};
        status=new(){Name="Situacao",Text="Consulta individual. CREATE manual exige autorização específica; lote desativado.",Dock=DockStyle.Fill,AutoEllipsis=true};
        layout.Controls.Add(toolbar,0,0);layout.Controls.Add(detalhes,0,1);layout.Controls.Add(historico,0,2);layout.Controls.Add(status,0,3);
        Controls.Add(layout);AcceptButton=consultar;
    }
}
