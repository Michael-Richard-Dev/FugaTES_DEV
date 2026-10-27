#nullable enable
namespace FugaTES.WinForms;

public partial class FrmContratosAcm
{
    private TextBox centro = null!;
    private Button atualizar = null!;
    private DataGridView grid = null!;
    private Label status = null!;
    private DataGridViewButtonColumn json = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            cancelamento.Cancel();
            lifetime?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        Text = "FugaTES - Contratos ACM";
        ClientSize = new Size(1180, 600);
        MinimumSize = new Size(900, 420);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        var filtro = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        filtro.Controls.Add(new Label { Text = "Centro", AutoSize = true, Margin = new Padding(0, 7, 8, 0) });
        centro = new TextBox { Width = 110, AccessibleName = "Centro" };
        atualizar = new Button { Text = "Consultar", Width = 100, Height = 28 };
        filtro.Controls.Add(centro);
        filtro.Controls.Add(atualizar);
        grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells };
        void Column(string field, string title) => grid.Columns.Add(new DataGridViewTextBoxColumn
            { DataPropertyName = field, HeaderText = title, Name = field });
        Column("Centro", "Centro"); Column("NumeroContrato", "Contrato");
        // A identidade empresarial do contrato e NumeroContrato + Item. Sem a coluna de
        // item, varias linhas do mesmo contrato ficam indistinguiveis na grade.
        Column("Item", "Item"); Column("Versao", "Versao");
        Column("RecebidoEm", "Recebido em (UTC)"); Column("StatusContrato", "Status contrato");
        Column("EstadoIntegracao", "Estado integracao");
        grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "VersaoMaisRecente", HeaderText = "Mais recente" });
        Column("CorrelationId", "Correlacao origem"); Column("IntegracaoId", "Integracao");
        json = new DataGridViewButtonColumn
        {
            Name = "json", HeaderText = "JSON", Text = "JSON", UseColumnTextForButtonValue = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 70,
            ToolTipText = "Abrir o JSON deste contrato e item",
        };
        grid.Columns.Add(json);
        status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        layout.Controls.Add(filtro, 0, 0); layout.Controls.Add(grid, 0, 1); layout.Controls.Add(status, 0, 2);
        Controls.Add(layout);
    }
}
