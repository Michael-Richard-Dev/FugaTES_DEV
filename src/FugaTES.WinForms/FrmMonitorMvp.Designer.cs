#nullable enable
namespace FugaTES.WinForms;

partial class FrmMonitorMvp
{
    private System.ComponentModel.IContainer? components;
    private new TableLayoutPanel Layout = null!;
    private FlowLayoutPanel Filtros = null!;
    private FlowLayoutPanel Comandos = null!;
    private FlowLayoutPanel Rodape = null!;
    private TextBox Operacao = null!, Correlacao = null!, Documento = null!;
    private ComboBox Estado = null!;
    private DateTimePicker De = null!, Ate = null!;
    private Button Atualizar = null!, Todos = null!, Pendentes = null!, Falhas = null!, Indeterminados = null!;
    private CheckBox AutoRefresh = null!;
    private Label Indicadores = null!, Projecao = null!, Ambiente = null!, Banco = null!, UltimoRefresh = null!, Escopo = null!;
    private DataGridView Integracoes = null!, Tentativas = null!, Auditoria = null!, Guardian = null!;
    private DataGridView FluxoAcm = null!;
    private TextBox IdentidadesAcm = null!;
    private TextBox Geral = null!;
    private TabControl Detalhes = null!;
    private TabPage GeralTab = null!, TentativasTab = null!, AuditoriaTab = null!, GuardianTab = null!;
    private TabPage AcmTab = null!;
    private TabPage ProtocolTab = null!;
    private Button GenerateProtocol = null!, CopyProtocol = null!, SendProtocol = null!;
    private TextBox ProtocolJson = null!, ProtocolMetadata = null!, ProtocolResult = null!;
    private Label ProtocolLockReason = null!;
    private System.Windows.Forms.Timer RefreshTimer = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            RefreshTimer?.Stop(); closed.Cancel();
            components?.Dispose(); lifetime?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        Layout = new TableLayoutPanel(); Filtros = new FlowLayoutPanel(); Comandos = new FlowLayoutPanel(); Rodape = new FlowLayoutPanel();
        Operacao = new TextBox(); Correlacao = new TextBox(); Documento = new TextBox(); Estado = new ComboBox();
        De = new DateTimePicker(); Ate = new DateTimePicker();
        Atualizar = new Button(); Todos = new Button(); Pendentes = new Button(); Falhas = new Button(); Indeterminados = new Button();
        AutoRefresh = new CheckBox(); Indicadores = new Label(); Projecao = new Label(); Ambiente = new Label();
        Banco = new Label(); UltimoRefresh = new Label(); Escopo = new Label();
        Integracoes = new DataGridView(); Tentativas = new DataGridView(); Auditoria = new DataGridView(); Guardian = new DataGridView();
        FluxoAcm = new DataGridView(); IdentidadesAcm = new TextBox(); AcmTab = new TabPage();
        Geral = new TextBox(); Detalhes = new TabControl(); GeralTab = new TabPage(); TentativasTab = new TabPage(); AuditoriaTab = new TabPage(); GuardianTab = new TabPage();
        RefreshTimer = new System.Windows.Forms.Timer(components);
        SuspendLayout(); Layout.SuspendLayout();
        Font = new Font("Segoe UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleDimensions = new SizeF(7F, 13F); AutoScaleMode = AutoScaleMode.Font;
        Text = "FugaTES - Monitor operacional"; Name = "FrmMonitorMvp";
        ClientSize = new Size(1280, 800); MinimumSize = new Size(980, 700); StartPosition = FormStartPosition.CenterParent;
        Layout.Dock = DockStyle.Fill; Layout.Padding = new Padding(12); Layout.ColumnCount = 1; Layout.RowCount = 7;
        Layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86)); Layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        Layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); Layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        Layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35)); Layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        Layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Filtros.Dock = DockStyle.Fill; Filtros.AutoScroll = true; Filtros.WrapContents = true; Filtros.TabIndex = 0;
        Operacao.PlaceholderText = "Operacao / Fluxo"; Operacao.Width = 190; Operacao.TabIndex = 0;
        Estado.DropDownStyle = ComboBoxStyle.DropDownList; Estado.Width = 205; Estado.TabIndex = 1;
        Estado.Items.AddRange(new object[] { "", "PENDENTE", "EM_PROCESSAMENTO", "AGUARDANDO_REPROCESSAMENTO", "AGUARDANDO_ACAO_MANUAL", "RESULTADO_INDETERMINADO", "CONCLUIDA", "FALHA_DEFINITIVA", "CANCELADA" }); Estado.SelectedIndex = 0;
        Correlacao.PlaceholderText = "CorrelationId"; Correlacao.Width = 180; Correlacao.TabIndex = 2;
        Documento.PlaceholderText = "Documento / Referencia"; Documento.Width = 180; Documento.TabIndex = 3;
        De.Format = DateTimePickerFormat.Short; De.ShowCheckBox = true; De.Checked = false; De.Width = 130; De.TabIndex = 4; De.AccessibleName = "Periodo inicial";
        Ate.Format = DateTimePickerFormat.Short; Ate.ShowCheckBox = true; Ate.Checked = false; Ate.Width = 130; Ate.TabIndex = 5; Ate.AccessibleName = "Periodo final";
        var operacaoLabel = new Label { Text = "Operacao/Fluxo", AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
        var estadoLabel = new Label { Text = "Estado", AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
        var correlacaoLabel = new Label { Text = "CorrelationId", AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
        var documentoLabel = new Label { Text = "Documento", AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
        var deLabel = new Label { Text = "De", AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
        var ateLabel = new Label { Text = "Ate", AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
        Filtros.Controls.AddRange(new Control[] { operacaoLabel, Operacao, estadoLabel, Estado, correlacaoLabel, Correlacao, documentoLabel, Documento, deLabel, De, ateLabel, Ate });
        Comandos.Dock = DockStyle.Fill; Comandos.AutoScroll = true; Comandos.WrapContents = false; Comandos.TabIndex = 1;
        Atualizar.Text = "Atualizar"; Atualizar.TabIndex = 0; Atualizar.Width = 85;
        Todos.Text = "Todos"; Todos.TabIndex = 1; Todos.Width = 75;
        Pendentes.Text = "Pendentes"; Pendentes.TabIndex = 2; Pendentes.Width = 85;
        Falhas.Text = "Falhas"; Falhas.TabIndex = 3; Falhas.Width = 75;
        Indeterminados.Text = "Indeterminados"; Indeterminados.TabIndex = 4; Indeterminados.Width = 115;
        AutoRefresh.Text = "Automatico (5 s)"; AutoRefresh.Checked = true; AutoRefresh.AutoSize = true; AutoRefresh.TabIndex = 5;
        Comandos.Controls.AddRange(new Control[] { Atualizar, Todos, Pendentes, Falhas, Indeterminados, AutoRefresh });
        Indicadores.Dock = DockStyle.Fill; Indicadores.Text = "Indicadores indisponiveis"; Indicadores.BackColor = Color.FromArgb(239, 244, 247); Indicadores.Padding = new Padding(8); Indicadores.AutoEllipsis = true; Indicadores.TabIndex = 2;
        Projecao.Dock = DockStyle.Fill; Projecao.Text = "Projecao MVP: nao verificada"; Projecao.TabIndex = 3;
        Integracoes.Dock = DockStyle.Fill; Integracoes.ReadOnly = true; Integracoes.AllowUserToAddRows = false; Integracoes.AllowUserToDeleteRows = false; Integracoes.SelectionMode = DataGridViewSelectionMode.FullRowSelect; Integracoes.MultiSelect = false; Integracoes.RowHeadersVisible = false; Integracoes.TabIndex = 4; Integracoes.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
        Detalhes.Dock = DockStyle.Fill; Detalhes.TabIndex = 5;
        GeralTab.Text = "Geral"; TentativasTab.Text = "Tentativas"; AuditoriaTab.Text = "Auditoria"; GuardianTab.Text = "Guardian";
        AcmTab.Text = "Fluxo ACM";
        IdentidadesAcm.Name = "IdentidadesAcm"; IdentidadesAcm.Multiline = true; IdentidadesAcm.ReadOnly = true;
        IdentidadesAcm.Dock = DockStyle.Top; IdentidadesAcm.Height = 100; IdentidadesAcm.ScrollBars = ScrollBars.Vertical;
        FluxoAcm.Name = "FluxoAcm"; FluxoAcm.Dock = DockStyle.Fill; FluxoAcm.ReadOnly = true;
        FluxoAcm.AllowUserToAddRows = false; FluxoAcm.AllowUserToDeleteRows = false;
        FluxoAcm.RowHeadersVisible = false; FluxoAcm.AutoGenerateColumns = false;
        FluxoAcm.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        FluxoAcm.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        FluxoAcm.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        FluxoAcm.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
        FluxoAcm.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Etapa", HeaderText = "Etapa", FillWeight = 25 });
        FluxoAcm.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Estado", HeaderText = "Estado", FillWeight = 16 });
        FluxoAcm.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Evidencia", HeaderText = "Evidencia / impedimento", FillWeight = 44 });
        FluxoAcm.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "DataHora", HeaderText = "Data/hora", FillWeight = 15, DefaultCellStyle = new DataGridViewCellStyle { Format = "yyyy-MM-dd HH:mm:ss" } });
        AcmTab.Controls.Add(FluxoAcm); AcmTab.Controls.Add(IdentidadesAcm);
        Geral.Multiline = true; Geral.ReadOnly = true; Geral.Dock = DockStyle.Fill; Geral.ScrollBars = ScrollBars.Vertical; Geral.TabIndex = 0;
        Tentativas.Dock = DockStyle.Fill; Tentativas.ReadOnly = true; Tentativas.AllowUserToAddRows = false; Tentativas.AllowUserToDeleteRows = false; Tentativas.TabIndex = 0;
        Auditoria.Dock = DockStyle.Fill; Auditoria.ReadOnly = true; Auditoria.AllowUserToAddRows = false; Auditoria.AllowUserToDeleteRows = false; Auditoria.TabIndex = 0;
        Guardian.Dock = DockStyle.Fill; Guardian.ReadOnly = true; Guardian.AllowUserToAddRows = false; Guardian.AllowUserToDeleteRows = false; Guardian.TabIndex = 0;
        GeralTab.Controls.Add(Geral); TentativasTab.Controls.Add(Tentativas); AuditoriaTab.Controls.Add(Auditoria); GuardianTab.Controls.Add(Guardian);
        ProtocolTab = new TabPage { Text = "PROTOCOLO GUARDIAN", Name = "ProtocolTab" };
        GenerateProtocol = new Button { Text = "Gerar JSON", Name = "GenerateProtocol", AutoSize = true };
        CopyProtocol = new Button { Text = "Copiar JSON", Name = "CopyProtocol", AutoSize = true, Enabled = false };
        SendProtocol = new Button { Text = "Enviar protocolo", Name = "SendProtocol", AutoSize = true, Enabled = false };
        ProtocolLockReason = new Label { Name = "ProtocolLockReason", Text = "BACKEND_COMMAND_DISABLED", Dock = DockStyle.Fill, AutoEllipsis = true };
        ProtocolJson = new TextBox { Name = "ProtocolJson", Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        ProtocolMetadata = new TextBox { Name = "ProtocolMetadata", Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill };
        ProtocolResult = new TextBox { Name = "ProtocolResult", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        var protocolLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(3) };
        protocolLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        protocolLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 83));
        protocolLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        protocolLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        protocolLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        var protocolFrame = new GroupBox { Text = "Comando backend", Dock = DockStyle.Fill };
        var protocolCommands = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 31, WrapContents = false, AutoScroll = true };
        protocolCommands.Controls.AddRange(new Control[] { GenerateProtocol, CopyProtocol, SendProtocol });
        var protocolFrameLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        protocolFrameLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        protocolFrameLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        protocolFrameLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        protocolFrameLayout.Controls.Add(protocolCommands, 0, 0);
        protocolFrameLayout.Controls.Add(ProtocolLockReason, 0, 1);
        protocolFrame.Controls.Add(protocolFrameLayout);
        protocolLayout.Controls.Add(protocolFrame, 0, 0);
        protocolLayout.Controls.Add(ProtocolMetadata, 0, 1);
        protocolLayout.Controls.Add(ProtocolJson, 0, 2);
        protocolLayout.Controls.Add(ProtocolResult, 0, 3);
        ProtocolTab.Controls.Add(protocolLayout);
        Detalhes.TabPages.AddRange(new[] { AcmTab, ProtocolTab, GeralTab, TentativasTab, AuditoriaTab, GuardianTab });
        Rodape.Dock = DockStyle.Fill; Rodape.AutoScroll = true; Rodape.WrapContents = false; Rodape.TabIndex = 6;
        Ambiente.AutoSize = true; Banco.AutoSize = true; UltimoRefresh.AutoSize = true; Escopo.AutoSize = true;
        Ambiente.Text = "Ambiente: nao informado"; Banco.Text = "Banco desconectado"; UltimoRefresh.Text = "Ultimo refresh: -";
        Rodape.Controls.AddRange(new Control[] { Ambiente, Banco, UltimoRefresh, Escopo });
        Layout.Controls.Add(Filtros, 0, 0); Layout.Controls.Add(Comandos, 0, 1); Layout.Controls.Add(Indicadores, 0, 2); Layout.Controls.Add(Projecao, 0, 3);
        Layout.Controls.Add(Integracoes, 0, 4); Layout.Controls.Add(Detalhes, 0, 5); Layout.Controls.Add(Rodape, 0, 6);
        RefreshTimer.Interval = 5000; Controls.Add(Layout);
        Layout.ResumeLayout(false); ResumeLayout(false);
    }
}
