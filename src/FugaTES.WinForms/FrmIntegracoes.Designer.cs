#nullable enable
namespace FugaTES.WinForms;

partial class FrmIntegracoes
{
    private System.ComponentModel.IContainer? components;
    private Button contratos = null!;
    private Button transportadoras = null!;
    private Button guardian = null!;
    private Button contratosJson = null!;
    protected override void Dispose(bool disposing) { if (disposing) components?.Dispose(); base.Dispose(disposing); }
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        Text = "Integrações — FugaTES HML";
        ClientSize = new Size(660, 360);
        MinimumSize = new Size(620, 350);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10F);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "Integrações", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 17F, FontStyle.Bold) }, 0, 0);
        contratos = new Button { Text = "Contratos ACM", Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8) };
        layout.Controls.Add(contratos, 0, 1);
        var cadastros = new GroupBox { Text = "Cadastros SAP → Guardian", Dock = DockStyle.Fill };
        transportadoras = new Button { Text = "Transportadoras", Dock = DockStyle.Fill, Margin = new Padding(8) };
        cadastros.Text = "Cadastros SAP";
        guardian = new Button {Text="Guardian",Dock=DockStyle.Left,Width=180,Margin=new Padding(8)};

        cadastros.Controls.Add(transportadoras);
        cadastros.Controls.Add(guardian);
        layout.Controls.Add(cadastros, 0, 2);
        // Entrada propria para a tela de contratos por contrato + item, onde fica o JSON.
        // So aparece quando o botao acima abre o Monitor ACM, de modo a acrescentar um
        // caminho sem substituir nem duplicar o acesso ja existente.
        contratosJson = new Button { Text = "Contratos ACM — JSON por contrato/item", Dock = DockStyle.Top,
            Height = 46, Margin = new Padding(0, 8, 0, 8), Visible = false, Name = "contratosJson" };
        layout.Controls.Add(contratosJson, 0, 3);
        Controls.Add(layout);
    }
}
