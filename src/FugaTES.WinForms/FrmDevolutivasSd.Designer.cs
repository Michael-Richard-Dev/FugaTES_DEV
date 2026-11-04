namespace FugaTES.WinForms;

partial class FrmDevolutivasSd
{
    private System.ComponentModel.IContainer components = null!;
    private TableLayoutPanel layout;
    private FlowLayoutPanel barra;
    private Button atualizar;
    private CheckBox automatico;
    private CheckBox mostrarSemCorrelacao;
    private Label resumo;
    private DataGridView grid;
    private Label status;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components is not null) components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        layout = new TableLayoutPanel();
        barra = new FlowLayoutPanel();
        atualizar = new Button(); automatico = new CheckBox(); resumo = new Label();
        mostrarSemCorrelacao = new CheckBox();
        grid = new DataGridView(); status = new Label();
        SuspendLayout();

        layout.Dock = DockStyle.Fill;
        layout.ColumnCount = 1;
        layout.RowCount = 3;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        barra.Dock = DockStyle.Fill;
        barra.AutoSize = true;
        barra.WrapContents = false;
        barra.Padding = new Padding(4);

        atualizar.Text = "Atualizar"; atualizar.AutoSize = true; atualizar.Margin = new Padding(0, 2, 12, 0);
        automatico.Text = "A cada 30s"; automatico.AutoSize = true; automatico.Margin = new Padding(0, 7, 16, 0);
        // Desmarcada por padrao. A pesagem que chega sem indicar a ordem quase sempre e do ACM,
        // que o Guardian manda para as duas rotas; mostra-la aqui enche a tela de linha em branco
        // e confunde quem monitora a venda. Nada e descartado - a linha continua gravada, com o
        // documento, e esta caixa a traz de volta quando alguem precisar procurar uma pesagem.
        mostrarSemCorrelacao.Text = "Mostrar sem correlação"; mostrarSemCorrelacao.AutoSize = true;
        mostrarSemCorrelacao.Margin = new Padding(0, 7, 16, 0);
        resumo.AutoSize = true; resumo.Margin = new Padding(0, 8, 0, 0); resumo.Name = "resumo";

        barra.Controls.AddRange([atualizar, automatico, mostrarSemCorrelacao, resumo]);

        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.ReadOnly = true;
        grid.AutoGenerateColumns = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.Name = "grid";

        status.Dock = DockStyle.Fill;
        status.AutoSize = true;
        status.Padding = new Padding(4);
        status.Name = "status";

        layout.Controls.Add(barra, 0, 0);
        layout.Controls.Add(grid, 0, 1);
        layout.Controls.Add(status, 0, 2);

        Controls.Add(layout);
        ClientSize = new Size(1150, 560);
        Text = "Devolutivas do Guardian (SD) — pesagens recebidas";
        ResumeLayout(false);
    }
}
