using FugaTES.WinForms;
using Xunit;
using FugaTES.WinForms.Monitor;
using FugaTES.Aplicacao.Consultas.Monitor;
using System.Text.Json;

namespace FugaTES.Testes;

public sealed class LocalSimulationReadbackTests
{
    [Fact]
    public void Explicit_local_observation_adapter_exists_without_replacing_production_readback()
    {
        var type=typeof(FrmMonitorMvp).Assembly.GetType("FugaTES.WinForms.Monitor.LocalSimulationReadback");
        Assert.NotNull(type);
    }

    [Theory]
    [InlineData("success",200,99077L,"CONCLUIDA")]
    [InlineData("http400",400,null,"FALHA_DEFINITIVA")]
    [InlineData("unknown",null,null,"RESULTADO_INDETERMINADO")]
    public async Task Observation_matches_export_and_can_never_send(string outcome,int? status,long? protocol,string state)
    {
        var run=Guid.NewGuid();var id=Guid.NewGuid();var path=Path.Combine(Path.GetTempPath(),"r165aj-"+Guid.NewGuid()+".json");
        try
        {
            Write(path,run,new(){Outcome=outcome,Contract="0000986701",IntegrationId=id,CorrelationId="SYNTHETIC-CORRELATION",Fingerprint="ABC",
                Address="http://127.0.0.1:19999",MotorState=state,Event="TERMINAL_JOURNAL_OBSERVED",HttpStatus=status,ProtocolId=protocol,
                H4="MODELLED COMMIT",Worker="CLAIM FENCING 1",Soap="CONFIRMED",Rest=outcome,JournalStage="REAL_OBSERVED_STAGE",
                CreatedUtc=DateTimeOffset.UtcNow,UpdatedUtc=DateTimeOffset.UtcNow});
            var client=new LocalSimulationReadback(path,run);
            var row=Assert.Single((await client.ConsultarAsync(new(),default)).Integracoes);
            Assert.Equal(id,row.IntegracaoId);Assert.Equal(state,row.EstadoMotor);Assert.Equal(protocol?.ToString(),row.ProtocoloGuardian);
            var snapshot=await client.PreviewAsync(id,default);
            Assert.Equal(status,snapshot.HttpStatus);Assert.Equal(protocol,snapshot.ProtocolId);Assert.False(snapshot.CanSend);Assert.False(client.Configured);
            Assert.Contains("SYNTHETIC-CORRELATION",snapshot.LocalSimulationDetails);Assert.Contains("SIMULACAO LOCAL",snapshot.LocalSimulationDetails);
            await Assert.ThrowsAsync<GuardianProtocolException>(()=>client.SendAsync(id,"","",true,default));
            Assert.Empty((await client.ConsultarAsync(new(Documento:"OTHER"),default)).Integracoes);
        }
        finally{File.Delete(path);}
    }
    [Theory]
    [InlineData("stale")][InlineData("foreign-run")][InlineData("real-address")][InlineData("not-simulated")]
    public async Task Rejects_stale_foreign_and_nonlocal_observation(string kind)
    {
        var path=Path.Combine(Path.GetTempPath(),"r165aj-"+Guid.NewGuid()+".json");var run=Guid.NewGuid();
        try
        {
            var frame=new LocalSimulationFrame{Outcome="success",Contract="0000986701",IntegrationId=Guid.NewGuid(),Address=kind=="real-address"?"http://192.168.17.40":"http://127.0.0.1:19999"};
            Write(path,kind=="foreign-run"?Guid.NewGuid():run,frame,kind=="stale"?DateTimeOffset.UtcNow.AddMinutes(-1):null,kind!="not-simulated");
            await Assert.ThrowsAsync<InvalidOperationException>(()=>new LocalSimulationReadback(path,run).ConsultarAsync(new(),default));
        }
        finally{File.Delete(path);}
    }
    private static void Write(string path,Guid run,LocalSimulationFrame frame,DateTimeOffset? time=null,bool simulated=true)
        =>File.WriteAllText(path,JsonSerializer.Serialize(new LocalSimulationDocument{Schema="R165AJ_LOCAL_SIMULATION_V1",RunId=run,Simulated=simulated,
            CapturedUtc=time??DateTimeOffset.UtcNow,Scenarios=[frame]},new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    [Fact]
    public async Task Atomic_export_can_be_replaced_while_monitor_reads()
    {
        var path=Path.Combine(Path.GetTempPath(),"r165aj-"+Guid.NewGuid()+".json");var run=Guid.NewGuid();
        try
        {
            Write(path,run,new(){Outcome="success",Contract="0000986701",IntegrationId=Guid.NewGuid()});
            var exported=File.ReadAllText(path);var reader=new LocalSimulationReadback(path,run);
            await Task.WhenAll(Task.Run(async()=>{for(var i=0;i<1000;i++)await reader.ConsultarAsync(new(),default);}),
                Task.Run(()=>{for(var i=0;i<1000;i++)LocalObservationFile.Publish(path,exported);}));
        }
        finally{File.Delete(path);File.Delete(path+".tmp");}
    }

    [Fact]
    public void Visual_capture_revalidates_selected_identity_after_async_wait()
    {
        var captureType=typeof(FrmMonitorMvp);
        var method=captureType.GetMethod("CanCaptureLocalObservation",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var id=Guid.NewGuid();
        Assert.True((bool)method.Invoke(null,[id,id,"TERMINAL_JOURNAL_OBSERVED",true,false])!);
        Assert.False((bool)method.Invoke(null,[id,Guid.NewGuid(),"TERMINAL_JOURNAL_OBSERVED",true,false])!);
        Assert.False((bool)method.Invoke(null,[id,id,"REST_SIMULATED_REQUEST",true,false])!);
        Assert.False((bool)method.Invoke(null,[id,id,"TERMINAL_JOURNAL_OBSERVED",false,false])!);
    }

    [Fact]
    public void Existing_protocol_tab_auto_reads_simulation_with_manual_send_locked()
    {
        var path=Path.Combine(Path.GetTempPath(),"r165aj-"+Guid.NewGuid()+".json");var run=Guid.NewGuid();var id=Guid.NewGuid();
        try
        {
            Write(path,run,new(){Outcome="success",Contract="0000986701",IntegrationId=id,CorrelationId="SYNTHETIC-LIVE-CORRELATION",
                Address="http://127.0.0.1:19999",H4="MODELLED COMMIT",Worker="CLAIM AUTOMATICO FENCING 1",Soap="SOAP CONFIRMED",Rest="REST 200",
                ProtocolId=99077,HttpStatus=200,MotorState="CONCLUIDA",Event="TERMINAL_JOURNAL_OBSERVED",JournalStage="R165U_CONFIRMED",
                CreatedUtc=DateTimeOffset.UtcNow,UpdatedUtc=DateTimeOffset.UtcNow});
            GuardianProtocolFormTests.OnSta(()=>
            {
                var reader=new LocalSimulationReadback(path,run);
                using var form=new FrmMonitorMvp(new MonitorOperacionalConsulta(reader),reader.Estado,leituraExportada:true,protocolClient:reader);
                form.ShowInTaskbar=false;form.StartPosition=System.Windows.Forms.FormStartPosition.Manual;form.Location=new(-20000,-20000);form.Show();
                var end=DateTime.UtcNow.AddSeconds(4);
                var controls=GuardianProtocolFormTests.Descendants(form).ToArray();
                var text=(System.Windows.Forms.TextBox)controls.Single(x=>x.Name=="ProtocolJson");
                while(!text.Text.Contains("99077")&&DateTime.UtcNow<end){System.Windows.Forms.Application.DoEvents();Thread.Sleep(10);}
                Assert.Contains("99077",text.Text);Assert.Contains(id.ToString(),text.Text);Assert.Contains("SYNTHETIC-LIVE-CORRELATION",text.Text);
                Assert.Contains("SOAP CONFIRMED",text.Text);Assert.Contains("R165U_CONFIRMED",text.Text);
                Assert.False(controls.Single(x=>x.Name=="SendProtocol").Enabled);
                Assert.Contains("SIMULACAO LOCAL",form.Text);
                form.Close();
            });
        }
        finally{File.Delete(path);}
    }
}
