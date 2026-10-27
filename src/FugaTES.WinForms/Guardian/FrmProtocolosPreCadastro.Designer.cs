#nullable enable
using FugaTES.Aplicacao.Consultas.ProtocolosGuardian;

namespace FugaTES.WinForms.Guardian;

public sealed partial class FrmProtocolosPreCadastro
{
    private TextBox contrato = null!;
    private TextBox referencia = null!;
    private TextBox protocolo = null!;
    private ComboBox situacao = null!;
    private Button atualizar = null!;
    private Button contratoNavegar = null!;
    private DataGridView grid = null!;
    private Label status = null!;
    private DataGridViewButtonColumn json = null!;
    private DataGridViewButtonColumn reenviar = null!;

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
        Text = "FugaTES - Protocolos de Pre-Cadastro (Guardian REST)";
        ClientSize = new Size(1320, 640);
        MinimumSize = new Size(980, 460);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        var filtros = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        void Campo(string rotulo, Control controle)
        {
            filtros.Controls.Add(new Label { Text = rotulo, AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
            filtros.Controls.Add(controle);
        }
        contrato = new TextBox { Width = 120, AccessibleName = "Contrato" };
        referencia = new TextBox { Width = 110, AccessibleName = "Referencia OC" };
        protocolo = new TextBox { Width = 110, AccessibleName = "Protocolo" };
        situacao = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Situacao" };
        situacao.Items.AddRange([.. FiltroProtocoloPreCadastro.Situacoes]);
        situacao.SelectedIndex = 0;
        Campo("Contrato", contrato);
        Campo("Referencia OC", referencia);
        Campo("Protocolo", protocolo);
        Campo("Situacao", situacao);
        atualizar = new Button { Text = "Consultar", Width = 100, Height = 28, Margin = new Padding(12, 0, 0, 0) };
        filtros.Controls.Add(atualizar);
        contratoNavegar = new Button { Text = "Abrir contrato SAP", Width = 150, Height = 28, Margin = new Padding(8, 0, 0, 0) };
        filtros.Controls.Add(contratoNavegar);

        grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        };
        void Coluna(string campo, string titulo) => grid.Columns.Add(new DataGridViewTextBoxColumn
            { DataPropertyName = campo, HeaderText = titulo, Name = campo });
        Coluna("Contrato", "Contrato SAP");
        Coluna("Item", "Item");
        Coluna("Referencia", "Referencia OC");
        Coluna("Protocolo", "Protocolo Guardian");
        Coluna("TentativaEm", "Tentativa (UTC)");
        Coluna("StatusHttp", "Status HTTP");
        Coluna("EstadoEmpresarial", "Estado empresarial");
        Coluna("Tentativas", "Tentativas");
        Coluna("UltimoErro", "Ultima mensagem de erro");
        Coluna("Correlacao", "Correlacao");
        Coluna("Origem", "Origem");
        Coluna("Reenvio", "Reenvio");
        Coluna("Integracao", "IntegrationId");
        json = new DataGridViewButtonColumn
        {
            Name = "json", HeaderText = "JSON", Text = "JSON", UseColumnTextForButtonValue = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 70,
            ToolTipText = "Abrir o JSON enviado, a resposta e a execucao desta tentativa",
        };
        grid.Columns.Add(json);
        reenviar = new DataGridViewButtonColumn
        {
            Name = "reenviar", HeaderText = "Reenviar", Text = "Reenviar", UseColumnTextForButtonValue = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 90,
            ToolTipText = "Avaliar a elegibilidade do reenvio da etapa REST; nenhum envio e realizado",
        };
        grid.Columns.Add(reenviar);

        status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };

        layout.Controls.Add(filtros, 0, 0);
        layout.Controls.Add(grid, 0, 1);
        layout.Controls.Add(status, 0, 2);
        Controls.Add(layout);
    }
}
