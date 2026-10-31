using FugaTES.WinForms.Runtime;

namespace FugaTES.WinForms.Transportadoras.MasterdataR3;

public partial class FrmTransportadoraMasterdata : Form
{
    CarrierMasterdataSession? session;
    readonly CancellationTokenSource lifetime=new();
    bool busy;
    string? feedback;
    public FrmTransportadoraMasterdata(bool readOnly=false,
        Func<string?,CancellationToken,Task<FugaTES.Persistencia.Consultas.UnifiedDev.DevMasterdataSnapshot>>? readDatabase=null)
    {
        InitializeComponent();
        if(readOnly)
        {
            if(readDatabase is not null)
            {
                Text="Transportadoras — PostgreSQL DEV 5433 — journal somente leitura";
                cadastrar.Enabled=false;bp.Enabled=consultar.Enabled=true;bp.PlaceholderText="Filtro BP; vazio = todos";
                consultar.Text="Consultar journal DEV";
                async Task ReadDevAsync()
                {
                    if(busy)return;busy=true;consultar.Enabled=false;
                    try
                    {
                        var data=await readDatabase(bp.Text,lifetime.Token);if(IsDisposed||lifetime.IsCancellationRequested)return;
                        historico.DataSource=data.Rows.ToArray();
                        detalhes.Text="Fonte: fugates_dev / porta física 5433 / "+data.Schema+".carrier_single_create_journal\r\nSomente estados persistidos do DEV. Nenhum cadastro HML foi copiado; dados cadastrais SAP/Guardian não são inferidos do journal.";
                        status.Text=$"CONECTADO — {data.Total} registro(s) no journal DEV; {data.Rows.Count} exibido(s). CREATE desabilitado.";
                    }
                    catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
                    catch{if(!IsDisposed){historico.DataSource=null;detalhes.Text="";status.Text="DESCONECTADO — leitura MasterData DEV indisponível; confira Conexão na tela inicial.";}}
                    finally{busy=false;if(!IsDisposed){consultar.Enabled=true;cadastrar.Enabled=false;}}
                }
                consultar.Click+=async(_,_)=>await ReadDevAsync();Shown+=async(_,_)=>await ReadDevAsync();
                FormClosed+=(_,_)=>{lifetime.Cancel();};
                return;
            }
            Text="Transportadoras — DEV110 — consulta operacional pendente de ativação";
            bp.Enabled=consultar.Enabled=cadastrar.Enabled=false;
            status.Text="Tela integrada. Consultas SAP/Guardian e CREATE não ativados nesta sessão consultiva. Perfis anteriores preservados.";
            return;
        }
        consultar.Click+=async(_,_)=>await RunAsync(async()=>
        {
            if(session?.BusinessPartner!=bp.Text||!session.SelectionValid)
            {
                if(session is not null)await session.DisposeAsync();session=null;
                session=await CarrierMasterdataSession.OpenAsync(bp.Text,lifetime.Token);
            }
            await session.ConsultAsync(lifetime.Token);Render();
        });
        cadastrar.Click+=async(_,_)=>await RunAsync(async()=>
        {
            if(session?.CanCreate!=true)return;
            var result=await session.CreateAsync(lifetime.Token);
            feedback=$"{result.State}: {result.Code} — HTTP {session.CreateHttpStatus?.ToString()??"não observado"}; código {session.ReturnedCode??"não observado"}; sequencial {session.ReturnedSequence?.ToString()??"não observado"}";
        });
        bp.TextChanged+=(_,_)=>{session?.InvalidateSelection();cadastrar.Enabled=false;detalhes.Text="";historico.DataSource=null;status.Text="Consulte o alvo selecionado.";};
        FormClosing+=(_,e)=>{if(busy){e.Cancel=true;status.Text="Operação em andamento. Aguarde o resultado; não repita o envio.";}};
        FormClosed+=async(_,_)=>{lifetime.Cancel();if(session is not null)await session.DisposeAsync();lifetime.Dispose();};
    }
    async Task RunAsync(Func<Task> operation)
    {
        if(busy)return;busy=true;feedback=null;bp.Enabled=consultar.Enabled=cadastrar.Enabled=false;UseWaitCursor=true;
        try{await operation();}
        catch(OperationCanceledException){feedback="Operação cancelada. Consulte o journal antes de qualquer nova tentativa.";}
        catch(DesktopStartupException e){feedback=e.Code;}
        catch{feedback="MASTERDATA_OPERATION_FAILED — consulte o histórico; nenhum reenvio automático.";}
        finally{if(session is not null)Render();if(feedback is not null)status.Text=feedback+"\r\n"+status.Text;busy=false;UseWaitCursor=false;bp.Enabled=consultar.Enabled=true;cadastrar.Enabled=session?.BusinessPartner==bp.Text&&session.CanCreate;}
    }
    void Render()
    {
        if(session is null)return;
        detalhes.Text=$"BP: {session.BusinessPartner}\r\nNome: {session.Name}\r\nRazão social: {session.LegalName}\r\n"+
            $"CNPJ SAP: {session.Cnpj}\r\n{session.SapStatus}\r\nGuardian: {session.GuardianStatus}\r\n\r\nJSON preparado para conferência (dados transitórios, sem gravação em logs):\r\n{session.JsonPreview}";
        status.Text=session.CreateReadiness+"\r\nHistórico: "+session.HistoryReadStatus+
            $" | Sequencial Guardian: {session.ReturnedSequence?.ToString()??"não observado"}\r\nCallback: "+session.CallbackStatus;
        if(session.LastResult is {} result)status.Text=$"{result.State}: {result.Code} — tentativa {result.AttemptId}\r\n"+status.Text;
        historico.DataSource=session.History.ToArray();
    }
}
