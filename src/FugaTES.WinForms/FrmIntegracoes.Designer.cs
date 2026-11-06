#nullable enable
namespace FugaTES.WinForms;

partial class FrmIntegracoes
{
    private System.ComponentModel.IContainer? components;
    private Button contratos = null!;
    private Button transportadoras = null!;
    private Button guardian = null!;
    private Button cadastros = null!;
    private Button contratosJson = null!;
    private Button protocolosPreCadastro = null!;
    private Button toledoRecebimentos = null!;
    private Button ordensVendaSd = null!;
    private Button pedidosCompraMm = null!;
    private Button devolutivasSd = null!;
    protected override void Dispose(bool disposing) { if (disposing) components?.Dispose(); base.Dispose(disposing); }
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        Text = "Integrações — FugaTES HML";
        ClientSize = new Size(660, 710);
        MinimumSize = new Size(620, 700);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10F);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 9 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "Integrações", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 17F, FontStyle.Bold) }, 0, 0);
        contratos = new Button { Text = "Contratos ACM", Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8) };
        layout.Controls.Add(contratos, 0, 1);
        var grupoCadastros = new GroupBox { Text = "Cadastros SAP → Guardian", Dock = DockStyle.Fill };
        transportadoras = new Button { Text = "Transportadoras", Dock = DockStyle.Fill, Margin = new Padding(8) };
        grupoCadastros.Text = "Cadastros SAP";
        guardian = new Button {Text="Guardian",Dock=DockStyle.Left,Width=180,Margin=new Padding(8)};

        grupoCadastros.Controls.Add(transportadoras);
        grupoCadastros.Controls.Add(guardian);
        cadastros = new Button { Text = "Cadastros", Name = "cadastros", Dock = DockStyle.Left, Width = 140, TabIndex = 0, Margin = new Padding(8) };
        grupoCadastros.Controls.Add(cadastros);
        layout.Controls.Add(grupoCadastros, 0, 2);
        // Entrada propria para a tela de contratos por contrato + item, onde fica o JSON.
        // So aparece quando o botao acima abre o Monitor ACM, de modo a acrescentar um
        // caminho sem substituir nem duplicar o acesso ja existente.
        contratosJson = new Button { Text = "Contratos ACM — JSON por contrato/item", Dock = DockStyle.Top,
            Height = 46, Margin = new Padding(0, 8, 0, 8), Visible = false, Name = "contratosJson" };
        layout.Controls.Add(contratosJson, 0, 3);
        protocolosPreCadastro = new Button { Text = "Protocolos de Pré-Cadastro Guardian", Name = "protocolosPreCadastro",
            Dock = DockStyle.Top, Height = 46, Margin = new Padding(0, 8, 0, 8), Enabled = false };
        layout.Controls.Add(protocolosPreCadastro, 0, 4);
        toledoRecebimentos=new Button{Text="Recebimentos Guardian / Toledo",Name="toledoRecebimentos",Dock=DockStyle.Top,Height=46,Margin=new Padding(0,8,0,8),Enabled=false};
        layout.Controls.Add(toledoRecebimentos,0,5);
        // Fluxo SD, em construcao: leitura de ordens de venda no SAP para validar a busca. Nao
        // despacha nada, e por isso fica fora do grupo de cadastros SAP -> Guardian.
        ordensVendaSd = new Button { Text = "Ordens de venda (SD) — leitura SAP", Name = "ordensVendaSd",
            Dock = DockStyle.Top, Height = 46, Margin = new Padding(0, 8, 0, 8) };
        layout.Controls.Add(ordensVendaSd, 0, 6);
        pedidosCompraMm = new Button { Text = "Pedidos de compra (MM) — leitura SAP", Name = "pedidosCompraMm",
            Dock = DockStyle.Top, Height = 46, Margin = new Padding(0, 8, 0, 8) };
        layout.Controls.Add(pedidosCompraMm, 0, 7);
        // Devolutiva do Guardian para o SD: acompanhamento das pesagens que voltaram.
        devolutivasSd = new Button { Text = "Devolutivas do Guardian (SD) — pesagens", Name = "devolutivasSd",
            Dock = DockStyle.Top, Height = 46, Margin = new Padding(0, 8, 0, 8) };
        layout.Controls.Add(devolutivasSd, 0, 8);
        Controls.Add(layout);
    }
}
