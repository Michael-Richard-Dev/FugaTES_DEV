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
        // Ligar a criacao automatica muda o que a tela FAZ, nao o que ela mostra: a partir dai
        // toda linha nova vira protocolo real sem ninguem clicar. Por isso a confirmacao.
        automaticoProtocolo.CheckedChanged += (_, _) =>
        {
            if (!automaticoProtocolo.Checked) { status.Text = "Criação automática desligada."; return; }
            var confirma = MessageBox.Show(this,
                "A partir de agora, toda linha trazida pela busca que ainda não tenha protocolo "
                + "vai gerar um protocolo REAL no Guardian, sem confirmação."
                + "\r\n\r\nIsso inclui todos os tipos de documento, porque o recorte de balança "
                + "ainda não foi definido. Protocolo criado não pode ser desfeito."
                + $"\r\n\r\nNo máximo {ProtocolosPorVarredura} por varredura; o restante vai nas seguintes."
                + "\r\n\r\nLigar a criação automática?",
                "Confirmar criação automática", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirma != DialogResult.Yes) { automaticoProtocolo.Checked = false; return; }
            status.Text = "Criação automática LIGADA. A próxima busca já cria protocolos.";
        };

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
            await ApresentarAsync(resultado, inicio);
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

    private async Task ApresentarAsync(ResultadoConsultaSap<IReadOnlyList<OrdemVendaSdLeitura>> resultado, DateTime inicio)
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
        status.Text = $"{DateTime.Now:HH:mm:ss} — {linhas.Count} linha(s) em {ordens} ordem(ns), {decorrido} ms, "
            + "somente OverallSDProcessStatus = A. Leitura apenas: nada foi gravado nem enviado."
            + (criacao is null ? "" : "   ➜ " + criacao)
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

    private sealed record DesfechoEnvio(bool Criado, string Resumo, string? Detalhe);

    /// <summary>
    /// Reserva, envia e fecha o desfecho. Sem interface: e o mesmo caminho do botao e do modo
    /// automatico, para o que acontece sozinho ser exatamente o que o operador conferiu a mao.
    /// </summary>
    private async Task<DesfechoEnvio> ExecutarEnvioAsync(
        ProtocoloPreCadastroSdRequest pedido, string chave, OrdemVendaSdLeitura linha)
    {
        // RESERVA a chave antes de enviar. Quem arbitra a corrida entre duas janelas e o indice
        // unico do banco, nao uma leitura previa que ficaria velha entre o conferir e o gravar.
        // E se o processo cair durante o POST, a linha fica ocupada como desfecho desconhecido,
        // em vez de livre para um segundo envio.
        var dados = pedido.DadosProtocoloPreCadastro;
        try
        {
            await registro!.RegistrarAsync(new ProtocoloSdEnvio(
                ChaveNatural: chave,
                OrdemVenda: chave[..chave.IndexOf('/')],
                LinhaOrdem: chave[(chave.IndexOf('/') + 1)..],
                Centro: linha.Centro!,
                CodigoItemGuardian: dados.CodigoItem,
                CodigoEmissorGuardian: dados.CodigoEmissor,
                ProtocoloId: null,
                Estado: EstadoProtocoloSd.DesfechoDesconhecido,
                HttpStatus: 0,
                PayloadSha256: GuardianProtocoloSdClient.Impressao(pedido),
                UrlRetorno: pedido.Configuracao.UrlParaRetorno,
                MensagemRecusa: null), cancelamento.Token);
        }
        catch (ProtocoloSdJaRegistradoException)
        { return new(false, "Esta linha já tem protocolo — outra janela chegou primeiro. Nada foi enviado.", null); }
        catch (Exception ex)
        { return new(false, $"Não foi possível reservar a linha ({ex.GetType().Name}). Nada foi enviado.", null); }

        try
        {
            var resultado = await guardian!.EnviarAsync(pedido, chave, cancelamento.Token);
            await registro.AtualizarDesfechoAsync(chave, resultado.ProtocoloId,
                resultado.Sucesso ? EstadoProtocoloSd.Criado : EstadoProtocoloSd.Recusado,
                resultado.HttpStatus, resultado.MensagemSegura, cancelamento.Token);
            return new(resultado.Sucesso,
                resultado.Sucesso
                    ? $"Protocolo criado. HTTP {resultado.HttpStatus}, id {resultado.ProtocoloId?.ToString() ?? "não devolvido"}."
                    : $"Guardian recusou. HTTP {resultado.HttpStatus}.",
                (resultado.Sucesso ? "// ENVIADO — protocolo criado no Guardian" : "// RECUSADO pelo Guardian")
                    + "\r\n// HTTP " + resultado.HttpStatus
                    + "\r\n// id do protocolo: " + (resultado.ProtocoloId?.ToString() ?? "não devolvido")
                    + "\r\n// sha256 do payload: " + resultado.PayloadSha256
                    + (resultado.MensagemSegura is null ? "" : "\r\n// resposta: " + resultado.MensagemSegura));
        }
        catch (Exception ex)
        {
            // A reserva ja esta gravada como DESFECHO_DESCONHECIDO, que e a verdade: o POST pode
            // ter chegado. A linha segue bloqueada ate alguem conferir no Guardian.
            return new(false, $"Desfecho desconhecido ({ex.GetType().Name}: {ex.Message}). "
                + "A linha ficou bloqueada. Confira no Guardian antes de liberar.", null);
        }
    }

    /// <summary>
    /// Modo automatico: cria protocolo para as linhas que ainda nao tem, logo apos a varredura.
    ///
    /// O alcance e limitado pelo numero de LINHAS distintas, nao pelo tempo - cada linha so pode
    /// virar protocolo uma vez, e quem garante isso e o indice unico do banco. O teto por varredura
    /// existe so para a primeira ativacao nao virar uma rajada; o que sobrar vai na proxima volta.
    /// </summary>
    private async Task<string?> CriarProtocolosPendentesAsync()
    {
        if (!automaticoProtocolo.Checked || registro is null || guardian is null) return null;

        var criados = 0;
        var recusados = 0;
        foreach (DataGridViewRow linhaGrade in grid.Rows)
        {
            if (criados + recusados >= ProtocolosPorVarredura) break;
            if (cancelamento.IsCancellationRequested) return null;
            if (linhaGrade.DataBoundItem is not OrdemVendaSdLeitura l) continue;

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
