using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FugaTES.WinForms.Transportadoras.MasterdataR3;

// A tela observa o runtime da aplicação; não agenda ciclos nem encerra o runtime ao fechar.
public sealed class FrmCadastrosGuardianR3 : Form
{
    private readonly OrquestradorCompartilhadoMasterdataR3 _orquestrador;
    private readonly CarrierDesktopLifecycle? _lifecycle;
    private readonly SafeDemoReadOnlySessionR50? _demo;
    private Button? _sapDemo;
    private Button? _guardianDemo;
    private readonly CheckBox _guardianAttested=new(){AutoSize=true,Text="Confirmo escopo GET HML aprovado R43"};
    private readonly TextBox _guardianScope=new(){Width=130,UseSystemPasswordChar=true,PlaceholderText="GET scope HML"};
    private readonly System.Threading.CancellationTokenSource _viewCancellation=new();
    private readonly TextBox _bp=new(){Width=150,PlaceholderText="BP transportadora"};
    private readonly Button _sync=new(){Text="Sincronizar agora",Width=155,Height=30};
    private bool _manualRunning;
    private bool _readOnlyRunning;
    private bool _reconciliationRunning;
    private readonly Button _reconcile=new(){Text="Consultar CRM010 agora",Width=175,Height=30};
    private readonly Button _readOnly=new(){Text="CONSULTA MANUAL — SOMENTE LEITURA",Width=285,Height=30};
    private readonly Dictionary<CategoriaMasterdataR3, TextBox> _painelCategoria = new();
    private readonly TextBox _logs;
    private readonly Label _resumo;
    private bool _fechando;

    public FrmCadastrosGuardianR3(OrquestradorCompartilhadoMasterdataR3? orquestrador = null, CarrierDesktopLifecycle? lifecycle=null,SafeDemoReadOnlySessionR50? demo=null)
    {
        // Abertura sem injeção = seguranca fail-closed, nenhuma execucao externa.
        _lifecycle=lifecycle;_demo=demo;
        _orquestrador = demo?.Shared ?? lifecycle?.Masterdata ?? orquestrador ?? new OrquestradorCompartilhadoMasterdataR3();
        Text = "Integrações > Cadastros SAP > Guardian";
        MinimumSize = new Size(850, 520);
        Size = new Size(1050, 690);
        StartPosition = FormStartPosition.CenterParent;

        var principal = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        principal.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        principal.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        principal.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        Controls.Add(principal);

        var barra = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(12, 8, 12, 8), AutoScroll = true };
        _sync.Click += async (_,_)=>await SolicitarBpAsync();
        _bp.TextChanged += (_,_)=>AtualizarVisual();
        barra.Controls.Add(_bp);barra.Controls.Add(_sync);
        _readOnly.Click+=async(_,_)=>await ConsultarReadOnlyNaInterfaceAsync();
        barra.Controls.Add(_readOnly);
        _reconcile.Click+=async(_,_)=>
        {
            if(_lifecycle is null||_reconciliationRunning)return;
            _reconciliationRunning=true;AtualizarVisual();
            try{var result=await _lifecycle.ConsultarCadastrosAsync(_viewCancellation.Token);if(!_fechando&&_resumo is not null)_resumo.Text=result.Code;}
            catch{if(!_fechando&&_resumo is not null)_resumo.Text="Consulta cancelada ou bloqueada; nenhuma escrita autorizada.";}
            finally{_reconciliationRunning=false;AtualizarVisual();}
        };
        barra.Controls.Add(_reconcile);
        var historico=new Button{Text="Logs PostgreSQL",Width=140,Height=30};
        historico.Click += async (_,_)=>await CarregarLogsAsync();
        barra.Controls.Add(historico);
        barra.Controls.Add(new Label { AutoSize = true, Text = "Execução manual e automática compartilham o mesmo lock.", Padding = new Padding(10, 7, 0, 0) });
        if(demo is not null)
        {
            principal.RowStyles[0].Height=100;barra.WrapContents=true;
            _sync.Visible=false;_readOnly.Visible=false;_reconcile.Visible=false;
            historico.Enabled=false;historico.Text="Logs PG indisponíveis";
            foreach(var label in barra.Controls.OfType<Label>())label.Text="Somente leitura manual. Automático desabilitado.";
            _sapDemo=new Button{Text="Consultar SAP",Width=145,Height=30,AccessibleName="Consultar SAP"};
            _sapDemo.Click+=async(_,_)=>await ConsultarDemoSapAsync();barra.Controls.Add(_sapDemo);
            barra.Controls.Add(new Label{Text="idAssinatura GET aprovado R43 (sessão):",AutoSize=true,Padding=new Padding(0,7,0,0)});
            barra.Controls.Add(_guardianScope);
            _guardianScope.TextChanged+=(_,_)=>AtualizarVisual();
            _guardianAttested.CheckedChanged+=(_,_)=>AtualizarVisual();barra.Controls.Add(_guardianAttested);
            _guardianDemo=new Button{Text="Consultar Guardian",Width=160,Height=30,AccessibleName="Consultar Guardian — somente leitura HML"};
            _guardianDemo.Click+=async(_,_)=>await ConsultarDemoGuardianAsync();barra.Controls.Add(_guardianDemo);
        }
        principal.Controls.Add(barra, 0, 0);

        var abas = new TabControl { Dock = DockStyle.Fill };
        foreach (var categoria in Enum.GetValues<CategoriaMasterdataR3>())
        {
            var tab = new TabPage(categoria.ToString());
            var visual = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window, Font = new Font("Consolas", 11), ScrollBars = ScrollBars.Vertical };
            tab.Controls.Add(visual);
            _painelCategoria.Add(categoria, visual);
            abas.TabPages.Add(tab);
        }
        var paginaLog = new TabPage("Logs de Sincronização");
        _logs = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10) };
        paginaLog.Controls.Add(_logs);
        abas.TabPages.Add(paginaLog);
        principal.Controls.Add(abas, 0, 1);

        _resumo = new Label { Dock = DockStyle.Fill, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0) };
        principal.Controls.Add(_resumo, 0, 2);

        _orquestrador.EstadoAtualizado += AtualizarSeguro;
        if(_lifecycle is not null)_lifecycle.ReconciliationUpdated+=AtualizarSeguro;
        Shown += (_, _) => AtualizarVisual();
        FormClosing += (_, _) =>
        {
            _fechando = true;
            _viewCancellation.Cancel();
            _orquestrador.EstadoAtualizado -= AtualizarSeguro;
            if(_lifecycle is not null)_lifecycle.ReconciliationUpdated-=AtualizarSeguro;
        };
        if(demo is not null)_bp.Text=demo.InitialBp;
        AtualizarVisual();
    }

    private async Task ConsultarDemoGuardianAsync()
    {
        if(_demo is null||_readOnlyRunning||_fechando)return;
        var scope=_guardianScope.Text;_guardianScope.Clear();
        _readOnlyRunning=true;AtualizarVisual();
        try{await _demo.ConsultarGuardianAsync(_bp.Text.Trim(),scope,_viewCancellation.Token,_guardianAttested.Checked);}
        finally{scope=string.Empty;_readOnlyRunning=false;AtualizarVisual();}
    }

    private async Task ConsultarDemoSapAsync()
    {
        if(_demo is null||_readOnlyRunning||_fechando)return;
        _readOnlyRunning=true;AtualizarVisual();
        try{await _demo.ConsultarSapAsync(_bp.Text.Trim(),_viewCancellation.Token);}
        finally{_readOnlyRunning=false;AtualizarVisual();}
    }

    private async Task SolicitarBpAsync()
    {
        if(_fechando||_manualRunning||_lifecycle is null||!_lifecycle.Snapshot.Active)return;
        _manualRunning=true;AtualizarVisual();
        try {var result=await _lifecycle.RequestMasterdataAsync(Guid.NewGuid(),_bp.Text.Trim(),_viewCancellation.Token);
            if(!_fechando&&_resumo is not null)_resumo.Text=result.Code;}
        catch {if(!_fechando)_resumo.Text="Pedido não confirmado; consultar histórico antes de repetir.";}
        finally {_manualRunning=false;if(!_fechando)_sync.Enabled=_lifecycle.Snapshot.Active&&!_lifecycle.Snapshot.Cycling&&!string.IsNullOrWhiteSpace(_bp.Text);}
    }


    private async Task ConsultarReadOnlyNaInterfaceAsync()
    {
        if(_fechando||_readOnlyRunning||_lifecycle is null)return;
        _readOnlyRunning=true;_readOnly.Enabled=false;
        try{
            var result=await _lifecycle.ConsultarManualReadOnlyAsync(_bp.Text.Trim(),_viewCancellation.Token);
            if(!_fechando)_resumo.Text=$"CONSULTA SOMENTE LEITURA: {result.Decisao} | {result.Motivo} | Não autoriza POST/PUT.";
        }catch{if(!_fechando)_resumo.Text="Consulta interrompida; nenhuma escrita autorizada.";}
        finally{_readOnlyRunning=false;if(!_fechando)_readOnly.Enabled=_lifecycle.ManualReadOnlyAvailable(_bp.Text.Trim());}
    }

    private async Task CarregarLogsAsync()
    {
        if(_lifecycle is null||!_lifecycle.Snapshot.Active||_fechando)return;
        try{
            var cycles=await _lifecycle.ReadLogsAsync(_viewCancellation.Token);
            if(!_fechando)_logs.Lines=cycles.Select(c=>$"{c.StartedUtc:u} | {c.Id} | {c.Status} | Consultados={c.Consulted} | Criados={c.Created} | Atualizados={c.Updated} | Sem alteração={c.Unchanged} | Pendentes={c.Pending} | Falhas={c.Failed}").ToArray();
        }catch{if(!_fechando)_resumo.Text="Histórico PostgreSQL indisponível.";}
    }

    private void AtualizarSeguro()
    {
        if (_fechando || IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke((Action)AtualizarVisual); }
            catch (InvalidOperationException) { }
        }
        else AtualizarVisual();
    }

    private void AtualizarVisual()
    {
        if (_fechando || IsDisposed) return;
        if(_demo is not null){
            if(_sapDemo is not null)_sapDemo.Enabled=!_readOnlyRunning&&_demo.CanQuery(_bp.Text.Trim());
            if(_guardianDemo is not null)_guardianDemo.Enabled=!_readOnlyRunning&&_demo.CanQuery(_bp.Text.Trim())&&_guardianAttested.Checked&&_demo.GuardianRead?.Available==true&&!string.IsNullOrWhiteSpace(_guardianScope.Text);
            var r=_demo.LastSap;
            _painelCategoria[CategoriaMasterdataR3.Transportadoras].Text=$"BP autorizado: {_demo.InitialBp}\r\nSAP: {(r is null?"NÃO CONSULTADO":r.State)}\r\nHTTP: {r?.HttpStatus?.ToString()??"NÃO EXECUTADO"}\r\nIdentidade BP: {(r?.IdentityMatch==true?"CONFIRMADA":"NÃO APURADA")}\r\nCRM010: {r?.Crm010Eligibility??"NÃO APURADO"}\r\nNome / razão social presentes: {r?.NamePresent.ToString()??"NÃO APURADO"} / {r?.LegalNamePresent.ToString()??"NÃO APURADO"}\r\n\r\nGuardian: {_demo.GuardianStatus}\r\n{_demo.GuardianHistoricalStatus}\r\n\r\nJournal / checkpoint PostgreSQL: NÃO COMPROVADOS. Nenhuma escrita autorizada.";
            foreach(var entry in _painelCategoria.Where(e=>e.Key!=CategoriaMasterdataR3.Transportadoras))
                entry.Value.Text=$"Categoria: {entry.Key}\r\nStatus: BLOQUEADO — consulta e escrita não disponíveis nesta demo.\r\nNenhuma execução realizada.";
            _logs.Text="BLOQUEADO: histórico operacional PostgreSQL indisponível. Evidências históricas não representam logs de execução desta sessão.";
            _resumo.Text=_demo.SapStatus??"DEMO R50: somente consultas manuais; nenhum timer/worker ativo. Dados não consultados permanecem NÃO APURADOS.";return;
        }
        _reconcile.Enabled=!_reconciliationRunning&&_lifecycle?.Reconciliation?.CanActivate==true;
        _sync.Enabled=_lifecycle?.Reconciliation is null&&!_manualRunning&&_lifecycle?.Snapshot.Active==true&&!_lifecycle.Snapshot.Cycling&&!string.IsNullOrWhiteSpace(_bp.Text);

        _readOnly.Enabled=!_readOnlyRunning&&_lifecycle?.ManualReadOnlyAvailable(_bp.Text.Trim())==true;
        var estados = _orquestrador.Estados();
        foreach (var e in estados)
        {
            if (!_painelCategoria.TryGetValue(e.Categoria, out var visual)) continue;
            static string Exibir(int? n) => n?.ToString() ?? "NÃO APURADO";
            visual.Text = $"Categoria: {e.Categoria}\r\nStatus: {e.Estado}\r\n" +
                $"Última execução (UTC): {e.UltimaExecucaoUtc?.ToString("u") ?? "NÃO EXECUTADA"}\r\n" +
                $"Processados: {Exibir(e.Processados)}\r\n" +
                $"Sincronizados: {Exibir(e.Sincronizados)}\r\n" +
                $"Pendentes: {Exibir(e.Pendentes)}\r\n" +
                $"Falhas: {Exibir(e.Falhas)}\r\n" +
                $"Checkpoint: {e.Checkpoint ?? "NÃO COMPROVADO"}\r\n" +
                $"Motivo: {e.Motivo}\r\n";
        }
        if(_lifecycle?.LastReconciliation is {} reconciliation&&_painelCategoria.TryGetValue(CategoriaMasterdataR3.Transportadoras,out var panel))
            panel.Text=$"Reconciliação SAP somente leitura: {reconciliation.State}\r\nCódigo: {reconciliation.Code}\r\nBPs observados: {reconciliation.CachedBps}\r\nCheckpoint PostgreSQL: NÃO COMPROVADO\r\nNão autoriza escrita Guardian.";
        _logs.Lines = _orquestrador.Logs().Select(l =>
            $"{l.QuandoUtc:u} | {l.Origem} | {l.Categoria?.ToString() ?? "GLOBAL"} | {l.Codigo}").ToArray();
        var ultima = estados.Where(e => e.UltimaExecucaoUtc.HasValue)
            .Select(e => e.UltimaExecucaoUtc!.Value).DefaultIfEmpty().Max();
        _resumo.Text = "Última execução: " + (ultima == default ? "NÃO EXECUTADA" : ultima.ToString("u")) +
            "    | Próxima consulta: " + (_lifecycle?.Snapshot.NextQueryUtc?.ToString("u")??"INATIVA") +
            "    | Intervalo: 30 s | Eventos UI locais; logs operacionais no PostgreSQL" + (_lifecycle?.Snapshot.Active==true?"":" | HML BLOQUEADO");
    }
}
