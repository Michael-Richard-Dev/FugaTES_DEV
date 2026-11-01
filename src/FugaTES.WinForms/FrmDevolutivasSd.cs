using FugaTES.Aplicacao.Contratos.Sd;

namespace FugaTES.WinForms;

/// <summary>
/// SD: acompanhamento das pesagens que o Guardian devolve depois da balanca.
///
/// Somente leitura. Nao recebe o POST - quem recebe e o servico HTTP na VM - e nao chama o SAP.
/// Serve para ver o que chegou, se cada pesagem achou a ordem de venda a que pertence, e o
/// documento original como veio.
/// </summary>
public partial class FrmDevolutivasSd : Form
{
    private readonly IDevolutivasSdRepositorio? repositorio;
    private readonly string? motivoIndisponivel;
    private readonly IDisposable? lifetime;
    private readonly CancellationTokenSource cancelamento = new();
    private readonly System.Windows.Forms.Timer relogio;
    private bool consultando;

    public const int LimiteDeLinhas = 200;

    public FrmDevolutivasSd(IDevolutivasSdRepositorio? repositorio, IDisposable? lifetime = null,
        string? motivoIndisponivel = null)
    {
        this.repositorio = repositorio;
        this.motivoIndisponivel = motivoIndisponivel;
        this.lifetime = lifetime;
        InitializeComponent();
        MontarColunas();

        atualizar.Click += async (_, _) => await ConsultarAsync();
        grid.CellContentClick += (_, e) => AbrirDocumentoSeAcionado(e.RowIndex, e.ColumnIndex);
        relogio = new System.Windows.Forms.Timer(components) { Interval = 30_000 };
        relogio.Tick += async (_, _) => await ConsultarAsync();
        automatico.CheckedChanged += (_, _) => relogio.Enabled = automatico.Checked;

        status.Text = repositorio is null
            ? "Registro de devolutivas indisponível"
                + (motivoIndisponivel is null ? "" : " — " + motivoIndisponivel) + "."
            : "Clique em Atualizar. Leitura apenas: esta tela não recebe nem envia nada.";
        atualizar.Enabled = repositorio is not null;
        automatico.Enabled = repositorio is not null;

        FormClosing += (_, _) => { relogio.Stop(); cancelamento.Cancel(); lifetime?.Dispose(); };
    }

    private void MontarColunas()
    {
        void Coluna(string nome, string titulo, string propriedade, int largura = 0, string? formato = null)
        {
            var c = new DataGridViewTextBoxColumn
            { Name = nome, HeaderText = titulo, DataPropertyName = propriedade, ReadOnly = true };
            if (largura > 0) { c.AutoSizeMode = DataGridViewAutoSizeColumnMode.None; c.Width = largura; }
            if (formato is not null) c.DefaultCellStyle.Format = formato;
            grid.Columns.Add(c);
        }

        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "documento", HeaderText = "JSON", Text = "Ver", UseColumnTextForButtonValue = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 60
        });
        Coluna("recebido", "Recebido em", nameof(DevolutivaSdLinha.RecebidoEm), 140, "dd/MM/yyyy HH:mm:ss");
        Coluna("pesagem", "Id pesagem", nameof(DevolutivaSdLinha.IdPesagem), 100);
        Coluna("centro", "Centro", nameof(DevolutivaSdLinha.Centro), 70);
        Coluna("ordem", "Ordem", nameof(DevolutivaSdLinha.OrdemVenda), 90);
        Coluna("linha", "Linha", nameof(DevolutivaSdLinha.LinhaOrdem), 70);
        Coluna("protocolo", "Protocolo", nameof(DevolutivaSdLinha.ProtocoloId), 90);
        Coluna("estado", "Correlação", nameof(DevolutivaSdLinha.Estado), 130);
        Coluna("bruto", "Peso bruto", nameof(DevolutivaSdLinha.PesoBruto), 100, "N3");
        Coluna("tara", "Tara", nameof(DevolutivaSdLinha.PesoTara), 100, "N3");
        Coluna("liquido", "Peso líquido", nameof(DevolutivaSdLinha.PesoLiquido), 110, "N3");
        Coluna("placa", "Placa", nameof(DevolutivaSdLinha.Placa), 90);
        Coluna("motorista", "Motorista", nameof(DevolutivaSdLinha.NomeMotorista));
        Coluna("pesado", "Data da pesagem", nameof(DevolutivaSdLinha.DataPesagem), 140, "dd/MM/yyyy HH:mm:ss");
    }

    private async Task ConsultarAsync()
    {
        if (consultando || repositorio is null) return;
        consultando = true;
        atualizar.Enabled = false;
        var inicio = DateTime.Now;
        try
        {
            var linhas = await repositorio.ListarAsync(LimiteDeLinhas, cancelamento.Token);
            if (cancelamento.IsCancellationRequested) return;
            grid.DataSource = linhas.ToList();
            RealcarSemCorrelacao();

            var decorrido = (DateTime.Now - inicio).TotalMilliseconds
                .ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            var sem = linhas.Count(l => l.Estado == EstadoDevolutivaSd.SemCorrelacao);
            resumo.Text = $"{linhas.Count} devolutiva(s)" + (sem > 0 ? $"   —   {sem} sem correlação" : "");
            status.Text = $"{DateTime.Now:HH:mm:ss} — {linhas.Count} linha(s), {decorrido} ms. "
                + "Leitura apenas: nada foi recebido, gravado nem enviado por esta tela."
                + (sem > 0 ? $"   ⚠ {sem} pesagem(ns) chegaram sem indicar a ordem de venda." : "");
        }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested) { }
        // Handler async void: uma excecao nao tratada aqui derrubaria o aplicativo.
        catch (Exception erro)
        {
            grid.DataSource = null;
            status.Text = $"{DateTime.Now:HH:mm:ss} — consulta interrompida por {erro.GetType().Name}: {erro.Message}.";
        }
        finally
        {
            consultando = false;
            if (!IsDisposed) atualizar.Enabled = true;
        }
    }

    // Sem correlacao e o caso que exige alguem olhar: a pesagem existe e nao se sabe de qual
    // ordem e. Destacar evita que ela se perca no meio das que seguiram o caminho normal.
    private void RealcarSemCorrelacao()
    {
        foreach (DataGridViewRow r in grid.Rows)
            if (r.DataBoundItem is DevolutivaSdLinha l && l.Estado == EstadoDevolutivaSd.SemCorrelacao)
                r.DefaultCellStyle.BackColor = Color.FromArgb(255, 244, 214);
    }

    /// <summary>Abre o documento como o Guardian enviou. Nao consulta nada.</summary>
    internal void AbrirDocumentoSeAcionado(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || columnIndex < 0 || columnIndex >= grid.Columns.Count) return;
        if (!string.Equals(grid.Columns[columnIndex].Name, "documento", StringComparison.Ordinal)) return;
        if (grid.Rows[rowIndex].DataBoundItem is not DevolutivaSdLinha linha) return;

        using var janela = new Form
        {
            Text = $"Pesagem {linha.IdPesagem} — documento recebido do Guardian",
            ClientSize = new Size(820, 600),
            StartPosition = FormStartPosition.CenterParent
        };
        janela.Controls.Add(new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both, WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9F),
            Text = Formatar(linha.PayloadJson)
        });
        janela.ShowDialog(this);
    }

    private static string Formatar(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        // Guardar o documento como veio inclui guardar um documento torto: mostrar cru e melhor
        // do que esconder.
        catch (System.Text.Json.JsonException) { return json; }
    }
}
