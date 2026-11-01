using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.Integracao.Guardian;
using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.WinForms.Sd;
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
    private readonly CadastroGuardianConhecido cadastro;
    private readonly IEnviarProtocoloSdGateway? guardian;

    /// <summary>
    /// Registro duravel no PostgreSQL. Nulo quando o banco nao esta disponivel - e entao a tela
    /// NAO envia: sem poder conferir se a linha ja virou protocolo, enviar seria apostar.
    ///
    /// Preenchido na PRIMEIRA busca, nao na abertura. Abrir aqui exigia bloquear a thread da
    /// interface esperando o tunel SSH, o que travava a tela inteira - e travava de vez quando a
    /// continuacao tentava voltar para a thread que estava bloqueada.
    /// </summary>
    private IRegistroProtocolosSd? registro;
    private readonly Func<CancellationToken, Task<(IRegistroProtocolosSd?, IDisposable?, string?)>>? abrirRegistro;
    private IDisposable? posseRegistro;
    private bool registroTentado;
    private string? motivoSemRegistro;
    private IReadOnlyList<ProtocoloSdRegistrado> jaEnviados = [];
    private bool buscando;

    /// <summary>
    /// Teto de protocolos por varredura no modo automatico. Nao e um limite de seguranca - quem
    /// impede duplicata e o indice unico do banco, e cada linha so vira protocolo uma vez. Serve
    /// para a primeira ativacao nao virar uma rajada; o que sobrar vai na volta seguinte.
    ///
    /// Substituiu o limite de um envio por sessao, que o operador autorizou em 31/10/2026 para a
    /// primeira prova e suspendeu no mesmo dia ao liberar o modo automatico.
    /// </summary>
    public const int ProtocolosPorVarredura = 10;

    /// <summary>
    /// Url de retorno do protocolo. Editavel na tela de proposito: ela ainda vai ser trocada pelo
    /// receptor do FugaTES, e por enquanto aponta para a caixa de captura do operador.
    /// </summary>
    public const string UrlRetornoInicial = "https://webhook.site/717543aa-e052-4a2d-95b1-53983c881c1e";

    /// <param name="bloqueio">
    /// Quando informado, a tela abre sem poder consultar e exibe este motivo. Existe porque o
    /// objetivo dela e descobrir o que falta para a busca funcionar: nao abrir esconderia a causa.
    /// </param>
    public FrmOrdensVendaSd(IConsultaOrdensVendaSdSap consulta, IDisposable? lifetime = null,
        string? bloqueio = null, CadastroGuardianConhecido? cadastro = null,
        IEnviarProtocoloSdGateway? guardian = null, IRegistroProtocolosSd? registro = null,
        string? motivoSemRegistro = null,
        Func<CancellationToken, Task<(IRegistroProtocolosSd?, IDisposable?, string?)>>? abrirRegistro = null)
    {
        this.cadastro = cadastro ?? CadastroGuardianConhecido.Desconhecido;
        this.guardian = guardian;
        this.registro = registro;
        this.motivoSemRegistro = motivoSemRegistro;
        this.abrirRegistro = abrirRegistro;
        // Um registro entregue pronto (teste) dispensa abrir conexao.
        registroTentado = registro is not null || abrirRegistro is null;
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
            // A varredura ja comeca ligada: a criacao de protocolo e o motor do fluxo, e motor
            // que so anda quando alguem lembra de marcar uma caixa nao e motor.
            automatico.Checked = true;
            status.Text = $"Organização {OrdensVendaSdFiltro.OrganizacaoFixa} e centro "
                + string.Join(" ou ", OrdensVendaSdFiltro.CentrosPermitidos) + " — fixos. "
                + "Somente ordens com OverallSDProcessStatus = A, mais recentes primeiro. "
                + "Tipos de documento em branco trazem TODOS os tipos dessa situação. "
                + $"A cada varredura, até {ProtocolosPorVarredura} linha(s) sem protocolo viram "
                + "protocolo REAL no Guardian, sem confirmação — apenas de ordens criadas a "
                + $"partir de {OrdensVendaSdFiltro.CorteAutomatico:dd/MM/yyyy}. "
                + "As anteriores aparecem na lista e só saem pelo botão Enviar.";
        }
        // posseRegistro carrega a fonte de dados e o tunel SSH abertos sob demanda.
        FormClosing += (_, _) =>
        { relogio.Stop(); cancelamento.Cancel(); posseRegistro?.Dispose(); lifetime?.Dispose(); };
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
        // Previsualizacao do protocolo: mostra o payload exato que SAIRIA. Nada e enviado.
        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "protocolo", HeaderText = "Protocolo", Text = "Prévia", UseColumnTextForButtonValue = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 80
        });
        // Coluna nao ligada aos dados do SAP: vem do registro de envios. Sem ela, saber se uma
        // linha ja virou protocolo exigiria abrir a previsualizacao de uma por uma.
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "jaEnviado", HeaderText = "Já criado", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 90
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
            await ApresentarAsync(resultado, inicio, filtro.MaximoPorVarredura);
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

    private async Task ApresentarAsync(ResultadoConsultaSap<IReadOnlyList<OrdemVendaSdLeitura>> resultado,
        DateTime inicio, int janela)
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
        var aviso = await MarcarJaEnviadosAsync();
        // A criacao vem DEPOIS da marcacao: e ela que diz quais linhas ja tem protocolo.
        var criacao = await CriarProtocolosPendentesAsync();
        var ordens = linhas.Select(l => l.CodigoOrdemVenda).Distinct(StringComparer.Ordinal).Count();
        // O aviso entra DEPOIS do resumo: escrever antes seria apaga-lo nesta mesma linha.
        // Janela cheia significa que o SAP tinha mais ordens do que coube no $top. Com a ordem
        // decrescente as que ficam de fora sao as mais antigas - mas dizer isso e melhor do que
        // truncar calado, que foi o que escondeu a ordem 4232 em 01/11/2026.
        var truncou = ordens >= janela
            ? $"janela cheia: {janela} ordens mais recentes. Ordens mais antigas ficaram de fora."
            : null;
        status.Text = $"{DateTime.Now:HH:mm:ss} — {linhas.Count} linha(s) em {ordens} ordem(ns), {decorrido} ms, "
            + "somente OverallSDProcessStatus = A, mais recentes primeiro. "
            + "Leitura apenas: nada foi gravado nem enviado."
            + (criacao is null ? "" : "   ➜ " + criacao)
            + (truncou is null ? "" : "   ⚠ " + truncou)
            + (aviso is null ? "" : "   ⚠ " + aviso);
    }

    /// <summary>
    /// Abre o JSON da linha - os campos da planilha com o nome OData de origem, nao a resposta
    /// inteira da ordem. Usa o payload ja trazido; nao consulta o SAP.
    /// </summary>
    internal void AbrirJsonSeAcionado(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || columnIndex < 0 || columnIndex >= grid.Columns.Count) return;
        var coluna = grid.Columns[columnIndex].Name;
        if (grid.Rows[rowIndex].DataBoundItem is not OrdemVendaSdLeitura linha) return;
        if (string.Equals(coluna, "protocolo", StringComparison.Ordinal)) { AbrirPreviaProtocolo(linha); return; }
        if (!string.Equals(coluna, "json", StringComparison.Ordinal)) return;

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

    /// <summary>
    /// Mostra o protocolo que SAIRIA para esta linha, ou o motivo de ela estar bloqueada. Nada e
    /// enviado: serializa pelo mesmo caminho do envio, para o que se confere aqui ser o que sai.
    /// </summary>
    private void AbrirPreviaProtocolo(OrdemVendaSdLeitura linha)
    {
        string texto;
        ProtocoloPreCadastroSdRequest? pedido = null;
        var chave = "";
        try
        {
            pedido = MapeadorProtocoloSd.Montar(linha, UrlRetornoInicial, cadastro);
            chave = MapeadorProtocoloSd.ChaveNatural(linha);
            texto = "// Nada foi enviado ainda. Previsualizacao do POST em"
                + "\r\n// https://guardian.cloudprix.com.br/api/v1/Guardian/ProtocoloPreCadastro"
                + "\r\n// Chave desta linha: " + chave
                + "\r\n\r\n" + Formatar(MapeadorProtocoloSd.Serializar(pedido));
        }
        catch (ProtocoloSdBloqueadoException ex)
        {
            texto = "// Esta linha NAO viraria protocolo."
                + "\r\n\r\nMotivo: " + ex.Codigo + "\r\n" + ex.Detalhe;
        }

        using var janela = new Form
        {
            Text = $"Protocolo — ordem {linha.CodigoOrdemVenda}, linha {linha.LinhaItem}",
            ClientSize = new Size(780, 560),
            StartPosition = FormStartPosition.CenterParent
        };
        var corpo = new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both, WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9F), Text = texto
        };
        var rodape = new Panel { Dock = DockStyle.Bottom, Height = 56 };
        var aviso = new Label { Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 0), AutoSize = false };
        var enviar = new Button { Text = "Enviar ao Guardian", Dock = DockStyle.Right, Width = 180, Enabled = false };
        rodape.Controls.Add(aviso);
        rodape.Controls.Add(enviar);
        janela.Controls.Add(corpo);
        janela.Controls.Add(rodape);

        // Leitura ja carregada pela busca. A conferencia que vale acontece de novo no clique, e a
        // palavra final e do indice unico do banco.
        var jaRegistrado = pedido is null
            ? null
            : jaEnviados.FirstOrDefault(p => string.Equals(p.ChaveNatural, chave, StringComparison.Ordinal));
        aviso.Text = Situacao(pedido, jaRegistrado);
        enviar.Enabled = pedido is not null && jaRegistrado is null && guardian is not null
            && registro is not null;
        enviar.Click += async (_, _) =>
        {
            enviar.Enabled = false;
            await EnviarAsync(pedido!, chave, linha, corpo, aviso);
        };
        janela.ShowDialog(this);
    }

    /// <summary>
    /// Preenche a coluna "Já criado" a partir do registro compartilhado. Em caso de duvida marca
    /// "?" em vez de deixar em branco: branco seria lido como "pode enviar".
    /// </summary>
    /// <summary>
    /// Abre a conexao com o registro na primeira vez que ela e necessaria, sem bloquear a
    /// interface. Uma unica tentativa por sessao da tela: insistir a cada busca faria a varredura
    /// de 30 segundos virar uma fila de conexoes penduradas.
    /// </summary>
    private async Task GarantirRegistroAsync()
    {
        if (registroTentado) return;
        registroTentado = true;
        status.Text = "Conectando ao registro de protocolos...";
        try { (registro, posseRegistro, motivoSemRegistro) = await abrirRegistro!(cancelamento.Token); }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested) { }
        catch (Exception erro) { motivoSemRegistro = "SD_BANCO_" + erro.GetType().Name; }
    }

    /// <summary>
    /// Preenche a coluna "Já criado". Devolve o aviso a ser mostrado, em vez de escrever na linha
    /// de status: quem chama escreve a linha DEPOIS, e o aviso era apagado no mesmo instante.
    /// </summary>
    private async Task<string?> MarcarJaEnviadosAsync()
    {
        void Marcar(string texto)
        { foreach (DataGridViewRow r in grid.Rows) r.Cells["jaEnviado"].Value = texto; }

        await GarantirRegistroAsync();
        if (registro is null)
        {
            Marcar("?");
            return "Registro de protocolos indisponível"
                + (motivoSemRegistro is null ? "" : " (" + motivoSemRegistro + ")") + ": envio bloqueado.";
        }
        try { jaEnviados = await registro.LerAsync(cancelamento.Token); }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested) { return null; }
        // Banco fora do ar marca "?" e nao vazio: vazio seria lido como "pode enviar". E diz o
        // motivo: "?" em toda a grade sem explicacao parece defeito da busca.
        catch (Npgsql.PostgresException erro)
        { jaEnviados = []; Marcar("?"); return $"Registro não pôde ser lido (SQLSTATE {erro.SqlState}): envio bloqueado."; }
        catch (Exception erro)
        { jaEnviados = []; Marcar("?"); return $"Registro não pôde ser lido ({erro.GetType().Name}): envio bloqueado."; }

        foreach (DataGridViewRow r in grid.Rows)
        {
            if (r.DataBoundItem is not OrdemVendaSdLeitura l) continue;
            string marca;
            try
            {
                var chave = MapeadorProtocoloSd.ChaveNatural(l);
                var achado = jaEnviados.FirstOrDefault(p => string.Equals(p.ChaveNatural, chave, StringComparison.Ordinal));
                marca = achado is null ? "" : achado.ProtocoloId?.ToString() ?? achado.Estado;
            }
            catch (ProtocoloSdBloqueadoException) { marca = ""; }
            r.Cells["jaEnviado"].Value = marca;
        }
        return null;
    }

    private string Situacao(ProtocoloPreCadastroSdRequest? pedido, ProtocoloSdRegistrado? registrado)
    {
        if (pedido is null) return "Linha bloqueada: nada a enviar.";
        if (registrado is not null)
            return $"Esta linha já tem protocolo ({registrado.ProtocoloId?.ToString() ?? registrado.Estado}, "
                + $"{registrado.QuandoUtc:dd/MM/yyyy HH:mm}). Não será enviada de novo.";
        if (guardian is null) return "Perfil do Guardian indisponível nesta sessão: só prévia.";
        // Sem registro nao ha como afirmar que a linha e nova. Enviar seria apostar.
        if (registro is null)
            return "Registro de protocolos (PostgreSQL 5433) indisponível"
                + (motivoSemRegistro is null ? "" : " — " + motivoSemRegistro)
                + ". Envio bloqueado: não dá para conferir se esta linha já virou protocolo.";
        return "Pronto para enviar. Cria um registro REAL no Guardian e não pode ser desfeito.";
    }

    private async Task EnviarAsync(ProtocoloPreCadastroSdRequest pedido, string chave,
        OrdemVendaSdLeitura linha, TextBox corpo, Label aviso)
    {
        var confirma = MessageBox.Show(this,
            $"Criar protocolo REAL no Guardian para a ordem {linha.CodigoOrdemVenda}, linha {linha.LinhaItem}?"
            + "\r\n\r\nItem: " + pedido.DadosProtocoloPreCadastro.CodigoItem
            + "   Emissor: " + pedido.DadosProtocoloPreCadastro.CodigoEmissor
            + "\r\nEsta ação não pode ser desfeita.",
            "Confirmar envio ao Guardian", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirma != DialogResult.Yes) { aviso.Text = "Envio cancelado. Nada foi enviado."; return; }

        var desfecho = await ExecutarEnvioAsync(pedido, chave, linha);
        aviso.Text = desfecho.Resumo;
        if (desfecho.Detalhe is not null) corpo.Text = desfecho.Detalhe + "\r\n\r\n" + corpo.Text;
        _ = await MarcarJaEnviadosAsync();
    }

    /// <summary>
    /// Reserva, envia e fecha o desfecho. A tela nao tem caminho proprio: chama o mesmo
    /// <see cref="CriadorProtocoloSd"/> que o motor da VM usa, para o que acontece sozinho ser
    /// exatamente o que o operador confere a mao.
    /// </summary>
    private Task<DesfechoProtocoloSd> ExecutarEnvioAsync(
        ProtocoloPreCadastroSdRequest pedido, string chave, OrdemVendaSdLeitura linha)
        => new CriadorProtocoloSd(registro!, guardian!)
            .ExecutarAsync(pedido, chave, linha.Centro!, cancelamento.Token);

    /// <summary>
    /// Cria protocolo para as linhas que ainda nao tem, logo apos cada varredura. E o motor do
    /// fluxo e esta SEMPRE ativo: nao depende de caixa marcada nem de ninguem clicar.
    ///
    /// O alcance e limitado pelo numero de LINHAS distintas, nao pelo tempo - cada linha so pode
    /// virar protocolo uma vez, e quem garante isso e o indice unico do banco. O teto por varredura
    /// existe so para nao virar uma rajada; o que sobrar vai na proxima volta.
    ///
    /// Sem registro ou sem Guardian nada e criado: conferir a duplicidade e condicao para enviar,
    /// e nao um detalhe que se possa pular quando o banco esta fora.
    /// </summary>
    private async Task<string?> CriarProtocolosPendentesAsync()
    {
        if (registro is null || guardian is null) return null;

        var criados = 0;
        var recusados = 0;
        foreach (DataGridViewRow linhaGrade in grid.Rows)
        {
            if (criados + recusados >= ProtocolosPorVarredura) break;
            if (cancelamento.IsCancellationRequested) return null;
            if (linhaGrade.DataBoundItem is not OrdemVendaSdLeitura l) continue;

            // Corte por data: o que acontece SOZINHO nao alcanca o acervo antigo. Ordem sem data
            // de criacao tambem nao passa - nao saber quando foi criada nao e o mesmo que ser
            // recente. Pelo botao Enviar, qualquer linha continua alcancavel.
            if (l.DataCriacao is not DateTime criada
                || DateOnly.FromDateTime(criada) < OrdensVendaSdFiltro.CorteAutomatico) continue;

            string chave;
            ProtocoloPreCadastroSdRequest pedido;
            // Linha bloqueada (item ou emissor fora do Guardian) e pulada em silencio: ela ja
            // aparece com o motivo na previsualizacao, e insistir a cada 30 segundos so faria
            // barulho.
            try { chave = MapeadorProtocoloSd.ChaveNatural(l); pedido = MapeadorProtocoloSd.Montar(l, UrlRetornoInicial, cadastro); }
            catch (ProtocoloSdBloqueadoException) { continue; }

            if (jaEnviados.Any(p => string.Equals(p.ChaveNatural, chave, StringComparison.Ordinal))) continue;

            var desfecho = await ExecutarEnvioAsync(pedido, chave, l);
            if (desfecho.Criado) criados++; else recusados++;
        }

        if (criados + recusados == 0) return null;
        _ = await MarcarJaEnviadosAsync();
        return $"automático: {criados} protocolo(s) criado(s)"
            + (recusados > 0 ? $", {recusados} não concluído(s)" : "") + ".";
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
