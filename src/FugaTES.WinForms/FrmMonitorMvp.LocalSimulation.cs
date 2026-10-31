using System.Drawing.Imaging;
using System.Text.Json;

namespace FugaTES.WinForms;

public partial class FrmMonitorMvp
{
    private string? localEvidenceDirectory;
    private readonly HashSet<Guid> localCaptured=[];
    private bool localCapturePending;
    private static bool CanCaptureLocalObservation(Guid expected,Guid? selected,string? currentState,bool correctTab,bool sendEnabled)
        =>expected==selected && currentState=="TERMINAL_JOURNAL_OBSERVED" && correctTab && !sendEnabled;

    public void EnableLocalDemoPresentation(string directory)
    {
        if(!localSimulation || !Path.IsPathFullyQualified(directory))throw new InvalidOperationException("LOCAL_DEMO_ONLY");
        localEvidenceDirectory=directory;Directory.CreateDirectory(directory);
        StartPosition=FormStartPosition.CenterScreen;
        var area=Screen.FromControl(this).WorkingArea;
        Size=new Size(Math.Min(1500,area.Width-40),Math.Min(1000,area.Height-40));
        TopMost=true;
    }

    // The application's own demo presentation/export, not a business command.
    private async Task PresentLocalObservationAsync()
    {
        if(localEvidenceDirectory is null || localCapturePending || closed.IsCancellationRequested)return;
        localCapturePending=true;
        try
        {
            var next=Integracoes.Rows.Cast<DataGridViewRow>().FirstOrDefault(r=>
                r.DataBoundItem is FugaTES.Aplicacao.Consultas.Monitor.MonitorIntegracao i
                && !localCaptured.Contains(i.IntegracaoId) && i.UltimoResultado=="TERMINAL_JOURNAL_OBSERVED");
            if(next is null)return;
            Integracoes.CurrentCell=next.Cells[0];
            await protocol.GeneratePreviewAsync(closed.Token);RenderProtocol();
            if(protocol.Snapshot is not {LocalSimulationDetails:not null} snapshot || snapshot.IntegrationId!=SelectedId
                || snapshot.State!="TERMINAL_JOURNAL_OBSERVED" || SendProtocol.Enabled)return;
            Detalhes.SelectedTab=ProtocolTab;
            Activate();BringToFront();
            await Task.Delay(200,closed.Token);
            if(IsDisposed||!Visible || protocol.Snapshot?.IntegrationId!=snapshot.IntegrationId
                || !CanCaptureLocalObservation(snapshot.IntegrationId,SelectedId,protocol.Snapshot?.State,
                    Detalhes.SelectedTab==ProtocolTab,SendProtocol.Enabled))return;
            var name=snapshot.Contract;
            using(var bitmap=new Bitmap(Width,Height))
            {
                // Real desktop pixels at the visible form's screen position.
                using var graphics=Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(Location,Point.Empty,Size);
                bitmap.Save(Path.Combine(localEvidenceDirectory,name+"-screen.png"),ImageFormat.Png);
            }
            using(var rendered=new Bitmap(Width,Height))
            {
                DrawToBitmap(rendered,new Rectangle(Point.Empty,Size));
                rendered.Save(Path.Combine(localEvidenceDirectory,name+"-render.png"),ImageFormat.Png);
            }
            File.WriteAllText(Path.Combine(localEvidenceDirectory,name+"-ui.json"),JsonSerializer.Serialize(new
            {
                scope="VISIBLE_LOCAL_SIMULATION_NOT_HML",capturedUtc=DateTimeOffset.UtcNow,Visible,
                title=Text,integrationId=snapshot.IntegrationId,contract=snapshot.Contract,
                protocolId=snapshot.ProtocolId,httpStatus=snapshot.HttpStatus,state=snapshot.State,
                sendEnabled=SendProtocol.Enabled,metadata=ProtocolMetadata.Text,details=ProtocolJson.Text,
                result=ProtocolResult.Text,windowBounds=new{Left,Top,Width,Height}
            },new JsonSerializerOptions{WriteIndented=true}));
            localCaptured.Add(snapshot.IntegrationId);
            if(localCaptured.Count==3)TopMost=false;
        }
        finally{localCapturePending=false;}
    }
}
