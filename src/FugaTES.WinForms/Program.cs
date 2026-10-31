using System.Security.Principal;
using FugaTES.WinForms.Runtime;

namespace FugaTES.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length>0 && args[0]=="--local-simulation")
        {
            if(args.Length!=3 || !Guid.TryParse(args[2],out var runId) || !Path.IsPathFullyQualified(args[1]))
                throw new InvalidOperationException("LOCAL_DEMO_ARGUMENTS_REQUIRED");
            var reader=new Monitor.LocalSimulationReadback(args[1],runId);
            using var form=new FrmMonitorMvp(new FugaTES.Aplicacao.Consultas.Monitor.MonitorOperacionalConsulta(reader),
                reader.Estado,leituraExportada:true,estadoFonte:()=>reader.Estado,protocolClient:reader);
            form.EnableLocalDemoPresentation(Path.Combine(Path.GetDirectoryName(args[1])!,"screens"));
            form.Shown+=(_,_)=>File.WriteAllText(args[1]+".monitor-ready",runId.ToString());
            Application.Run(form);
            return;
        }
        if(args.Length==0 || args[0]!="--monitor-only")
        {
            UnifiedDesktopStartup.Run(args);
            return;
        }
#if FUGATES_ACM_MONITOR_R165M
        using var session = Monitor.AcmMonitorSession.Carregar(AppContext.BaseDirectory);
        using var protocolSession = Monitor.GuardianProtocolSession.Load(AppContext.BaseDirectory);
        Application.Run(new FrmMonitorMvp(session.Consulta, session.Estado, lerAcm: session.DetalharAcmAsync,
            leituraExportada: session.LeituraExportada, estadoFonte: () => session.Estado, protocolClient: protocolSession.Client));
#else
        using var instance = new Mutex(false, (Transportadoras.SafeDemoReadOnlySessionR50.Enabled?(File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.execution.R57.json"))?"Local\\FugaTES.SafeDemo.R57.":File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.preview.R55.json"))?"Local\\FugaTES.SafeDemo.R55.":File.Exists(Path.Combine(AppContext.BaseDirectory,"Acm.preview.R53.json"))?"Local\\FugaTES.SafeDemo.R53.":File.Exists(Path.Combine(AppContext.BaseDirectory,"Guardian.read.R50B.json"))?"Local\\FugaTES.SafeDemo.R50B.":"Local\\FugaTES.SafeDemo.R50."):"Local\\FugaTES.Desktop.") + WindowsIdentity.GetCurrent().User!.Value);
        var acquired = false;
        try
        {
            try { acquired = instance.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { MessageBox.Show("FugaTES já está aberto.", "FugaTES", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if(Transportadoras.SafeDemoReadOnlySessionR50.Enabled)
            {
                using var demo=Transportadoras.SafeDemoReadOnlySessionR50.Create(AppContext.BaseDirectory);
                Application.Run(new PainelInicialForm(demo:demo));return;
            }
            DesktopRuntime.Initialize();
            var carrier=Transportadoras.CarrierDesktopComposition.Create();
            try { Application.Run(new PainelInicialForm(carrier)); }
            finally { carrier.StopAsync().GetAwaiter().GetResult(); }
        }
        finally
        {
            DesktopRuntime.Current?.Dispose();
            if (acquired) instance.ReleaseMutex();
        }
#endif
    }
}
