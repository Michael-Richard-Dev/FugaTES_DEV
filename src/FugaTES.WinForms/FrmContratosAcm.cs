using FugaTES.Aplicacao.Consultas.ContratosAcm;

namespace FugaTES.WinForms;

public partial class FrmContratosAcm : Form
{
    private readonly ContratosAcmConsulta consulta;
    private readonly IDisposable? lifetime;
    private readonly CancellationTokenSource cancelamento = new();
    private bool carregando;
    private readonly Guid? integrationScope;

    public FrmContratosAcm(ContratosAcmConsulta consulta, IDisposable? lifetime = null,string? operationalBlocker=null,bool restPreview=false,bool execucaoR57=false,
        FugaTES.Aplicacao.Servicos.ManualRetry.IAcmManualRetryCommandClient? manualRetry=null,Action? retryStateChanged=null,Guid? integrationScope=null,ManualRetry.AcmManualRetryController? sharedRetry=null)
    {
        if(integrationScope==Guid.Empty)throw new ArgumentException("Integração inválida.",nameof(integrationScope));
        this.integrationScope=integrationScope;
        this.consulta = consulta;
        this.lifetime = lifetime;
        InitializeComponent();
        centro.Text = ContratosAcmConsulta.CentroInicial;
        if(execucaoR57){ConfigurarExecucaoR57();return;}
        if(restPreview){if(File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.preview.R55.json")))ConfigurarDualPreviewR55();else ConfigurarRestPreview();return;}
        atualizar.Click += async (_, _) => await ConsultarAsync();
        // Acao JSON por contrato/item. Somente no caminho produtivo: as previas R55/R57
        // refazem as colunas da grade e retornam antes desta linha.
        grid.CellContentClick += (_, e) => AbrirJsonSeAcionado(e.RowIndex, e.ColumnIndex);
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="FonteDados",HeaderText="Origem / natureza",DataPropertyName="FonteDados",ReadOnly=true});
        // Keep the JSON action and provenance visible without scrolling over technical correlations.
        grid.Columns["json"]!.DisplayIndex=0;
        grid.Columns["FonteDados"]!.DisplayIndex=4;
        grid.Columns["FonteDados"]!.AutoSizeMode=DataGridViewAutoSizeColumnMode.None;
        grid.Columns["FonteDados"]!.Width=220;
        grid.CellFormatting+=(_,e)=>
        {
            if(grid.Columns[e.ColumnIndex].Name!="FonteDados")return;
            e.Value=(e.Value as string) switch {"SIMULACAO_CONFIRMADA"=>"SIMULAÇÃO CONFIRMADA",
                "PROJECAO_PERSISTIDA_ORIGEM_NAO_ATESTADA"=>"PERSISTIDO / ORIGEM NÃO ATESTADA",_=>e.Value};
        };
        ConfigureRetry(manualRetry,retryStateChanged,sharedRetry);
        if(operationalBlocker is null)ConfigurarCentrosEAtualizacaoAutomatica();
        if(operationalBlocker is null)Shown += async (_, _) => await ConsultarAsync();
        else {status.Text=operationalBlocker;atualizar.Enabled=false;if(status.Parent is TableLayoutPanel panel)panel.RowStyles[2].Height=120;}
        FormClosing += (_, _) => cancelamento.Cancel();
    }

    /// <summary>
    /// Abre o JSON do contrato da linha, quando o clique foi na coluna JSON.
    /// Apresentacao apenas: usa o payload ja trazido com a linha, nao consulta o SAP e
    /// nao escreve nada. A origem exibida e a projecao persistida, declarada como tal.
    /// </summary>
    internal void AbrirJsonSeAcionado(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || columnIndex < 0 || columnIndex >= grid.Columns.Count) return;
        if (!string.Equals(grid.Columns[columnIndex].Name, "json", StringComparison.Ordinal)) return;
        if (grid.Rows[rowIndex].DataBoundItem is not ContratoAcmLeitura linha) return;
        using var janela = new Acm.FrmContratoJson(linha.NumeroContrato, linha.Item,
            linha.IntegracaoId, linha.Versao, linha.PayloadContratoJson,
            Acm.OrigemJsonContrato.ProjecaoPersistida);
        janela.ShowDialog(this);
    }

    private void ConfigurarDualPreviewR55()
    {
        Text="FugaTES — ACM 3011 — CONTRATO SOAP / AGENDAMENTO REST — R55";
        centro.ReadOnly=true;atualizar.Text="Atualizar prévia";atualizar.Width=125;
        if(atualizar.Parent is FlowLayoutPanel filtro)
        {
            filtro.AutoScroll=true;
            filtro.Controls.Add(new Label{Text="SAP mestre | 0000001031 / V2",AutoSize=true,Padding=new Padding(6,6,6,0)});
            filtro.Controls.Add(new Button{Text="Consultar SAP",Enabled=false,Width=115,AccessibleName="Consulta SAP do contrato não configurada"});
            filtro.Controls.Add(new Button{Text="Enviar SOAP — bloqueado",Enabled=false,Width=185});
            filtro.Controls.Add(new Button{Text="Enviar REST — bloqueado",Enabled=false,Width=185});
        }
        grid.Columns.Clear();
        void Columns(DataGridView g){foreach(var name in new[]{"Campo","Valor","Situacao","Origem"})g.Columns.Add(new DataGridViewTextBoxColumn{Name=name,HeaderText=name,DataPropertyName=name});g.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;}
        Columns(grid);
        var soapGrid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AutoGenerateColumns=false,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false};Columns(soapGrid);
        var restGrid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AutoGenerateColumns=false,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false};Columns(restGrid);
        TextBox Draft(string name)=>new(){Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,WordWrap=false,ScrollBars=ScrollBars.Both,Font=new Font("Consolas",10),AccessibleName=name};
        var soapText=Draft("SOAP cadastro de contrato — rascunho não enviável");var restText=Draft("REST agendamento vinculado — rascunho não enviável");
        var tabs=new TabControl{Dock=DockStyle.Fill};var sapTab=new TabPage("SAP / Estados");sapTab.Controls.Add(grid);tabs.TabPages.Add(sapTab);
        void AddTab(string name,DataGridView g,TextBox draft){var tab=new TabPage(name);var split=new SplitContainer{Dock=DockStyle.Fill,Size=new Size(1100,350),SplitterDistance=550};split.Panel1.Controls.Add(g);split.Panel2.Controls.Add(draft);tab.Controls.Add(split);tabs.TabPages.Add(tab);}
        AddTab("Contrato SOAP",soapGrid,soapText);AddTab("Agendamento REST",restGrid,restText);
        if(grid.Parent is not null&&Controls[0] is TableLayoutPanel layout){layout.Controls.Add(tabs,0,1);layout.RowStyles[2].Height=125;status.AutoEllipsis=false;}
        void Preview()
        {
            string? candidate=null;
            try{using var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Acm.preview.R55.json")));if(doc.RootElement.TryGetProperty("CodigoTransportadoraCandidata",out var c)&&c.ValueKind==System.Text.Json.JsonValueKind.String)candidate=c.GetString();}catch{ }
            var model=Acm.AcmDualPreviewR55.Criar(transportadoraCandidata:candidate);
            grid.DataSource=model.SapCampos.ToArray();soapGrid.DataSource=model.SoapCampos.ToArray();restGrid.DataSource=model.RestCampos.ToArray();
            soapText.Text="RASCUNHO SOAP — NÃO ENVIÁVEL\r\nConsulta / cadastro / manutenção do contrato mestre.\r\nCampos pendentes em comentários; ValorContrato = quantidade/peso SAP, não preço.\r\n\r\n"+model.SoapXml;
            restText.Text="RASCUNHO REST ACM — NÃO ENVIÁVEL\r\nPOST /api/v1/Guardian/ProtocoloPreCadastro\r\ncodigoContrato obrigatório nesta prévia vinculada; NÃO VALIDADO não é valor operacional.\r\n\r\n"+model.RestJson;
            status.Text="R55 — dois processos: contrato SAP → Guardian SOAP; agendamento → CloudPrix REST.\r\nSAP: "+model.SapEstado+".\r\nSOAP: quantidade/descrição e escolha funcional de código/Estado/TipoLancamento pendentes. REST: vínculo/cadastros/callback operacional pendentes.\r\nTransportadora é candidata configurável; confirmação e aprovação ainda necessárias. Webhook externo do teste não adotado.\r\nPostgreSQL/journal/idempotência/runtime: não atestados. Ambos os envios DESABILITADOS. Sem consultas, workers ou timers.";
        }
        atualizar.Click+=(_,_)=>Preview();FormClosing+=(_,_)=>cancelamento.Cancel();Preview();
    }

    private void ConfigurarRestPreview()
    {
        Text = "FugaTES — ACM 3011 — PRÉVIA REST — PROTOCOLO PARCIAL";
        centro.ReadOnly = true;
        atualizar.Text = "Atualizar prévia";
        atualizar.Width = 125;
        if (atualizar.Parent is FlowLayoutPanel filtro)
        {
            filtro.AutoScroll = true;
            filtro.Controls.Add(new Label { Text = "Contrato 0000001031 / V2 — alvo informado", AutoSize = true, Padding = new Padding(8, 6, 8, 0) });
            filtro.Controls.Add(new Button { Text = "Consultar SAP", Width = 120, Enabled = false,
                AccessibleName = "Consultar SAP — bloqueado: cliente autorizado do contrato não comprovado" });
            filtro.Controls.Add(new Button { Text = "Enviar REST — não autorizado", Width = 205, Enabled = false,
                AccessibleName = "Enviar REST — desabilitado; nenhum envio real" });
        }
        grid.Columns.Clear();
        foreach (var field in new[] { "Campo", "Valor", "Situacao", "Origem" })
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = field, Name = field, HeaderText = field });
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        var draft = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, WordWrap = false,
            ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 10), AccessibleName = "Rascunho REST parcial — não enviável" };
        if (grid.Parent is TableLayoutPanel layout)
        {
            layout.Controls.Remove(grid);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical,
                Size = new Size(1100, 350), SplitterDistance = 580 };
            split.Panel1.Controls.Add(grid); split.Panel2.Controls.Add(draft);
            layout.Controls.Add(split, 0, 1); layout.RowStyles[2].Height = 145;
            status.AutoEllipsis = false;
        }
        void Preview()
        {
            string? carrier = null, callback = null;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Acm.preview.R53.json");
                if (File.Exists(path))
                {
                    using var config = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                    var root = config.RootElement;
                    if (root.TryGetProperty("CodigoTransportadoraPadrao", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String) carrier = c.GetString();
                    if (root.TryGetProperty("UrlParaRetorno", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.String) callback = u.GetString();
                }
            }
            catch { /* Public preview configuration errors block readiness; never load a private profile. */ }
            var preview = Acm.AcmRestPreviewR53.CriarParcial(carrier, callback);
            var validatedCarrier = preview.Campos.Single(c => c.Campo == "dadosProtocoloPreCadastro.codigoTransportadora").Valor;
            grid.DataSource = preview.Campos.ToArray();
            draft.Text = "RASCUNHO PARCIAL — NÃO ENVIÁVEL\r\nDTO parcial + corpo aceito informado pelo usuário. POST /api/v1/Guardian/ProtocoloPreCadastro.\r\nCampos pendentes: NÃO VALIDADO.\r\n\r\n" + preview.RascunhoJson;
            status.Text = "Fluxo inicial: SAP → FugaTES → Guardian REST (pré-cadastro).\r\n" +
                "SOAP não é pré-requisito desta prévia. Nenhuma chamada SOAP realizada.\r\n" +
                "SAP: " + preview.SapStatus + ".\r\n" +
                "Modelo parcial: contrato/material/fluxo/agenda omitidos; isso não comprova cadastro do contrato ACM.\r\n" +
                "Transportadora padrão provisória: " + validatedCarrier + "; substituída pelo operador no Guardian. Emissor ACM ainda pendente.\r\n" +
                "PostgreSQL: não conectado/atestado; status separado, não impede esta prévia local.\r\n" +
                "Envio REST: DESABILITADO. Sem POST, SQL, workers ou timers. Alvo informado não equivale a dado recuperado.";
        }
        atualizar.Click += (_, _) => Preview();
        FormClosing += (_, _) => cancelamento.Cancel();
        Preview(); // local rendering only; no automatic external read
    }

    private async Task ConsultarAsync()
    {
        if (carregando) return;
        var filtro = centro.Text.Trim();
        if (filtro.Length == 0) { status.Text = "Informe o centro."; return; }
        carregando = true;
        atualizar.Enabled = centro.Enabled = false;
        grid.DataSource = null;
        status.Text = "Consultando contratos...";
        try
        {
            var rows = await consulta.ConsultarAsync(filtro, cancelamento.Token);
            if(integrationScope is { } target)rows=rows.Where(r=>r.IntegracaoId==target).ToArray();
            if (cancelamento.IsCancellationRequested) return;
            grid.DataSource = rows.ToArray();
            var todos = ContratosAcmConsulta.EhTodosOsCentros(filtro);
            var escopo = todos ? "Todos os centros autorizados" : $"Centro {filtro}";
            // Vazio em "todos os centros" nao comprova ausencia de registros: a API privada de
            // leitura pode ser anterior ao seletor TODOS e tratar o sentinela como um centro.
            var vazio = integrationScope is not null
                ? "Integração selecionada não retornada pela leitura atual; nenhum outro contrato será usado."
                : todos
                    ? "Nenhum contrato retornado para todos os centros. Isso não comprova ausência de registros: confirme se a API privada de leitura já publica o seletor TODOS."
                    : $"Nenhum contrato encontrado no centro {filtro}";
            status.Text = rows.Count == 0 ? vazio : $"{escopo}: {rows.Count} versoes / limite 500";
            await AtualizarReenviosAsync(rows);
        }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested) { }
        catch
        {
            if (!cancelamento.IsCancellationRequested)
                status.Text = "Falha de conectividade ou leitura dos contratos. Verifique a conexao e as permissoes.";
        }
        finally
        {
            carregando = false;
            if (!IsDisposed && !cancelamento.IsCancellationRequested)
            {
                atualizar.Enabled = true;
                centro.Enabled = todosOsCentros?.Checked != true;
                if (todosOsCentros is not null) todosOsCentros.Enabled = true;
            }
        }
    }

    private CheckBox? todosOsCentros;
    private System.Windows.Forms.Timer? atualizacaoAutomatica;

    /// <summary>
    /// Visibilidade por centro e atualizacao periodica da grade.
    ///
    /// Um contrato recebido do SAP nao pode ficar invisivel apenas por pertencer a um centro
    /// diferente do piloto: o campo Centro continua editavel e a opcao "Todos os centros"
    /// dispensa o filtro, usando a MESMA leitura autorizada somente leitura. Nenhuma consulta
    /// nova e criada e nenhum privilegio e ampliado.
    ///
    /// A atualizacao automatica de 10 s apenas reexecuta a consulta de leitura; nunca dispara
    /// reenvio, POST SAP, chamada Guardian ou worker.
    /// </summary>
    private void ConfigurarCentrosEAtualizacaoAutomatica()
    {
        if (centro.Parent is { } barra)
        {
            todosOsCentros = new CheckBox
            {
                Text = "Todos os centros",
                AutoSize = true,
                Margin = new Padding(12, 7, 0, 0),
                AccessibleName = "Todos os centros autorizados"
            };
            todosOsCentros.CheckedChanged += async (_, _) =>
            {
                if (todosOsCentros.Checked)
                {
                    centro.Text = ContratosAcmConsulta.TodosOsCentros;
                    centro.Enabled = false;
                }
                else
                {
                    centro.Enabled = true;
                    if (ContratosAcmConsulta.EhTodosOsCentros(centro.Text))
                        centro.Text = ContratosAcmConsulta.CentroInicial;
                }
                await ConsultarAsync();
            };
            barra.Controls.Add(todosOsCentros);
        }

        // Mesmo intervalo do painel inicial, para que a grade e o estado de conexao andem juntos.
        atualizacaoAutomatica = new System.Windows.Forms.Timer { Interval = 10000 };
        atualizacaoAutomatica.Tick += async (_, _) =>
        {
            if (IsDisposed || Disposing || carregando || !atualizar.Enabled || cancelamento.IsCancellationRequested) return;
            await ConsultarAsync();
        };
        Shown += (_, _) => atualizacaoAutomatica?.Start();
        FormClosing += (_, _) =>
        {
            atualizacaoAutomatica?.Stop();
            atualizacaoAutomatica?.Dispose();
            atualizacaoAutomatica = null;
        };
    }
}
