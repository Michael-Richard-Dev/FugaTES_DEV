using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Resultados.SAP;

namespace FugaTES.WinForms;

/// <summary>
/// SD: tela de acompanhamento da varredura de ordens de venda no SAP.
///
/// Somente leitura. Nao persiste, nao despacha e nao chama o Guardian - existe para validar a
/// busca no SAP enquanto o fluxo e construido. Os filtros sao editaveis de proposito: a planilha
/// de integracao diz "puxar so o que e pra balanca" sem listar os tipos de documento, e e aqui
/// que esse recorte vai ser descoberto.
/// </summary>
public partial class FrmOrdensVendaSd : Form
{
    /// <summary>Organizacao de vendas: fixa, so exibida.</summary>
    public const string OrganizacaoInicial = OrdensVendaSdFiltro.OrganizacaoFixa;

    private readonly IConsultaOrdensVendaSdSap consulta;
    private readonly IDisposable? lifetime;
    private readonly CancellationTokenSource cancelamento = new();
    private readonly System.Windows.Forms.Timer relogio;
    private readonly bool bloqueada;
    private bool buscando;

    /// <param name="bloqueio">
    /// Quando informado, a tela abre sem poder consultar e exibe este motivo. Existe porque o
    /// objetivo dela e descobrir o que falta para a busca funcionar: nao abrir esconderia a causa.
    /// </param>
    public FrmOrdensVendaSd(IConsultaOrdensVendaSdSap consulta, IDisposable? lifetime = null, string? bloqueio = null)
    {
        this.consulta = consulta ?? throw new ArgumentNullException(nameof(consulta));
        this.lifetime = lifetime;
        InitializeComponent();

        organizacao.Text = OrdensVendaSdFiltro.OrganizacaoFixa;
        centros.Items.AddRange([.. OrdensVendaSdFiltro.CentrosPermitidos]);
        centros.SelectedIndex = 0;
        tipos.PlaceholderText = "vazio = todos";

        MontarColunas();
        atualizar.Click += async (_, _) => await BuscarAsync();
        grid.CellContentClick += (_, e) => AbrirJsonSeAcionado(e.RowIndex, e.ColumnIndex);

        relogio = new System.Windows.Forms.Timer(components) { Interval = 30_000 };
        relogio.Tick += async (_, _) => await BuscarAsync();
        automatico.CheckedChanged += (_, _) => relogio.Enabled = automatico.Checked;

        bloqueada = !string.IsNullOrWhiteSpace(bloqueio);
        if (bloqueada)
        {
            atualizar.Enabled = false;
            automatico.Enabled = false;
            status.Text = bloqueio;
        }
        else
        {
            // A regra da situacao da ordem aparece aqui porque ela nao e editavel: sem isso,
            // zero linha pareceria falha de busca em vez de recorte.
            status.Text = $"Organização {OrdensVendaSdFiltro.OrganizacaoFixa} e centro "
                + string.Join(" ou ", OrdensVendaSdFiltro.CentrosPermitidos) + " — fixos. "
                + "Somente ordens com OverallSDProcessStatus = A. "
                + "Tipos de documento em branco trazem TODOS os tipos dessa situação. "
                + "Clique em Buscar agora.";
        }
        FormClosing += (_, _) => { relogio.Stop(); cancelamento.Cancel(); lifetime?.Dispose(); };
    }

    private void MontarColunas()
    {
        void Coluna(string nome, string titulo, string propriedade, int largura = 0)
        {
            var c = new DataGridViewTextBoxColumn { Name = nome, HeaderText = titulo, DataPropertyName = propriedade, ReadOnly = true };
            if (largura > 0) { c.AutoSizeMode = DataGridViewAutoSizeColumnMode.None; c.Width = largura; }
            grid.Columns.Add(c);
        }

        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "json", HeaderText = "JSON", Text = "Ver", UseColumnTextForButtonValue = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 60
        });
        // Ordem e titulos seguem a planilha de integracao, para conferencia campo a campo.
        Coluna("centro", "Centro", nameof(OrdemVendaSdLeitura.Centro), 70);
        Coluna("ordem", "Ordem de venda", nameof(OrdemVendaSdLeitura.CodigoOrdemVenda), 120);
        Coluna("linha", "Linha", nameof(OrdemVendaSdLeitura.LinhaItem), 70);
        Coluna("material", "Item da ordem", nameof(OrdemVendaSdLeitura.Material));
        Coluna("situacao", "Situação", nameof(OrdemVendaSdLeitura.SituacaoOrdem), 80);
        Coluna("tipo", "Tipo doc.", nameof(OrdemVendaSdLeitura.TipoDocumentoVendas), 90);
        Coluna("empresa", "Empresa", nameof(OrdemVendaSdLeitura.EmpresaOrdem), 80);
        Coluna("emissor", "Emissor", nameof(OrdemVendaSdLeitura.Emissor), 110);
        Coluna("setor", "Setor", nameof(OrdemVendaSdLeitura.SetorAtividade), 70);
        Coluna("recebedor", "Recebedor", nameof(OrdemVendaSdLeitura.RecebedorMercadoria), 100);
    }

    /// <summary>Le os filtros da tela. Devolve nulo quando o recorte nao fecha.</summary>
    public static OrdensVendaSdFiltro? Montar(string organizacao, string centros, string tipos)
    {
        static string[] Lista(string v) => (v ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var filtro = new OrdensVendaSdFiltro(
            (organizacao ?? string.Empty).Trim(), Lista(centros), Lista(tipos));
        return filtro.EhValido() ? filtro : null;
    }

    private async Task BuscarAsync()
    {
        if (buscando || bloqueada) return;
        var filtro = Montar(OrdensVendaSdFiltro.OrganizacaoFixa,
            centros.SelectedItem?.ToString() ?? string.Empty, tipos.Text);
        if (filtro is null)
        {
            // So acontece por defeito nosso: a organizacao e constante e o centro vem de lista
            // fechada. Dizer isso e melhor do que culpar o preenchimento do operador.
            status.Text = "Recorte recusado antes da busca: organização ou centro fora do aprovado. "
                + "Nada foi consultado.";
            return;
        }

        buscando = true;
        atualizar.Enabled = false;
        var inicio = DateTime.Now;
        try
        {
            var resultado = await consulta.ConsultarAsync(filtro, cancelamento.Token);
            if (cancelamento.IsCancellationRequested) return;
            Apresentar(resultado, inicio);
        }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested) { }
        // O timer dispara por um handler async void: uma excecao nao tratada aqui derrubaria o
        // aplicativo. Mostrar o tipo e a mensagem e menos ruim do que isso e nao esconde a causa.
        catch (Exception ex)
        {
            grid.DataSource = null;
            status.Text = $"{DateTime.Now:HH:mm:ss} — busca interrompida por {ex.GetType().Name}: {ex.Message}. "
                + "Nada foi gravado nem enviado.";
        }
        finally
        {
            buscando = false;
            if (!IsDisposed) atualizar.Enabled = true;
        }
    }

    private void Apresentar(ResultadoConsultaSap<IReadOnlyList<OrdemVendaSdLeitura>> resultado, DateTime inicio)
    {
        var decorrido = (DateTime.Now - inicio).TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        if (resultado.Status != StatusConsultaSap.Sucesso || resultado.Dados is null)
        {
            grid.DataSource = null;
            // O status cru do SAP e mostrado como veio: traduzir aqui esconderia a causa.
            status.Text = $"{DateTime.Now:HH:mm:ss} — busca não concluída: {resultado.Status} ({decorrido} ms). Nada foi alterado.";
            return;
        }

        var linhas = resultado.Dados;
        grid.DataSource = linhas.ToList();
        var ordens = linhas.Select(l => l.CodigoOrdemVenda).Distinct(StringComparer.Ordinal).Count();
        status.Text = $"{DateTime.Now:HH:mm:ss} — {linhas.Count} linha(s) em {ordens} ordem(ns), {decorrido} ms, "
            + "somente OverallSDProcessStatus = A. Leitura apenas: nada foi gravado nem enviado.";
    }

    /// <summary>
    /// Abre o JSON da linha - os campos da planilha com o nome OData de origem, nao a resposta
    /// inteira da ordem. Usa o payload ja trazido; nao consulta o SAP.
    /// </summary>
    internal void AbrirJsonSeAcionado(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || columnIndex < 0 || columnIndex >= grid.Columns.Count) return;
        if (!string.Equals(grid.Columns[columnIndex].Name, "json", StringComparison.Ordinal)) return;
        if (grid.Rows[rowIndex].DataBoundItem is not OrdemVendaSdLeitura linha) return;

        using var janela = new Form
        {
            Text = $"Ordem {linha.CodigoOrdemVenda} — linha {linha.LinhaItem} — campos do SAP",
            ClientSize = new Size(760, 520),
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
        catch (System.Text.Json.JsonException) { return json; }
    }
}
