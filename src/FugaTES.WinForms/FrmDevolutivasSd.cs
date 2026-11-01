using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.Integracao.SAP.BusinessDocuments;

namespace FugaTES.WinForms;

/// <summary>
/// SD: acompanhamento das pesagens que o Guardian devolve depois da balanca, e criacao da
/// remessa no SAP a partir delas (passo 3 do fluxo).
///
/// A tela nao RECEBE o POST do Guardian - quem recebe e o servico HTTP na VM. O que ela faz com
/// o SAP e sempre por acao explicita, nunca sozinha.
/// </summary>
public partial class FrmDevolutivasSd : Form
{
    private readonly IDevolutivasSdRepositorio? repositorio;
    private readonly IRemessasSdRepositorio? remessas;
    private readonly ICriarRemessaSdSap? sap;
    private readonly IConsultarSetorAtividadeSd? setores;
    private readonly IAjustarRemessaSdSap? ajuste;
    private readonly IGravarTextoNfeSd? textos;
    private readonly IExpedirRemessaSdSap? expedicao;
    private readonly string? motivoIndisponivel;
    private readonly IDisposable? lifetime;
    private readonly CancellationTokenSource cancelamento = new();
    private readonly System.Windows.Forms.Timer relogio;
    private IReadOnlyList<RemessaSdLinha> remessasConhecidas = [];
    private bool consultando;

    public const int LimiteDeLinhas = 200;

    public FrmDevolutivasSd(IDevolutivasSdRepositorio? repositorio, IDisposable? lifetime = null,
        string? motivoIndisponivel = null, IRemessasSdRepositorio? remessas = null,
        ICriarRemessaSdSap? sap = null, IConsultarSetorAtividadeSd? setores = null,
        IAjustarRemessaSdSap? ajuste = null, IGravarTextoNfeSd? textos = null,
        IExpedirRemessaSdSap? expedicao = null)
    {
        this.repositorio = repositorio;
        this.remessas = remessas;
        this.sap = sap;
        this.setores = setores;
        this.ajuste = ajuste;
        this.textos = textos;
        this.expedicao = expedicao;
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
            : "Clique em Atualizar. Esta tela não recebe o POST do Guardian; a remessa no SAP "
                + "só é criada pelo botão Criar, com prévia e confirmação.";
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
        // Passo 3: previsualizacao e criacao da remessa. Nao ha caminho de enviar sem ver antes.
        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "remessa", HeaderText = "Remessa", Text = "Criar", UseColumnTextForButtonValue = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 70
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "numeroRemessa", HeaderText = "Nº remessa", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 100
        });
        // Passo 4: peso da balanca e transporte no cabecalho; itens zerados no biodiesel.
        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "ajuste", HeaderText = "Ajuste", Text = "Ajustar", UseColumnTextForButtonValue = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 70
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "estadoAjuste", HeaderText = "Situação do ajuste", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 130
        });
        // Passo 5: texto ZNFE, preenchendo os espacos que vieram vazios da ordem.
        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "texto", HeaderText = "Texto NF-e", Text = "Compor", UseColumnTextForButtonValue = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 80
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "estadoTexto", HeaderText = "Situação do texto", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 130
        });
        // Passo 6: duas acoes separadas de proposito. Picking se refaz; saida de mercadoria nao.
        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "picking", HeaderText = "Picking", Text = "Confirmar", UseColumnTextForButtonValue = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 85
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "estadoPicking", HeaderText = "Situação do picking", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 130
        });
        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "saida", HeaderText = "Saída", Text = "Postar", UseColumnTextForButtonValue = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 75
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "estadoSaida", HeaderText = "Situação da saída", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 130
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
            await MarcarRemessasAsync();

            var decorrido = (DateTime.Now - inicio).TotalMilliseconds
                .ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            var sem = linhas.Count(l => l.Estado == EstadoDevolutivaSd.SemCorrelacao);
            resumo.Text = $"{linhas.Count} devolutiva(s)" + (sem > 0 ? $"   —   {sem} sem correlação" : "");
            status.Text = $"{DateTime.Now:HH:mm:ss} — {linhas.Count} linha(s), {decorrido} ms. "
                + "Nada foi enviado ao SAP por esta consulta."
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

    /// <summary>
    /// Preenche a coluna do numero da remessa. Em duvida marca "?", nunca vazio: vazio seria
    /// lido como "ainda nao criada" e convidaria uma segunda criacao.
    /// </summary>
    private async Task MarcarRemessasAsync()
    {
        void Marcar(string t)
        {
            foreach (DataGridViewRow r in grid.Rows)
            {
                r.Cells["numeroRemessa"].Value = t;
                r.Cells["estadoAjuste"].Value = t;
                r.Cells["estadoTexto"].Value = t;
                r.Cells["estadoPicking"].Value = t;
                r.Cells["estadoSaida"].Value = t;
            }
        }
        if (remessas is null) { Marcar("?"); return; }
        try { remessasConhecidas = await remessas.ListarAsync(LimiteDeLinhas, cancelamento.Token); }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested) { return; }
        catch (Exception) { remessasConhecidas = []; Marcar("?"); return; }

        foreach (DataGridViewRow r in grid.Rows)
        {
            if (r.DataBoundItem is not DevolutivaSdLinha l) continue;
            var achada = remessasConhecidas.FirstOrDefault(
                x => string.Equals(x.IdPesagem, l.IdPesagem, StringComparison.Ordinal));
            r.Cells["numeroRemessa"].Value = achada is null ? "" : achada.NumeroRemessa ?? achada.Estado;
            r.Cells["estadoAjuste"].Value = achada?.AjusteEstado ?? "";
            r.Cells["estadoTexto"].Value = achada?.TextoEstado ?? "";
            r.Cells["estadoPicking"].Value = achada?.PickingEstado ?? "";
            r.Cells["estadoSaida"].Value = achada?.SaidaEstado ?? "";
        }
    }

    /// <summary>Abre o documento como o Guardian enviou. Nao consulta nada.</summary>
    internal void AbrirDocumentoSeAcionado(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || columnIndex < 0 || columnIndex >= grid.Columns.Count) return;
        var coluna = grid.Columns[columnIndex].Name;
        if (grid.Rows[rowIndex].DataBoundItem is DevolutivaSdLinha alvo)
        {
            if (string.Equals(coluna, "remessa", StringComparison.Ordinal))
            { _ = AbrirRemessaAsync(alvo); return; }
            if (string.Equals(coluna, "ajuste", StringComparison.Ordinal))
            { _ = AbrirAjusteAsync(alvo); return; }
            if (string.Equals(coluna, "texto", StringComparison.Ordinal))
            { _ = AbrirTextoAsync(alvo); return; }
            if (string.Equals(coluna, "picking", StringComparison.Ordinal))
            { _ = AbrirExpedicaoAsync(alvo, EtapaExpedicaoSd.Picking); return; }
            if (string.Equals(coluna, "saida", StringComparison.Ordinal))
            { _ = AbrirExpedicaoAsync(alvo, EtapaExpedicaoSd.Saida); return; }
        }
        if (!string.Equals(coluna, "documento", StringComparison.Ordinal)) return;
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

    /// <summary>
    /// Passo 3: previsualiza e, so depois de confirmado, cria a remessa no SAP.
    ///
    /// A ordem e sempre a mesma do protocolo: RESERVAR no banco, enviar, fechar o desfecho. O
    /// indice unico de id_pesagem e quem arbitra a corrida - nao a leitura da coluna na tela.
    /// </summary>
    private async Task AbrirRemessaAsync(DevolutivaSdLinha linha)
    {
        string? Impedimento()
        {
            if (remessas is null || sap is null || setores is null)
                return "Criação de remessa indisponível nesta sessão"
                    + (motivoIndisponivel is null ? "" : " — " + motivoIndisponivel) + ".";
            if (linha.Estado == EstadoDevolutivaSd.SemCorrelacao)
                return $"Pesagem {linha.IdPesagem} chegou sem indicar a ordem de venda. "
                    + "Sem a ordem não há a que referenciar a remessa.";
            var ja = remessasConhecidas.FirstOrDefault(
                x => string.Equals(x.IdPesagem, linha.IdPesagem, StringComparison.Ordinal));
            if (ja is not null)
                return $"Pesagem {linha.IdPesagem} já foi usada: {ja.Estado}"
                    + (ja.NumeroRemessa is null ? "" : $", remessa {ja.NumeroRemessa}") + ".";
            return null;
        }

        if (Impedimento() is string impedimento)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — {impedimento}";
            MessageBox.Show(this, impedimento, "Remessa não criada",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string setor;
        try
        {
            var consulta = await setores!.ConsultarAsync(linha.OrdemVenda!, cancelamento.Token);
            if (consulta.Status != StatusConsultaSap.Sucesso || string.IsNullOrWhiteSpace(consulta.Dados))
            {
                // Supor o setor escolheria entre litro e quilo no escuro.
                var falha = $"Não foi possível ler o setor de atividade da ordem {linha.OrdemVenda} "
                    + $"({consulta.Status}). Sem ele não dá para escolher entre litro e quilo.";
                status.Text = $"{DateTime.Now:HH:mm:ss} — {falha}";
                MessageBox.Show(this, falha, "Remessa não criada",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            setor = consulta.Dados.Trim();
        }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested) { return; }
        catch (Exception erro)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — consulta do setor interrompida "
                + $"por {erro.GetType().Name}: {erro.Message}.";
            return;
        }

        RemessaSdRequest pedido;
        string json;
        FluxoRemessaSd fluxo;
        try
        {
            fluxo = FluxoRemessaSd.Escolher(setor, linha.PesoLiquido);
            pedido = MapeadorRemessaSd.Montar(linha.OrdemVenda, linha.LinhaOrdem, linha.PesoLiquido, setor);
            json = MapeadorRemessaSd.Serializar(pedido);
        }
        catch (RemessaSdBloqueadaException bloqueio)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — {bloqueio.Codigo}: {bloqueio.Detalhe}";
            MessageBox.Show(this, bloqueio.Detalhe, bloqueio.Codigo,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var cabecalho =
            $"Pesagem {linha.IdPesagem}   —   ordem {linha.OrdemVenda} / linha {linha.LinhaOrdem}\r\n"
            + $"Setor de atividade {setor}"
            + (setor == SetorAtividadeSd.Biodiesel
                ? "  (biodiesel: remessa nasce com 1 L e o peso real vai depois no cabeçalho)"
                : "  (quantidade = peso líquido da balança, em quilo)")
            + $"\r\nQuantidade a enviar: {fluxo.Quantidade} {fluxo.Unidade}"
            + $"   —   peso líquido medido: {linha.PesoLiquido:N3} kg";

        if (!ConfirmarEnvio(cabecalho, json)) return;
        await CriarRemessaAsync(linha, setor, fluxo, pedido, json);
    }

    /// <summary>
    /// Passo 4: ajusta a remessa ja criada - transporte sempre, pesos da balanca e itens zerados
    /// no biodiesel. Mesma disciplina do passo 3: previa, confirmacao, reserva antes do PATCH.
    /// </summary>
    private async Task AbrirAjusteAsync(DevolutivaSdLinha linha)
    {
        var jaRegistrada = remessasConhecidas.FirstOrDefault(
            x => string.Equals(x.IdPesagem, linha.IdPesagem, StringComparison.Ordinal));

        string? Impedimento()
        {
            if (remessas is null || ajuste is null)
                return "Ajuste da remessa indisponível nesta sessão"
                    + (motivoIndisponivel is null ? "" : " — " + motivoIndisponivel) + ".";
            if (jaRegistrada is null || jaRegistrada.NumeroRemessa is null)
                return $"A pesagem {linha.IdPesagem} ainda não tem remessa criada. "
                    + "O passo 4 edita uma remessa que já existe.";
            if (jaRegistrada.AjusteEstado == EstadoAjusteRemessaSd.Ajustada)
                return $"A remessa {jaRegistrada.NumeroRemessa} já foi ajustada.";
            if (jaRegistrada.AjusteEstado == EstadoAjusteRemessaSd.DesfechoDesconhecido)
                return $"A remessa {jaRegistrada.NumeroRemessa} tem um ajuste sem desfecho conhecido. "
                    + "Confira no SAP antes de tentar de novo.";
            return null;
        }

        if (Impedimento() is string impedimento)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — {impedimento}";
            MessageBox.Show(this, impedimento, "Ajuste não feito",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var numero = jaRegistrada!.NumeroRemessa!;
        AjusteCabecalhoRemessaSdRequest cabecalho;
        bool zerarItens;
        try
        {
            zerarItens = AjusteRemessaSd.AjustaPeso(jaRegistrada.SetorAtividade);
            cabecalho = MapeadorAjusteRemessaSd.MontarCabecalho(
                jaRegistrada.SetorAtividade, linha.PesoBruto, linha.PesoLiquido, linha.Placa);
        }
        catch (RemessaSdBloqueadaException bloqueio)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — {bloqueio.Codigo}: {bloqueio.Detalhe}";
            MessageBox.Show(this, bloqueio.Detalhe, bloqueio.Codigo,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Le a remessa antes de montar a previa: dai vem o ETag e, sobretudo, as CHAVES reais dos
        // itens - o SAP devolve "000010" onde o documento escreve "10".
        RemessaSdLida? lida;
        try { lida = await ajuste!.LerAsync(numero, cancelamento.Token); }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested) { return; }
        catch (Exception erro)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — leitura da remessa {numero} interrompida "
                + $"por {erro.GetType().Name}: {erro.Message}. Nada foi alterado.";
            return;
        }
        if (lida is null)
        {
            var falha = $"Não foi possível ler a remessa {numero} no SAP. Nada foi alterado.";
            status.Text = $"{DateTime.Now:HH:mm:ss} — {falha}";
            MessageBox.Show(this, falha, "Ajuste não feito", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var json = MapeadorAjusteRemessaSd.Serializar(cabecalho);
        var corpoItem = MapeadorAjusteRemessaSd.Serializar(MapeadorAjusteRemessaSd.MontarItemZerado());
        var chaves = string.Join(", ", lida.Itens.Select(i => i.DeliveryDocumentItem));

        var resumoCabecalho =
            $"Remessa {numero}   —   pesagem {linha.IdPesagem}, ordem {linha.OrdemVenda}/{linha.LinhaOrdem}\r\n"
            + $"Setor de atividade {jaRegistrada.SetorAtividade}"
            + (zerarItens
                ? "  (biodiesel: peso da balança vai ao cabeçalho e os itens são zerados)"
                : "  (o item já está em KG: só o transporte muda)")
            + $"\r\nMeio de transporte: {cabecalho.MeioDeTransporte}   —   tipo {cabecalho.TipoDeTransporte}"
            + (zerarItens
                ? $"\r\nPeso bruto: {cabecalho.PesoBruto}   —   peso líquido: {cabecalho.PesoLiquido}"
                : "")
            + $"\r\nItens lidos no SAP: {(chaves.Length == 0 ? "(nenhum)" : chaves)}";

        var previa = "PATCH no cabeçalho:\r\n" + Formatar(json)
            + (zerarItens
                ? $"\r\n\r\nPATCH em cada item ({chaves}):\r\n" + Formatar(corpoItem)
                : "\r\n\r\nNenhum item é alterado neste fluxo.");

        if (!ConfirmarAjuste(resumoCabecalho, previa)) return;
        await AjustarRemessaAsync(linha, numero, lida, cabecalho, zerarItens, json);
    }

    private bool ConfirmarAjuste(string cabecalho, string previa)
    {
        using var janela = new Form
        {
            Text = "Ajustar remessa no SAP — prévia",
            ClientSize = new Size(820, 540),
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false
        };
        var rodape = new Panel { Dock = DockStyle.Bottom, Height = 46 };
        var aplicar = new Button
        {
            Text = "Ajustar no SAP", DialogResult = DialogResult.OK,
            Width = 190, Height = 30, Left = 820 - 190 - 110 - 20, Top = 8
        };
        var fechar = new Button
        {
            Text = "Fechar", DialogResult = DialogResult.Cancel,
            Width = 100, Height = 30, Left = 820 - 110, Top = 8
        };
        rodape.Controls.Add(aplicar);
        rodape.Controls.Add(fechar);
        janela.Controls.Add(new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both, WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9F),
            Text = cabecalho + "\r\n\r\n" + previa
        });
        janela.Controls.Add(rodape);
        janela.AcceptButton = fechar;
        janela.CancelButton = fechar;

        if (janela.ShowDialog(this) != DialogResult.OK) return false;

        return MessageBox.Show(this,
            "Confirmar o ajuste da remessa no SAP?\r\n\r\n" + cabecalho
            + "\r\n\r\nO peso escrito aqui é o que vai para a NF-e.",
            "Confirmar ajuste no SAP", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private async Task AjustarRemessaAsync(DevolutivaSdLinha linha, string numero, RemessaSdLida lida,
        AjusteCabecalhoRemessaSdRequest cabecalho, bool zerarItens, string json)
    {
        var digital = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));

        try { await remessas!.ReservarAjusteAsync(linha.IdPesagem, digital, cancelamento.Token); }
        catch (AjusteRemessaSdJaFeitoException)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — a remessa {numero} já tinha ajuste registrado; "
                + "nada foi enviado ao SAP.";
            await ConsultarAsync();
            return;
        }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested) { return; }
        catch (Exception erro)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — reserva do ajuste falhou por "
                + $"{erro.GetType().Name}: {erro.Message}. Nada foi enviado ao SAP.";
            return;
        }

        IReadOnlyList<EtapaAjusteRemessaSd> etapas;
        try { etapas = await ajuste!.AjustarAsync(lida, cabecalho, zerarItens, cancelamento.Token); }
        catch (Exception erro)
        {
            // Fica DESFECHO_DESCONHECIDO: o PATCH pode ter chegado.
            status.Text = $"{DateTime.Now:HH:mm:ss} — ajuste interrompido por {erro.GetType().Name}: "
                + $"{erro.Message}. Confira a remessa {numero} no SAP antes de repetir.";
            await ConsultarAsync();
            return;
        }

        var tudoOk = etapas.Count > 0 && etapas.All(e => e.Ok);
        var estado = tudoOk ? EstadoAjusteRemessaSd.Ajustada : EstadoAjusteRemessaSd.Recusada;
        var ultima = etapas.LastOrDefault();
        // Etapa por etapa: num ajuste parcial, saber ONDE parou e o que permite retomar no SAP.
        var detalhe = string.Join(" | ", etapas.Select(
            e => $"{e.Etapa}: HTTP {e.HttpStatus}{(e.Ok ? " ok" : " — " + e.Resumo)}"));

        try
        {
            await remessas.FecharAjusteAsync(linha.IdPesagem, estado, ultima?.HttpStatus ?? 0,
                detalhe, cancelamento.Token);
        }
        catch (Exception erro)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — {detalhe}; mas o desfecho não foi gravado "
                + $"({erro.GetType().Name}: {erro.Message}).";
            await ConsultarAsync();
            return;
        }

        status.Text = tudoOk
            ? $"{DateTime.Now:HH:mm:ss} — remessa {numero} ajustada. {detalhe}"
            : $"{DateTime.Now:HH:mm:ss} — ajuste incompleto na remessa {numero}. {detalhe}";
        if (!tudoOk)
            MessageBox.Show(this, detalhe, "SAP recusou parte do ajuste",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

        var resumoFinal = status.Text;
        await ConsultarAsync();
        status.Text = resumoFinal;
    }

    /// <summary>
    /// Passo 5: compoe o texto ZNFE preenchendo os espacos vazios do que veio da ordem de venda.
    ///
    /// O PATCH substitui o texto INTEIRO, entao a previa mostra o texto atual e o final lado a
    /// lado. Quem confirma esta vendo exatamente o que vai ficar na NF-e.
    /// </summary>
    private async Task AbrirTextoAsync(DevolutivaSdLinha linha)
    {
        var registrada = remessasConhecidas.FirstOrDefault(
            x => string.Equals(x.IdPesagem, linha.IdPesagem, StringComparison.Ordinal));

        string? Impedimento()
        {
            if (remessas is null || textos is null)
                return "Texto da NF-e indisponível nesta sessão"
                    + (motivoIndisponivel is null ? "" : " — " + motivoIndisponivel) + ".";
            if (registrada is null || registrada.NumeroRemessa is null)
                return $"A pesagem {linha.IdPesagem} ainda não tem remessa criada. "
                    + "O texto pertence a uma remessa que já existe.";
            if (registrada.TextoEstado == EstadoTextoNfeSd.Gravado)
                return $"O texto da remessa {registrada.NumeroRemessa} já foi gravado.";
            if (registrada.TextoEstado == EstadoTextoNfeSd.DesfechoDesconhecido)
                return $"O texto da remessa {registrada.NumeroRemessa} ficou sem desfecho conhecido. "
                    + "Confira no SAP antes de tentar de novo.";
            return null;
        }

        if (Impedimento() is string impedimento)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — {impedimento}";
            MessageBox.Show(this, impedimento, "Texto não gravado",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var numero = registrada!.NumeroRemessa!;
        var complemento = MapeadorTextoNfeSd.LerComplemento(linha.PayloadJson);

        TextoNfeSdAtual atual;
        try { atual = await textos!.LerAsync(numero, cancelamento.Token); }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested) { return; }
        catch (Exception erro)
        {
            // Sem o texto atual nao ha composicao possivel: o PATCH apagaria o que veio da ordem.
            status.Text = $"{DateTime.Now:HH:mm:ss} — leitura do texto da remessa {numero} "
                + $"interrompida por {erro.GetType().Name}: {erro.Message}. Nada foi alterado.";
            return;
        }

        string final;
        try { final = MapeadorTextoNfeSd.Compor(atual.Texto, complemento); }
        catch (RemessaSdBloqueadaException bloqueio)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — {bloqueio.Codigo}: {bloqueio.Detalhe}";
            MessageBox.Show(this, bloqueio.Detalhe, bloqueio.Codigo,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.Equals(final, atual.Texto, StringComparison.Ordinal))
        {
            var nada = $"O texto da remessa {numero} já contém tudo o que a balança tinha a "
                + "acrescentar. Nada a enviar.";
            status.Text = $"{DateTime.Now:HH:mm:ss} — {nada}";
            MessageBox.Show(this, nada, "Texto inalterado", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var cabecalho =
            $"Remessa {numero}   —   pesagem {linha.IdPesagem}, ordem {linha.OrdemVenda}/{linha.LinhaOrdem}\r\n"
            + (atual.Existe
                ? "O texto já existe: será alterado (PATCH), preservando o que veio da ordem."
                : "A remessa ainda não tem texto: será criado (POST).")
            + "\r\nDa balança: "
            + string.Join("   ", new[]
            {
                complemento.Lacre is null ? null : "lacre " + complemento.Lacre,
                complemento.Placa is null ? null : "placa " + complemento.Placa,
                complemento.Motorista is null ? null : "motorista " + complemento.Motorista,
                complemento.Cpf is null ? null : "CPF " + complemento.Cpf
            }.Where(x => x is not null));

        var previa =
            "----- TEXTO ATUAL NO SAP -----\r\n"
            + (atual.Existe ? Linhas(atual.Texto!) : "(não existe)")
            + "\r\n\r\n----- TEXTO QUE SERÁ GRAVADO -----\r\n" + Linhas(final);

        if (!ConfirmarTexto(cabecalho, previa)) return;
        await GravarTextoAsync(linha, numero, atual, final);
    }

    /// <summary>
    /// Passo 6: picking de todos os itens, ou saida de mercadoria.
    ///
    /// Nao ha previa de corpo aqui porque as duas acoes nao tem corpo - o que a tela mostra e o
    /// que vai acontecer, por extenso. A saida e o passo irreversivel do fluxo: qualquer desfecho
    /// ja gravado, inclusive "desconhecido", impede uma segunda tentativa por esta tela.
    /// </summary>
    private async Task AbrirExpedicaoAsync(DevolutivaSdLinha linha, string etapa)
    {
        var registrada = remessasConhecidas.FirstOrDefault(
            x => string.Equals(x.IdPesagem, linha.IdPesagem, StringComparison.Ordinal));
        var saida = etapa == EtapaExpedicaoSd.Saida;
        var nome = saida ? "saída de mercadoria" : "picking";
        var jaFeito = saida ? registrada?.SaidaEstado : registrada?.PickingEstado;

        string? Impedimento()
        {
            if (remessas is null || expedicao is null)
                return $"A {nome} está indisponível nesta sessão"
                    + (motivoIndisponivel is null ? "" : " — " + motivoIndisponivel) + ".";
            if (registrada is null || registrada.NumeroRemessa is null)
                return $"A pesagem {linha.IdPesagem} ainda não tem remessa criada.";
            if (jaFeito is not null)
                return $"A {nome} da remessa {registrada.NumeroRemessa} já tem desfecho registrado: "
                    + jaFeito + (jaFeito == EstadoSaidaSd.DesfechoDesconhecido
                        ? ". Confira no SAP; repetir poderia duplicar o movimento de estoque." : ".");
            return null;
        }

        if (Impedimento() is string impedimento)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — {impedimento}";
            MessageBox.Show(this, impedimento, "Nada foi enviado",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var numero = registrada!.NumeroRemessa!;
        var aviso = saida
            ? "A saída de mercadoria MOVE ESTOQUE e gera documento de material.\r\n"
              + "Esta tela não desfaz o movimento depois.\r\n\r\n"
              + (registrada.PickingEstado == EstadoPickingSd.Confirmado
                  ? "Picking confirmado por esta tela."
                  : "⚠ Não há registro de picking confirmado por esta tela. "
                    + "Se ele não tiver sido feito, o SAP vai recusar.")
            : "Confirma o picking de todos os itens da remessa. Pode ser refeito se necessário.";

        var texto =
            $"Remessa {numero}   —   pesagem {linha.IdPesagem}, ordem {linha.OrdemVenda}/{linha.LinhaOrdem}\r\n"
            + $"Ação: {nome}\r\n\r\n{aviso}";

        if (MessageBox.Show(this, texto + $"\r\n\r\nConfirmar a {nome}?",
                $"Confirmar {nome} no SAP", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        // A saida pede a confirmacao duas vezes, porque errar aqui nao se corrige por aqui.
        if (saida && MessageBox.Show(this,
                $"Última confirmação: postar a saída de mercadoria da remessa {numero}?\r\n\r\n"
                + "Depois disso o estoque já terá sido movimentado.",
                "Saída de mercadoria", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        await ExecutarExpedicaoAsync(linha, numero, etapa, nome);
    }

    private async Task ExecutarExpedicaoAsync(DevolutivaSdLinha linha, string numero,
        string etapa, string nome)
    {
        try { await remessas!.ReservarExpedicaoAsync(linha.IdPesagem, etapa, cancelamento.Token); }
        catch (ExpedicaoSdJaFeitaException)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — a {nome} da remessa {numero} já tinha desfecho "
                + "registrado; nada foi enviado ao SAP.";
            await ConsultarAsync();
            return;
        }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested) { return; }
        catch (Exception erro)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — reserva da {nome} falhou por "
                + $"{erro.GetType().Name}: {erro.Message}. Nada foi enviado ao SAP.";
            return;
        }

        var saida = etapa == EtapaExpedicaoSd.Saida;
        ExpedicaoSd desfecho;
        try
        {
            desfecho = saida
                ? await expedicao!.PostarSaidaAsync(numero, cancelamento.Token)
                : await expedicao!.ConfirmarPickingAsync(numero, cancelamento.Token);
        }
        catch (Exception erro)
        {
            // Fica DESFECHO_DESCONHECIDO. Na saida isso e proposital: o POST pode ter chegado, e
            // tentar de novo "por garantia" e o que duplica o movimento de estoque.
            status.Text = $"{DateTime.Now:HH:mm:ss} — {nome} interrompida por {erro.GetType().Name}: "
                + $"{erro.Message}. Confira a remessa {numero} no SAP antes de repetir.";
            await ConsultarAsync();
            return;
        }

        var estado = desfecho.Ok
            ? (saida ? EstadoSaidaSd.Postada : EstadoPickingSd.Confirmado)
            : (saida ? EstadoSaidaSd.Recusada : EstadoPickingSd.Recusado);
        try
        {
            await remessas.FecharExpedicaoAsync(linha.IdPesagem, etapa, estado, desfecho.HttpStatus,
                desfecho.MensagensSap.Count == 0 ? null : desfecho.Resumo, cancelamento.Token);
        }
        catch (Exception erro)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — HTTP {desfecho.HttpStatus} na {nome}, mas o "
                + $"desfecho não foi gravado ({erro.GetType().Name}: {erro.Message}).";
            await ConsultarAsync();
            return;
        }

        status.Text = desfecho.Ok
            ? $"{DateTime.Now:HH:mm:ss} — {nome} da remessa {numero} concluída (HTTP {desfecho.HttpStatus})."
            : $"{DateTime.Now:HH:mm:ss} — SAP recusou a {nome} (HTTP {desfecho.HttpStatus}): {desfecho.Resumo}";
        if (!desfecho.Ok)
            MessageBox.Show(this, desfecho.Resumo, $"SAP recusou — HTTP {desfecho.HttpStatus}",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

        var resumoFinal = status.Text;
        await ConsultarAsync();
        status.Text = resumoFinal;
    }

    /// <summary>O texto do SAP usa \n; o TextBox do Windows precisa de \r\n para quebrar linha.</summary>
    private static string Linhas(string texto)
        => texto.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n", "\r\n", StringComparison.Ordinal);

    private bool ConfirmarTexto(string cabecalho, string previa)
    {
        using var janela = new Form
        {
            Text = "Texto da NF-e (ZNFE) — prévia",
            ClientSize = new Size(900, 600),
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false
        };
        var rodape = new Panel { Dock = DockStyle.Bottom, Height = 46 };
        var gravar = new Button
        {
            Text = "Gravar texto no SAP", DialogResult = DialogResult.OK,
            Width = 190, Height = 30, Left = 900 - 190 - 110 - 20, Top = 8
        };
        var fechar = new Button
        {
            Text = "Fechar", DialogResult = DialogResult.Cancel,
            Width = 100, Height = 30, Left = 900 - 110, Top = 8
        };
        rodape.Controls.Add(gravar);
        rodape.Controls.Add(fechar);
        janela.Controls.Add(new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both, WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9F),
            Text = cabecalho + "\r\n\r\n" + previa
        });
        janela.Controls.Add(rodape);
        janela.AcceptButton = fechar;
        janela.CancelButton = fechar;

        if (janela.ShowDialog(this) != DialogResult.OK) return false;

        return MessageBox.Show(this,
            "Gravar este texto na remessa?\r\n\r\n" + cabecalho
            + "\r\n\r\nO texto gravado é o que sai nas informações complementares da NF-e.",
            "Confirmar texto no SAP", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private async Task GravarTextoAsync(DevolutivaSdLinha linha, string numero,
        TextoNfeSdAtual atual, string final)
    {
        var digital = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(final)));

        try { await remessas!.ReservarTextoAsync(linha.IdPesagem, digital, cancelamento.Token); }
        catch (TextoNfeSdJaGravadoException)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — o texto da remessa {numero} já tinha gravação "
                + "registrada; nada foi enviado ao SAP.";
            await ConsultarAsync();
            return;
        }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested) { return; }
        catch (Exception erro)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — reserva do texto falhou por "
                + $"{erro.GetType().Name}: {erro.Message}. Nada foi enviado ao SAP.";
            return;
        }

        GravacaoTextoNfeSd desfecho;
        try { desfecho = await textos!.GravarAsync(numero, atual, final, cancelamento.Token); }
        catch (Exception erro)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — gravação do texto interrompida por "
                + $"{erro.GetType().Name}: {erro.Message}. Confira a remessa {numero} no SAP.";
            await ConsultarAsync();
            return;
        }

        var estado = desfecho.Ok ? EstadoTextoNfeSd.Gravado : EstadoTextoNfeSd.Recusado;
        try
        {
            await remessas.FecharTextoAsync(linha.IdPesagem, estado, desfecho.HttpStatus,
                desfecho.Criou, desfecho.MensagensSap.Count == 0 ? null : desfecho.Resumo,
                cancelamento.Token);
        }
        catch (Exception erro)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — HTTP {desfecho.HttpStatus} no texto, mas o "
                + $"desfecho não foi gravado ({erro.GetType().Name}: {erro.Message}).";
            await ConsultarAsync();
            return;
        }

        status.Text = desfecho.Ok
            ? $"{DateTime.Now:HH:mm:ss} — texto da remessa {numero} "
                + (desfecho.Criou ? "criado" : "atualizado") + $" (HTTP {desfecho.HttpStatus})."
            : $"{DateTime.Now:HH:mm:ss} — SAP recusou o texto (HTTP {desfecho.HttpStatus}): {desfecho.Resumo}";
        if (!desfecho.Ok)
            MessageBox.Show(this, desfecho.Resumo, $"SAP recusou — HTTP {desfecho.HttpStatus}",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

        var resumoFinal = status.Text;
        await ConsultarAsync();
        status.Text = resumoFinal;
    }

    /// <summary>Previa com o corpo exato do POST. Nao ha caminho de criar sem passar por aqui.</summary>
    private bool ConfirmarEnvio(string cabecalho, string json)
    {
        using var janela = new Form
        {
            Text = "Criar remessa no SAP — prévia",
            ClientSize = new Size(820, 540),
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false
        };
        var rodape = new Panel { Dock = DockStyle.Bottom, Height = 46 };
        var criar = new Button
        {
            Text = "Criar remessa no SAP", DialogResult = DialogResult.OK,
            Width = 190, Height = 30, Left = 820 - 190 - 110 - 20, Top = 8
        };
        var fechar = new Button
        {
            Text = "Fechar", DialogResult = DialogResult.Cancel,
            Width = 100, Height = 30, Left = 820 - 110, Top = 8
        };
        rodape.Controls.Add(criar);
        rodape.Controls.Add(fechar);
        janela.Controls.Add(new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both, WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9F),
            Text = cabecalho + "\r\n\r\nCorpo do POST:\r\n" + Formatar(json)
        });
        janela.Controls.Add(rodape);
        janela.AcceptButton = fechar;   // Enter fecha; criar exige o clique.
        janela.CancelButton = fechar;

        if (janela.ShowDialog(this) != DialogResult.OK) return false;

        return MessageBox.Show(this,
            "Confirmar a criação da remessa no SAP?\r\n\r\n" + cabecalho
            + "\r\n\r\nRemessa criada no SAP não se desfaz por esta tela.",
            "Confirmar envio ao SAP", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private async Task CriarRemessaAsync(DevolutivaSdLinha linha, string setor, FluxoRemessaSd fluxo,
        RemessaSdRequest pedido, string json)
    {
        var chave = linha.ChaveNatural ?? $"{linha.OrdemVenda}/{linha.LinhaOrdem}";
        var digital = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));

        // Reserva ANTES do POST, com desfecho desconhecido: se a resposta se perder, a pesagem
        // continua ocupada em vez de ficar livre para uma segunda remessa.
        try
        {
            await remessas!.ReservarAsync(new RemessaSdRegistro(
                IdPesagem: linha.IdPesagem, ChaveNatural: chave,
                OrdemVenda: linha.OrdemVenda!, LinhaOrdem: linha.LinhaOrdem!,
                SetorAtividade: setor, Quantidade: fluxo.Quantidade, Unidade: fluxo.Unidade,
                NumeroRemessa: null, Estado: EstadoRemessaSd.DesfechoDesconhecido,
                HttpStatus: 0, PayloadSha256: digital, MensagensSap: null), cancelamento.Token);
        }
        catch (RemessaSdJaCriadaException)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — pesagem {linha.IdPesagem} já estava reservada; "
                + "nada foi enviado ao SAP.";
            await ConsultarAsync();
            return;
        }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested) { return; }
        catch (Exception erro)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — reserva falhou por {erro.GetType().Name}: "
                + $"{erro.Message}. Nada foi enviado ao SAP.";
            return;
        }

        CriacaoRemessaSd desfecho;
        try { desfecho = await sap!.CriarAsync(pedido, cancelamento.Token); }
        catch (Exception erro)
        {
            // A reserva fica como DESFECHO_DESCONHECIDO: o POST pode ter chegado.
            status.Text = $"{DateTime.Now:HH:mm:ss} — envio interrompido por {erro.GetType().Name}: "
                + $"{erro.Message}. Confira a remessa no SAP antes de repetir.";
            await ConsultarAsync();
            return;
        }

        var estado = desfecho.Criada ? EstadoRemessaSd.Criada : EstadoRemessaSd.Recusada;
        var mensagens = desfecho.MensagensSap.Count == 0 ? null : desfecho.Resumo;
        try
        {
            await remessas.FecharDesfechoAsync(linha.IdPesagem, desfecho.NumeroRemessa, estado,
                desfecho.HttpStatus, mensagens, cancelamento.Token);
        }
        catch (Exception erro)
        {
            status.Text = $"{DateTime.Now:HH:mm:ss} — HTTP {desfecho.HttpStatus}, "
                + $"remessa {desfecho.NumeroRemessa ?? "(sem número)"}, mas o desfecho não foi gravado "
                + $"({erro.GetType().Name}: {erro.Message}).";
            await ConsultarAsync();
            return;
        }

        status.Text = desfecho.Criada
            ? $"{DateTime.Now:HH:mm:ss} — remessa {desfecho.NumeroRemessa} criada para a ordem "
                + $"{linha.OrdemVenda}/{linha.LinhaOrdem} ({fluxo.Quantidade} {fluxo.Unidade})."
            : $"{DateTime.Now:HH:mm:ss} — SAP recusou (HTTP {desfecho.HttpStatus}): {desfecho.Resumo}";
        if (!desfecho.Criada)
            MessageBox.Show(this, desfecho.Resumo, $"SAP recusou — HTTP {desfecho.HttpStatus}",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

        var resumoFinal = status.Text;
        await ConsultarAsync();
        status.Text = resumoFinal;   // A consulta sobrescreveria o desfecho, que e o que importa.
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
