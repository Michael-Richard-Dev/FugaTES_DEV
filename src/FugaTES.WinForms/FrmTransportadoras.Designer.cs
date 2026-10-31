#nullable enable
namespace FugaTES.WinForms;

partial class FrmTransportadoras
{
    private System.ComponentModel.IContainer? components;
    private TextBox bp = null!, name = null!;
    private ComboBox statusFilter = null!;
    private DateTimePicker from = null!, until = null!;
    private Button update = null!, detail = null!, syncNow = null!, logs = null!;
    private Label indicators = null!, message = null!;
    private TabControl tabs = null!;
    private TabPage logsTab = null!;
    private DataGridView rows = null!, cycles = null!, steps = null!;
    protected override void Dispose(bool disposing)
    {
        if (disposing) { DisposeRuntime(); components?.Dispose(); }
        base.Dispose(disposing);
    }
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        Text = "Transportadoras — SAP → Guardian — HML";
        ClientSize = new Size(1320, 780); MinimumSize = new Size(1000, 620);
        StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 9F);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        indicators = new Label { Dock = DockStyle.Fill, Text = "Serviço: NÃO VALIDADO | Intervalo previsto: 30s", Padding = new Padding(6), BackColor = Color.FromArgb(235, 242, 247), AutoEllipsis = true };
        layout.Controls.Add(indicators, 0, 0);
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true };
        bp = new TextBox { Width = 110 }; name = new TextBox { Width = 190 };
        statusFilter = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDown };
        statusFilter.Items.AddRange(["TODOS", "SYNCED", "NO_ACTION", "BLOCKED_DATA", "INDETERMINATE", "FAILED"]); statusFilter.Text = "TODOS";
        from = new DateTimePicker { Width = 180, ShowCheckBox = true, Checked = false, Format = DateTimePickerFormat.Custom, CustomFormat = "dd/MM/yyyy HH:mm" };
        until = new DateTimePicker { Width = 180, ShowCheckBox = true, Checked = false, Format = DateTimePickerFormat.Custom, CustomFormat = "dd/MM/yyyy HH:mm" };
        AddFilter(filters,"BP SAP", bp); AddFilter(filters,"Nome",name); AddFilter(filters,"Status",statusFilter); AddFilter(filters,"De (local)",from); AddFilter(filters,"Até (local)",until);
        layout.Controls.Add(filters, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        update = new Button { Text = "Atualizar tela", AutoSize = true };
        detail = new Button { Text = "Consultar detalhe", AutoSize = true };
        syncNow = new Button { Text = "Sincronizar agora (controlado)", AutoSize = true, Enabled = false };
        logs = new Button { Text = "Visualizar logs", AutoSize = true };
        actions.Controls.AddRange([update, detail, syncNow, logs]); layout.Controls.Add(actions, 0, 2);
        tabs = new TabControl { Dock = DockStyle.Fill };
        var carrierTab = new TabPage("Transportadoras"); logsTab = new TabPage("Logs de Sincronização");
        rows = Grid();
        Columns(rows, ("Bp","BP SAP",100),("Name","Nome / Razão social",230),("Guardian","Código Guardian",130),("Status","Status",145),("Sap","Última consulta SAP",205),("Sync","Última sincronização Guardian",220),("Action","Ação executada",130),("Result","Mensagem de resultado",280));
        carrierTab.Controls.Add(rows);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(1296, 500), SplitterDistance = 230 };
        cycles = Grid(); steps = Grid();
        Columns(cycles, ("Start","Início",205),("End","Fim",205),("Duration","Duração (s)",100),("Found","Consultadas",100),("Created","Criadas",80),("Updated","Atualizadas",90),("Unchanged","Sem alteração",105),("Pending","Pendentes",90),("Errors","Falhas",75),("Status","Status final",180));
        Columns(steps,("Bp","BP SAP",100),("Step","Etapa",200),("Result","Resultado técnico",420),("Time","Data/hora local",220));
        split.Panel1.Controls.Add(cycles); split.Panel2.Controls.Add(steps); logsTab.Controls.Add(split);
        tabs.TabPages.AddRange([carrierTab,logsTab]); layout.Controls.Add(tabs,0,3);
        message = new Label { Dock = DockStyle.Fill, Text = "Histórico persistente PostgreSQL. Nenhuma escrita Guardian é executada pela interface.", AutoEllipsis = true, Padding = new Padding(4) };
        layout.Controls.Add(message,0,4); Controls.Add(layout);
    }
    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false, BackgroundColor = Color.White, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells };
    private static void Columns(DataGridView grid, params (string Property,string Header,int Width)[] values)
    {
        foreach(var value in values) grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = value.Property, HeaderText = value.Header, Width = value.Width, SortMode = DataGridViewColumnSortMode.NotSortable });
    }
    private static void AddFilter(FlowLayoutPanel panel, string title, Control field)
    {
        var group = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Width = field.Width + 8, Height = 64, WrapContents = false };
        group.Controls.Add(new Label { Text = title, AutoSize = true }); group.Controls.Add(field); panel.Controls.Add(group);
    }
}
