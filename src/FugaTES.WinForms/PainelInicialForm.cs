namespace FugaTES.WinForms;

public partial class PainelInicialForm : Form
{
    private readonly Color corMenuAtivo = Color.FromArgb(34, 102, 141);
    private readonly Color corMenuPadrao = Color.FromArgb(38, 43, 51);

    private readonly Transportadoras.CarrierDesktopLifecycle? carrier;
    private readonly Transportadoras.SafeDemoReadOnlySessionR50? demo;
    private bool carrierStopping,carrierStopped;
    private readonly Monitor.UnifiedDesktopSession? unified;
    private readonly System.Windows.Forms.Timer connectionTimer=new(){Interval=10000};
    private readonly Label connectionStatus=new(){Name="PhysicalConnectionStatus",AutoSize=false,Height=100,
        Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right,ForeColor=Color.DarkRed};
    public PainelInicialForm(Transportadoras.CarrierDesktopLifecycle? carrier=null,Transportadoras.SafeDemoReadOnlySessionR50? demo=null,Monitor.UnifiedDesktopSession? unified=null)
    {
        this.carrier=carrier;this.demo=demo;this.unified=unified;
        InitializeComponent();
        SelecionarBotaoMenu(BtnInicio);
        if(unified is not null)
        {
            ConfigureUnifiedHome();
            unified.ConnectionChanged+=ShowPhysicalConnection;
            connectionTimer.Tick+=async(_,_)=>await unified.RefreshConnectionAsync();
            FormClosed+=(_,_)=>{connectionTimer.Stop();connectionTimer.Dispose();unified.ConnectionChanged-=ShowPhysicalConnection;};
            return;
        }
        LblAmbiente.Text = "Ambiente: " + Runtime.DesktopRuntime.EnvironmentDisplay;
        LblSubtituloSistema.Text = "Ambiente " + Runtime.DesktopRuntime.EnvironmentDisplay + " — consultas operacionais";
        LblBanco.Text = Runtime.DesktopRuntime.Connected ? "Banco: Conectado" : "Banco: Não conectado";
        LblResumoConteudo.Text = Runtime.DesktopRuntime.Connected
            ? "PostgreSQL validado. Desktop em modo somente leitura."
            : Runtime.DesktopRuntime.FailureText;
        if(unified is not null)ConfigureUnifiedHome();
        if(demo is not null){
            Text=File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.execution.R57.json"))?"FugaTES — DEMO R57 — OFFLINE":File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.preview.R55.json"))?"FugaTES — DEMO R55 — OFFLINE":File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.preview.R53.json"))?"FugaTES — DEMO R53 — SOMENTE LEITURA":demo.GuardianRead is null?"FugaTES — DEMO R50 — SOMENTE LEITURA":"FugaTES — DEMO R50B — SOMENTE LEITURA";
            LblResumoConteudo.AutoSize=false;
            LblResumoConteudo.Size=new Size(Math.Max(300,PnlConteudo.ClientSize.Width-56),180);
            LblResumoConteudo.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
            MostrarDemo();
        }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if(unified is not null){connectionTimer.Start();await unified.RefreshConnectionAsync();return;}
        if(demo is not null)return;
        carrier?.Start();
        if (Runtime.DesktopRuntime.Connected) BtnMonitoramento_Click(this, EventArgs.Empty);
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if(demo is not null&&!carrierStopped){e.Cancel=true;base.OnFormClosing(e);if(carrierStopping)return;carrierStopping=true;await demo.StopAsync();carrierStopped=true;Close();return;}
        if(carrier is null||carrierStopped){base.OnFormClosing(e);return;}
        e.Cancel=true;base.OnFormClosing(e);
        if(carrierStopping)return;
        carrierStopping=true;LblResumoConteudo.Text="Encerrando sincronização de transportadoras com segurança...";
        await carrier.StopAsync();
        carrierStopped=true;Close();
    }

    private void BtnInicio_Click(object? sender, EventArgs e)
    {
        SelecionarBotaoMenu(BtnInicio);
        if(unified is not null){LblTituloConteudo.Text="Início";ConfigureUnifiedHome();return;}
        LblAmbiente.Text = "Ambiente: " + Runtime.DesktopRuntime.EnvironmentDisplay;
        LblSubtituloSistema.Text = "Ambiente " + Runtime.DesktopRuntime.EnvironmentDisplay + " — consultas operacionais";
        LblBanco.Text = Runtime.DesktopRuntime.Connected ? "Banco: Conectado" : "Banco: Não conectado";
        LblResumoConteudo.Text = Runtime.DesktopRuntime.Connected
            ? "PostgreSQL validado. Desktop em modo somente leitura."
            : Runtime.DesktopRuntime.FailureText;
        LblTituloConteudo.Text = "Início";
        if(demo is not null){MostrarDemo();return;}
        LblResumoConteudo.Text = Runtime.DesktopRuntime.Connected ? "PostgreSQL validado. Desktop em modo somente leitura." : Runtime.DesktopRuntime.FailureText;
    }

    private void BtnIntegracoes_Click(object? sender, EventArgs e)
    {
        SelecionarBotaoMenu(BtnIntegracoes);
        LblTituloConteudo.Text = "Integrações";
        LblResumoConteudo.Text = "Contratos ACM e Cadastros SAP → Guardian";
        using var contratos = new FrmIntegracoes(carrier,demo,unified is null?null:new Func<FrmMonitorMvp>(unified.CreateMonitor),
            unified is null?null:new Func<FrmContratosAcm>(unified.CreateContracts),unified is null?null:new Func<Transportadoras.MasterdataR3.FrmTransportadoraMasterdata>(unified.CreateCarriers),
            unified is null?null:new Func<Guardian.FrmProtocolosPreCadastro>(unified.CreateProtocols),
            unified is null?null:new Func<Toledo.FrmRecebimentosToledo>(unified.CreateToledoReceipts));
        contratos.ShowDialog(this);
    }

    private void BtnMonitoramento_Click(object? sender, EventArgs e)
    {
        SelecionarBotaoMenu(BtnMonitoramento);
        LblTituloConteudo.Text = "Monitoramento";
        if(demo is not null){LblResumoConteudo.Text="Monitor operacional: PostgreSQL DEV não conectado. Atualizações automáticas desabilitadas.";return;}
        using var monitor = unified?.CreateMonitor() ?? Monitor.MonitorComposition.Criar();
        monitor.ShowDialog(this);
    }

    private void BtnConfiguracoes_Click(object? sender, EventArgs e)
    {
        SelecionarBotaoMenu(BtnConfiguracoes);
        LblTituloConteudo.Text = "Configurações";
        if(unified is not null){LblResumoConteudo.Text=unified.Summary+"\r\nPerfis e credenciais anteriores preservados; nenhum worker ou envio habilitado.";return;}
        if(demo is not null){MostrarDemo();return;}
        LblResumoConteudo.Text = Runtime.DesktopRuntime.Connected ? "Configuração HML validada; segredos protegidos pelo Windows." : Runtime.DesktopRuntime.FailureText;
    }

    private void ConfigureUnifiedHome()
    {
        if(unified is null)return;
        MinimumSize=new Size(960,760);Size=new Size(1100,760);
        Text="FugaTES DEV110 — UNIFICADO — v"+Monitor.UnifiedDesktopSession.Version;
        LblVersao.Text="v"+Monitor.UnifiedDesktopSession.Version;
        LblAmbiente.Text="DEV110 — unificado";
        LblSubtituloSistema.Text="Monitor ACM, contratos e integrações consolidados";
        LblBanco.Text="PostgreSQL físico 5433: leitura";
        LblSap.Text="SAP: bloqueado";
        LblToledo.Text="Toledo: recebimentos / JSON";
        if(!PnlRodape.Controls.Cast<Control>().Any(c=>c.Name=="UnifiedStatus"))
        {
            var footer=new FlowLayoutPanel{Name="UnifiedStatus",Dock=DockStyle.Fill,Padding=new Padding(12,10,0,0),WrapContents=true};
            foreach(var label in new[]{LblAmbiente,LblVersao,LblSap,LblToledo,LblBanco})
            {label.Margin=new Padding(4,2,14,2);footer.Controls.Add(label);}
            PnlRodape.Controls.Add(footer);
        }
        LblResumoConteudo.AutoSize=false;LblResumoConteudo.Size=new Size(Math.Max(500,PnlConteudo.ClientSize.Width-56),150);
        LblResumoConteudo.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
        LblResumoConteudo.Text=unified.Summary;
        if(connectionStatus.Parent is null)
        {
            connectionStatus.Location=new Point(28,220);connectionStatus.Width=Math.Max(500,PnlConteudo.ClientSize.Width-56);
            PnlConteudo.Controls.Add(connectionStatus);
            var reconnect=new Button{Name="ReconnectPhysicalRead",Text="Conectar / Atualizar leitura",AutoSize=true,Location=new Point(28,325)};
            reconnect.Click+=async(_,_)=>await unified.RefreshConnectionAsync();PnlConteudo.Controls.Add(reconnect);
        }
        ShowPhysicalConnection();
        if(!PnlConteudo.Controls.Cast<Control>().Any(c=>c.Name=="UnifiedModules"))
        {
            var grid=new DataGridView{Name="UnifiedModules",Dock=DockStyle.Bottom,Height=230,ReadOnly=true,AllowUserToAddRows=false,
                AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};
            grid.DataSource=new[]{
                new{Módulo="Monitor ACM / contratos / IntegrationId",Estado="Disponível",Origem="Readback físico privado R165AL"},
                new{Módulo="H4, journal, tentativas e correlação",Estado="Somente leitura",Origem="PostgreSQL 5433; histórico detalhado conforme fonte"},
                new{Módulo="Guardian SOAP / REST / protocolo",Estado="Adapters integrados",Origem="Teste R165AL usa Guardian SIMULADO; sem CREATE real"},
                new{Módulo="Transportadoras / cadastros",Estado="Telas preservadas",Origem="Ações empresariais não ativadas por esta inicialização"},
                new{Módulo="Callback Guardian R4.1",Estado="Compatível",Origem="Código consolidado; nenhum deploy corporativo"},
                new{Módulo="Recebimentos Guardian / Toledo",Estado="Consulta e JSON",Origem="Recebimento persistido DEV5433; devolutiva SAP depende de correlação legítima"}};
            PnlConteudo.Controls.Add(grid);
        }
    }

    private void ShowPhysicalConnection()
    {
        if(unified is null || IsDisposed || Disposing)return;
        if(InvokeRequired){if(IsHandleCreated)BeginInvoke(ShowPhysicalConnection);return;}
        connectionStatus.Text=unified.Connection.Display;
        connectionStatus.ForeColor=unified.Connection.Connected?Color.DarkGreen:Color.DarkRed;
        LblBanco.Text=unified.Connection.Connected?"PostgreSQL 5433: CONECTADO":"PostgreSQL: DESCONECTADO";
    }

    private void MostrarDemo()
    {
        LblAmbiente.Text=File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.execution.R57.json"))?"Demo R57":File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.preview.R55.json"))?"Demo R55":File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.preview.R53.json"))?"Demo R53":demo?.GuardianRead is null?"Demo R50":"Demo R50B";
        LblVersao.Text="Somente leitura";
        LblSap.Text="SAP: manual";
        LblToledo.Visible=false;
        LblBanco.Location=LblToledo.Location;
        LblSubtituloSistema.Text="Consultas manuais — sem sincronização automática";
        LblBanco.Text="PostgreSQL DEV: NÃO CONECTADO";
        if(File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.execution.R57.json")))
        {
            LblResumoConteudo.Text="R57 — OFFLINE / SEM ESCRITAS\r\n\r\nFluxo começa no POST SAP; próximo contrato informado 1106. Nenhum evento real carregado nesta sessão.\r\nContrato SOAP e protocolo REST em etapas distintas, com reserva durável antes de envio.\r\nCadastros e consultas reais desabilitados nesta sessão. Runtime/journal PostgreSQL não atestados.\r\nNenhum worker/timer iniciado.";
            return;
        }
        LblResumoConteudo.Text="Telas liberadas para teste.\r\n\r\nSAP: consulta manual do BP 20000014 pelo perfil efetivo.\r\nGuardian: consultar o status e o binding manual na tela de Cadastros.\r\n"+(File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.preview.R55.json"))?"ACM: contrato mestre SOAP e agendamento REST em prévias separadas; nenhum envio.":File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.preview.R53.json"))?"ACM: prévia REST local; PostgreSQL separado, sem consulta de banco.":"ACM: leitura de banco bloqueada.")+"\r\n\r\nWorkers, timers e escritas automáticas desabilitados.";
    }

    private void BtnSair_Click(object? sender, EventArgs e)
    {
        Close();
    }

    private void SelecionarBotaoMenu(Button botaoSelecionado)
    {
        foreach (var botao in PnlMenuLateral.Controls.OfType<Button>())
        {
            botao.BackColor = botao == botaoSelecionado ? corMenuAtivo : corMenuPadrao;
        }
    }
}
