namespace FugaTES.WinForms;

partial class FrmOrdensVendaSd
{
    private System.ComponentModel.IContainer components = null!;
    private TableLayoutPanel layout;
    private FlowLayoutPanel filtros;
    private Label lblOrganizacao, lblCentros, lblTipos;
    // Organizacao e rotulo, nao campo: e fixa. Centro e lista fechada com os centros permitidos.
    private Label organizacao;
    private ComboBox centros;
    private TextBox tipos;
    private Button atualizar;
    private CheckBox automatico;
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
        filtros = new FlowLayoutPanel();
        lblOrganizacao = new Label(); organizacao = new Label();
        lblCentros = new Label(); centros = new ComboBox();
        lblTipos = new Label(); tipos = new TextBox();
        atualizar = new Button(); automatico = new CheckBox();
        grid = new DataGridView(); status = new Label();
        SuspendLayout();

        layout.Dock = DockStyle.Fill;
        layout.ColumnCount = 1;
        layout.RowCount = 3;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        filtros.Dock = DockStyle.Fill;
        filtros.AutoSize = true;
        filtros.WrapContents = false;
        filtros.Padding = new Padding(4);

        lblOrganizacao.Text = "Organização"; lblOrganizacao.AutoSize = true; lblOrganizacao.Margin = new Padding(4, 8, 2, 0);
        organizacao.AutoSize = true; organizacao.Margin = new Padding(0, 8, 12, 0);
        organizacao.Font = new Font(Font, FontStyle.Bold);
        organizacao.Name = "organizacao";
        lblCentros.Text = "Centro"; lblCentros.AutoSize = true; lblCentros.Margin = new Padding(4, 8, 2, 0);
        centros.Width = 90; centros.Margin = new Padding(0, 4, 12, 0);
        // Lista fechada: o usuario escolhe, nao digita.
        centros.DropDownStyle = ComboBoxStyle.DropDownList;
        centros.Name = "centros";
        lblTipos.Text = "Tipos de documento"; lblTipos.AutoSize = true; lblTipos.Margin = new Padding(4, 8, 2, 0);
        tipos.Width = 150; tipos.Margin = new Padding(0, 4, 12, 0);
        atualizar.Text = "Buscar agora"; atualizar.AutoSize = true; atualizar.Margin = new Padding(0, 2, 12, 0);
        automatico.Text = "Varredura a cada 30s (cria protocolos)"; automatico.AutoSize = true; automatico.Margin = new Padding(0, 7, 16, 0);

        filtros.Controls.AddRange([lblOrganizacao, organizacao, lblCentros, centros, lblTipos, tipos, atualizar, automatico]);

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

        layout.Controls.Add(filtros, 0, 0);
        layout.Controls.Add(grid, 0, 1);
        layout.Controls.Add(status, 0, 2);

        Controls.Add(layout);
        ClientSize = new Size(1100, 560);
        Text = "Ordens de venda (SD) — validação da busca no SAP";
        ResumeLayout(false);
    }
}
