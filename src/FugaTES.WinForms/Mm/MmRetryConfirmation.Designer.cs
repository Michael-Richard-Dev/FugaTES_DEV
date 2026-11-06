#nullable enable
namespace FugaTES.WinForms.Mm;
public sealed partial class MmRetryConfirmation
{
 System.ComponentModel.IContainer? components;
 Label facts=null!;TextBox justification=null!;Button accept=null!;
 protected override void Dispose(bool disposing){if(disposing)components?.Dispose();base.Dispose(disposing);}
 void InitializeComponent()
 {
  components=new System.ComponentModel.Container();Text="Confirmar recuperação MM no Guardian";ClientSize=new(700,330);StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
  facts=new(){Left=16,Top=16,Width=665,Height=150};
  var caption=new Label{Left=16,Top=174,Width=650,Text="Justificativa da correção (mínimo de 8 caracteres):"};
  justification=new(){Left=16,Top=200,Width=665,MaxLength=500,Name="justificativaMm"};
  accept=new(){Left=436,Top=268,Width=120,Text="Confirmar reenvio",Enabled=false,DialogResult=DialogResult.OK};
  var cancel=new Button{Left=568,Top=268,Width=112,Text="Cancelar",DialogResult=DialogResult.Cancel};
  Controls.AddRange([facts,caption,justification,accept,cancel]);AcceptButton=accept;CancelButton=cancel;
 }
}
