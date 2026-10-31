namespace FugaTES.WinForms.ManualRetry;

internal sealed partial class AcmRetryConfirmation
{
    TextBox reason=null!;Label summary=null!;Button confirm=null!;
    void InitializeComponent()
    {
        Text="Confirmar recuperação manual ACM";ClientSize=new(650,350);StartPosition=FormStartPosition.CenterParent;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=4,ColumnCount=1,Padding=new(12)};
        layout.RowStyles.Add(new(SizeType.Absolute,155));layout.RowStyles.Add(new(SizeType.Absolute,25));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,40));
        summary=new(){Dock=DockStyle.Fill};reason=new(){Name="RetryJustification",Multiline=true,Dock=DockStyle.Fill,MaxLength=400};
        layout.Controls.Add(summary);layout.Controls.Add(new Label{Text="Justificativa obrigatória (sem credenciais ou dados fiscais):",Dock=DockStyle.Fill});layout.Controls.Add(reason);
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill};confirm=new(){Text="Confirmar reenvio",AutoSize=true,Enabled=false,DialogResult=DialogResult.OK};
        buttons.Controls.Add(confirm);buttons.Controls.Add(new Button{Text="Cancelar",DialogResult=DialogResult.Cancel});layout.Controls.Add(buttons);Controls.Add(layout);
    }
}
