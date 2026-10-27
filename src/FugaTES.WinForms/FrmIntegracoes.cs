namespace FugaTES.WinForms;

public partial class FrmIntegracoes : Form
{
    public FrmIntegracoes(Transportadoras.CarrierDesktopLifecycle? lifecycle=null,Transportadoras.SafeDemoReadOnlySessionR50? demo=null,Func<FrmMonitorMvp>? monitorAcm=null)
    {
        InitializeComponent();
        var r57=File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.execution.R57.json"));
        var r55=File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.preview.R55.json"));
        if(demo is not null)Text=r57?"Integrações — DEMO R57 — offline":r55?"Integrações — DEMO R55 — offline":File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.preview.R53.json"))?"Integrações — DEMO R53 — somente leitura":"Integrações — DEMO R50 — somente leitura";
        guardian.Click += (_,_) => {using var tela=new Transportadoras.MasterdataR3.FrmCadastrosGuardianR3(lifecycle?.Masterdata,lifecycle,demo);tela.ShowDialog(this);};
        contratos.Click += (_, _) => { if(monitorAcm is not null){using var read=monitorAcm();read.ShowDialog(this);return;} using var tela = demo is null?Acm.ContratosAcmComposition.Criar():r57?new FrmContratosAcm(new FugaTES.Aplicacao.Consultas.ContratosAcm.ContratosAcmConsulta(new DemoAcmDisconnected()),execucaoR57:true):(r55||File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.preview.R53.json")))?new FrmContratosAcm(new FugaTES.Aplicacao.Consultas.ContratosAcm.ContratosAcmConsulta(new DemoAcmDisconnected()),restPreview:true):new FrmContratosAcm(new FugaTES.Aplicacao.Consultas.ContratosAcm.ContratosAcmConsulta(new DemoAcmDisconnected()),operationalBlocker:"ACM: BLOQUEADO — PostgreSQL DEV não conectado/atestado.\r\nCentro 3011; contrato 0000001031/V2: nenhuma consulta realizada.\r\nSOAP ConsultaContrato isolada não executada; igualdade Guardian não comprovada.\r\nCadastraContrato, AlteraContrato e SAP POST desabilitados; nenhum processamento automático."); tela.ShowDialog(this); };
        // No modo unificado o botao "Contratos ACM" abre o Monitor ACM do leitor fisico,
        // que nao expoe o JSON por contrato + item. Esta entrada adicional abre a tela de
        // contratos propriamente dita, somente leitura, sem tirar o monitor do lugar.
        contratosJson.Visible = monitorAcm is not null;
        contratosJson.Click += (_, _) => { using var tela = Acm.ContratosAcmComposition.Criar(); tela.ShowDialog(this); };
        transportadoras.Click += (_, _) => { if(demo is not null){using var telaDemo=new Transportadoras.MasterdataR3.FrmCadastrosGuardianR3(demo:demo);telaDemo.ShowDialog(this);}else{using var tela = new Transportadoras.MasterdataR3.FrmTransportadoraMasterdata(readOnly:monitorAcm is not null); tela.ShowDialog(this);} };
    }
    private sealed class DemoAcmDisconnected:FugaTES.Aplicacao.Consultas.ContratosAcm.IContratosAcmReadRepository
    {
        public Task<IReadOnlyList<FugaTES.Aplicacao.Consultas.ContratosAcm.ContratoAcmLeitura>> ConsultarAsync(string centro,CancellationToken ct)=>throw new InvalidOperationException("R50_DATABASE_NOT_ACTIVATED");
    }
}
