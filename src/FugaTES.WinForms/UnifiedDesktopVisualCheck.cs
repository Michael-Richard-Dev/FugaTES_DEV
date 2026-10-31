using System.Drawing.Imaging;
using System.Text.Json;

namespace FugaTES.WinForms;

// Explicit local UI verification/export. Never invokes a business send button.
public static class UnifiedDesktopVisualCheck
{
    public static async Task RunAsync(PainelInicialForm shell,Monitor.UnifiedDesktopSession session,string folder)
    {
        Directory.CreateDirectory(folder);
        try
        {
            var connectionUntil=DateTime.UtcNow.AddSeconds(25);
            while(!session.Connection.Connected && session.Connection.Message=="Aguardando conexão de leitura." && DateTime.UtcNow<connectionUntil)await Task.Delay(200);
            await Task.Delay(500);Capture(shell,folder,"MAIN");
            using(var integrations=new FrmIntegracoes(monitorAcm:session.CreateMonitor,contractsRead:session.CreateContracts,carriersRead:session.CreateCarriers,protocolsRead:session.CreateProtocols))
            {integrations.Show(shell);integrations.TopMost=true;await Task.Delay(500);Capture(integrations,folder,"INTEGRATIONS");integrations.Close();}
            object? contractsResult=null,masterdataResult=null;
            using(var contracts=session.CreateContracts())
            {
                contracts.Show(shell);contracts.TopMost=true;
                var grid=Controls(contracts).OfType<DataGridView>().Single();
                var deadline=DateTime.UtcNow.AddSeconds(20);
                while(grid.Rows.Count==0&&DateTime.UtcNow<deadline)await Task.Delay(200);
                var rows=grid.Rows.Cast<DataGridViewRow>().Select(r=>r.DataBoundItem).OfType<FugaTES.Aplicacao.Consultas.ContratosAcm.ContratoAcmLeitura>().ToArray();
                var retryInspect=rows.FirstOrDefault(r=>r.EstadoIntegracao=="RESULTADO_INDETERMINADO")??rows.FirstOrDefault();
                if(retryInspect is not null)await contracts.InspectRetryForEvidenceAsync(retryInspect.IntegracaoId);
                Capture(contracts,folder,"CONTRACTS_REAL");
                object? jsonCheck=null;
                var selected=Array.FindIndex(rows,r=>r.IntegracaoId!=FugaTES.Persistencia.Consultas.UnifiedDev.DevReadScope.PilotId&&!string.IsNullOrWhiteSpace(r.Item));
                if(selected<0)selected=Array.FindIndex(rows,r=>r.IntegracaoId!=FugaTES.Persistencia.Consultas.UnifiedDev.DevReadScope.PilotId);
                if(selected<0&&rows.Length>0)selected=0;
                if(selected>=0)
                {
                    using var timer=new System.Windows.Forms.Timer{Interval=600};
                    timer.Tick+=(_,_)=>
                    {
                        var dialog=Application.OpenForms.OfType<Acm.FrmContratoJson>().FirstOrDefault();
                        if(dialog is null)return;
                        timer.Stop();
                        var persisted=Controls(dialog).OfType<TextBox>().Single(t=>t.AccessibleName=="JSON da projecao persistida");
                        bool valid;try{using var doc=JsonDocument.Parse(persisted.Text);valid=doc.RootElement.ValueKind==JsonValueKind.Object;}catch{valid=false;}
                        jsonCheck=new{visible=dialog.Visible,title=dialog.Text,persistedJsonValid=valid,source="PROJECAO_PERSISTIDA",payloadExported=false};
                        dialog.Close();
                    };
                    timer.Start();contracts.AbrirJsonSeAcionado(selected,grid.Columns["json"]!.Index);timer.Stop();
                }
                contractsResult=new{count=rows.Length,rows=rows.Select(r=>new{r.NumeroContrato,r.Item,r.IntegracaoId,r.Versao,r.FonteDados}),jsonCheck,manualRetry=contracts.ExportRetryReadiness()};
                contracts.Close();
            }
            using(var carriers=session.CreateCarriers())
            {
                carriers.Show(shell);carriers.TopMost=true;await Task.Delay(1800);
                Capture(carriers,folder,"MASTERDATA_REAL");
                masterdataResult=new{title=carriers.Text,labels=Controls(carriers).OfType<Label>().Select(l=>l.Text).ToArray(),
                    buttons=Controls(carriers).OfType<Button>().Select(b=>new{b.Text,b.Enabled}).ToArray(),
                    rows=Controls(carriers).OfType<DataGridView>().Select(g=>g.Rows.Count).ToArray()};
                carriers.Close();
            }
            var precadastroResult=await VerifyProtocolsMenuAsync(shell,session,folder);
            var monitor=session.CreateMonitor();
            monitor.Show(shell);monitor.TopMost=true;
            await Task.Delay(1600);
            var until=DateTime.UtcNow.AddSeconds(25);
            while(DateTime.UtcNow<until && !Controls(monitor).OfType<DataGridView>().Any(g=>g.Rows.Count>0))await Task.Delay(250);
            Capture(monitor,folder,"PHYSICAL_MONITOR");
            var tabs=Controls(monitor).OfType<TabControl>().Single();
            tabs.SelectedTab=Controls(monitor).OfType<TabPage>().Single(t=>t.Name=="ProtocolTab");
            await Task.Delay(600);Capture(monitor,folder,"PROTOCOL");
            var text=Controls(monitor).OfType<TextBox>().Where(x=>x.Name.StartsWith("Protocol")).ToDictionary(x=>x.Name,x=>x.Text);
            var grids=Controls(monitor).OfType<DataGridView>().Where(g=>g.Rows.Count>0).Select(g=>new{name=g.Name,rows=g.Rows.Count}).ToArray();
            File.WriteAllText(Path.Combine(folder,"UI_RESULT.json"),JsonSerializer.Serialize(new{version=Monitor.UnifiedDesktopSession.Version,
                title=shell.Text,monitorTitle=monitor.Text,Visible=shell.Visible,grids,text,
                sendEnabled=Controls(monitor).Single(x=>x.Name=="SendProtocol").Enabled,
                modules=new[]{"Monitor ACM","Contratos / IntegrationId","H4 / journal","SOAP / REST","Transportadoras","Guardian R4.1","Toledo pendente"},
                connection=session.Connection,contracts=contractsResult,masterdata=masterdataResult,precadastro=precadastroResult,
                capturedUtc=DateTimeOffset.UtcNow},new JsonSerializerOptions{WriteIndented=true}));
            monitor.TopMost=false; // Leave the actual monitor open for the operator.
            shell.TopMost=false;
        }
        catch
        {
            File.WriteAllText(Path.Combine(folder,"UI_FAILURE.txt"),"UNIFIED_UI_SMOKE_FAILED — no business operation was performed");
            shell.TopMost=false;
        }
    }
    private static async Task<object> VerifyProtocolsMenuAsync(Form shell,Monitor.UnifiedDesktopSession session,string folder)
    {
        using var menu=new FrmIntegracoes(monitorAcm:session.CreateMonitor,contractsRead:session.CreateContracts,
            carriersRead:session.CreateCarriers,protocolsRead:session.CreateProtocols);
        menu.Show(shell);menu.TopMost=true;await Task.Delay(300);
        var entry=Controls(menu).OfType<Button>().Single(b=>b.Name=="protocolosPreCadastro");
        Capture(menu,folder,"INTEGRATIONS_CONSOLIDATED");
        object? result=null;
        using var observer=new System.Windows.Forms.Timer{Interval=100};
        observer.Tick+=async (_,_)=>
        {
            var form=Application.OpenForms.OfType<Guardian.FrmProtocolosPreCadastro>().FirstOrDefault();
            if(form is null)return;observer.Stop();
            try
            {
                var grid=Controls(form).OfType<DataGridView>().Single();
                var deadline=DateTime.UtcNow.AddSeconds(35);
                while(grid.Rows.Count==0&&DateTime.UtcNow<deadline)await Task.Delay(100);
                var rows=grid.Rows.Cast<DataGridViewRow>().Select(r=>r.DataBoundItem).OfType<Guardian.ProtocoloPreCadastroExibicao>().ToArray();
                // Read-only observation: no retry handler or confirmation is invoked.
                grid.Columns["json"]!.DisplayIndex=0;grid.Columns[Guardian.FrmProtocolosPreCadastro.RestRetryColumn]!.DisplayIndex=1;
                Capture(form,folder,"PRECADASTRO_REAL");
                var filter=Controls(form).OfType<TextBox>().Single(t=>t.AccessibleName=="Contrato");
                var target=rows.FirstOrDefault(r=>r.Origemlinha.ResultadoIndeterminado)??rows.FirstOrDefault();
                bool filterPass=false,jsonPass=false;
                if(target is not null)await form.InspectRestRetryForEvidenceAsync(target.Origemlinha.IntegracaoId);
                if(target is not null)
                {
                    filter.Text=target.Origemlinha.Contrato;await form.ConsultarAsync();
                    filterPass=grid.Rows.Count>0&&grid.Rows.Cast<DataGridViewRow>().All(r=>((Guardian.ProtocoloPreCadastroExibicao)r.DataBoundItem!).Contrato.Contains(filter.Text,StringComparison.OrdinalIgnoreCase));
                    Capture(form,folder,"PRECADASTRO_FILTERED");
                    using var closer=new System.Windows.Forms.Timer{Interval=300};
                    closer.Tick+=(_,_)=>
                    {
                        var json=Application.OpenForms.OfType<Guardian.FrmProtocoloJson>().FirstOrDefault();
                        if(json is null)return;closer.Stop();
                        jsonPass=json.Visible&&Controls(json).OfType<TextBox>().Count(t=>t.ReadOnly)==3;
                        Capture(json,folder,"PRECADASTRO_JSON");json.Close();
                    };
                    closer.Start();form.Acionar(0,grid.Columns["json"]!.Index);closer.Stop();
                }
                result=new{menuEnabled=entry.Enabled,openedViaMenu=true,count=rows.Length,filterPass,jsonPass,
                    jsonButton=grid.Columns.Contains("json"),retryButton=grid.Columns.Contains(Guardian.FrmProtocolosPreCadastro.RestRetryColumn),
                    command= form.ExportRestRetryReadiness(),
                    realRetryPossible=rows.Any(r=>FugaTES.Aplicacao.Consultas.ProtocolosGuardian.ReenvioProtocolo.Avaliar(r.Origemlinha).PodeExecutar),
                    rows=rows.Select(r=>new{r.Integracao,r.Contrato,r.Protocolo,r.Origem,r.Reenvio}),
                    source="POSTGRESQL DEV 5433 / API PRIVADA GET-only"};
            }
            finally{form.Close();}
        };
        observer.Start();entry.PerformClick();observer.Stop();menu.Close();
        return result??new{menuEnabled=entry.Enabled,openedViaMenu=false};
    }
    private static void Capture(Form form,string folder,string name)
    {
        form.TopMost=true;form.BringToFront();form.Activate();form.Refresh();
        var bounds=Rectangle.Intersect(form.Bounds,Screen.FromControl(form).Bounds);
        using var bitmap=new Bitmap(bounds.Width,bounds.Height);
        using(var graphics=Graphics.FromImage(bitmap))graphics.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size);
        bitmap.Save(Path.Combine(folder,name+"-screen.png"),ImageFormat.Png);
        using var render=new Bitmap(form.Width,form.Height);form.DrawToBitmap(render,new Rectangle(Point.Empty,form.Size));
        render.Save(Path.Combine(folder,name+"-render.png"),ImageFormat.Png);
    }
    private static IEnumerable<Control> Controls(Control root)=>root.Controls.Cast<Control>().SelectMany(x=>new[]{x}.Concat(Controls(x)));
}
