#nullable enable
using FugaTES.Aplicacao.Consultas.ProtocolosGuardian;

namespace FugaTES.WinForms.Guardian;

/// <summary>
/// Monitor dos pre-cadastros Guardian REST: o que foi registrado de cada tentativa,
/// o JSON de cada uma e quais falhas podem ser recuperadas.
///
/// Somente leitura. Nenhuma chamada externa parte desta tela: a acao Reenviar apresenta
/// a elegibilidade e o bloqueio operacional, e a execucao pertence ao mecanismo de
/// comandos autorizado.
/// </summary>
public sealed partial class FrmProtocolosPreCadastro : Form
{
    private readonly ProtocolosPreCadastroConsulta consulta;
    private readonly IDisposable? lifetime;
    private readonly Action<ProtocoloPreCadastroLinha, IWin32Window>? abrirContrato;
    private readonly CancellationTokenSource cancelamento = new();
    private IReadOnlyList<ProtocoloPreCadastroExibicao> linhas = [];

    public FrmProtocolosPreCadastro(ProtocolosPreCadastroConsulta consulta, IDisposable? lifetime = null,
        Action<ProtocoloPreCadastroLinha, IWin32Window>? abrirContrato = null)
    {
        this.consulta = consulta ?? throw new ArgumentNullException(nameof(consulta));
        this.lifetime = lifetime;
        this.abrirContrato = abrirContrato;
        InitializeComponent();
        contratoNavegar.Visible = abrirContrato is not null;
        atualizar.Click += async (_, _) => await ConsultarAsync();
        contratoNavegar.Click += (_, _) => Navegar();
        grid.CellContentClick += (_, e) => Acionar(e.RowIndex, e.ColumnIndex);
        Shown += async (_, _) => await ConsultarAsync();
    }

    private FiltroProtocoloPreCadastro Filtro() => new()
    {
        Contrato = contrato.Text,
        Referencia = referencia.Text,
        Protocolo = protocolo.Text,
        Situacao = situacao.SelectedItem as string ?? FiltroProtocoloPreCadastro.Todas,
    };

    public async Task ConsultarAsync()
    {
        atualizar.Enabled = false;
        status.Text = "Consultando pre-cadastros...";
        try
        {
            var resultado = await consulta.ConsultarAsync(Filtro(), cancelamento.Token);
            linhas = resultado.Select(ProtocoloPreCadastroExibicao.De).ToArray();
            grid.DataSource = linhas.ToArray();
            var elegiveis = resultado.Count(l => ReenvioProtocolo.Avaliar(l).Recuperavel);
            status.Text = resultado.Count == 0
                ? "Nenhum pre-cadastro registrado para o filtro informado. Protocolos ausentes nao sao fabricados."
                : $"{resultado.Count} tentativa(s); {elegiveis} com falha recuperavel confirmada. "
                  + "Status HTTP e corpo da resposta nao possuem origem persistida e aparecem como UNKNOWN.";
        }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested) { }
        catch (ArgumentException ex)
        {
            status.Text = "Filtro invalido: " + ex.Message;
        }
        catch (Exception ex)
        {
            // A falha e exibida como falha: nenhuma linha e inventada para preencher a grade.
            grid.DataSource = Array.Empty<ProtocoloPreCadastroExibicao>();
            linhas = [];
            status.Text = "Falha de conectividade ou leitura dos pre-cadastros: " + ex.Message;
        }
        finally { atualizar.Enabled = true; }
    }

    private ProtocoloPreCadastroLinha? Selecionada()
        => grid.CurrentRow?.DataBoundItem is ProtocoloPreCadastroExibicao exibicao ? exibicao.Origemlinha : null;

    private void Navegar()
    {
        if (Selecionada() is not { } linha) { status.Text = "Selecione um pre-cadastro."; return; }
        abrirContrato?.Invoke(linha, this);
    }

    public void Acionar(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || rowIndex >= grid.Rows.Count ||
            columnIndex < 0 || columnIndex >= grid.Columns.Count) return;
        if (grid.Rows[rowIndex].DataBoundItem is not ProtocoloPreCadastroExibicao exibicao) return;
        var nome = grid.Columns[columnIndex].Name;
        if (string.Equals(nome, "json", StringComparison.Ordinal))
        {
            using var janela = new FrmProtocoloJson(exibicao.Origemlinha);
            janela.ShowDialog(this);
            return;
        }
        if (string.Equals(nome, "reenviar", StringComparison.Ordinal)) Reenviar(exibicao.Origemlinha);
    }

    /// <summary>
    /// Apresenta a elegibilidade e, quando bloqueada, o motivo. Nenhum POST e realizado:
    /// a execucao do reenvio pertence ao mecanismo de comandos autorizado.
    /// </summary>
    public void Reenviar(ProtocoloPreCadastroLinha linha)
    {
        var avaliacao = ReenvioProtocolo.Avaliar(linha);
        var texto = ReenvioProtocolo.TextoConfirmacao(linha, avaliacao);
        if (!avaliacao.Recuperavel)
        {
            MessageBox.Show(this, texto, "Reenvio bloqueado — " + avaliacao.Codigo,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            status.Text = "Reenvio bloqueado: " + avaliacao.Codigo;
            return;
        }
        MessageBox.Show(this,
            texto + "\r\n\r\nNENHUM ENVIO FOI REALIZADO. A execucao depende do mecanismo de comandos "
            + "autorizado, homologado fora desta tela.",
            "Reenvio elegivel — sem execucao", MessageBoxButtons.OK, MessageBoxIcon.Information);
        status.Text = "Reenvio elegivel, sem execucao: " + avaliacao.Codigo;
    }
}
