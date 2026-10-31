using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.Monitor;
using FugaTES.WinForms;
using FugaTES.WinForms.Monitor;

namespace FugaTES.Testes;

public sealed class UnifiedDesktopTests
{
    [Fact]
    public void Unified_main_session_is_part_of_the_same_desktop_assembly()
        => Assert.NotNull(typeof(PainelInicialForm).Assembly.GetType("FugaTES.WinForms.Monitor.UnifiedDesktopSession"));

    [Fact]
    public void Persisted_protocol_is_available_without_inventing_callback_or_real_Guardian_result()
    {
        var row=new MonitorIntegracao{Fluxo="R165AL GUARDIAN SIMULADO",EstadoMotor="CONCLUIDA",ProtocoloGuardian="986801"};
        var data=new MonitorAcmDados{ContratosDisponiveis=true,Protocolo="986801",Contrato="0000986801",CodigoGuardian="0000986801"};
        var steps=MonitorFluxoAcm.Criar(row,new([],[],[]),data);
        var protocol=steps.Single(x=>x.Etapa=="Protocolo Guardian");
        Assert.Equal("CONCLUIDO",protocol.Estado);Assert.Contains("SIMULADO",protocol.Evidencia);Assert.Contains("986801",protocol.Evidencia);
        Assert.Equal("INDISPONIVEL",steps.Single(x=>x.Etapa=="Callback pesagem").Estado);
    }
    [Fact]
    public void Main_shell_opens_without_starting_the_carrier_or_a_corporate_provider()
    {
        GuardianProtocolFormTests.OnSta(()=>
        {
            var starts=0;
            var lifecycle=new FugaTES.WinForms.Transportadoras.CarrierDesktopLifecycle(_=>
            {Interlocked.Increment(ref starts);throw new InvalidOperationException("NO_CORPORATE_START");},TimeProvider.System);
            using var form=new PainelInicialForm(carrier:lifecycle,unified:new(new(),Path.GetTempPath()));
            form.ShowInTaskbar=false;form.StartPosition=System.Windows.Forms.FormStartPosition.Manual;form.Location=new(-20000,-20000);
            form.Show();System.Windows.Forms.Application.DoEvents();
            Assert.Equal(0,starts);Assert.Contains("DEV110",form.Text);Assert.Contains("UNIFICADO",form.Text);
            var modules=GuardianProtocolFormTests.Descendants(form).OfType<System.Windows.Forms.DataGridView>().Single(x=>x.Name=="UnifiedModules");
            Assert.Equal(6,modules.Rows.Count);form.Close();
        });
    }
    [Fact]
    public void Unified_carrier_view_cannot_open_a_provider_or_enable_business_actions()
    {
        GuardianProtocolFormTests.OnSta(()=>
        {
            using var form=new FugaTES.WinForms.Transportadoras.MasterdataR3.FrmTransportadoraMasterdata(readOnly:true);
            var buttons=GuardianProtocolFormTests.Descendants(form).OfType<System.Windows.Forms.Button>().ToArray();
            Assert.NotEmpty(buttons);Assert.All(buttons,b=>Assert.False(b.Enabled));
        });
    }
    [Fact]
    public void Returning_home_preserves_unified_physical_environment()
    {
        GuardianProtocolFormTests.OnSta(()=>
        {
            using var form=new PainelInicialForm(unified:new(new(),Path.GetTempPath()));
            typeof(PainelInicialForm).GetMethod("BtnInicio_Click",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(form,[form,EventArgs.Empty]);
            var texts=GuardianProtocolFormTests.Descendants(form).Select(c=>c.Text).ToArray();
            Assert.Contains(texts,t=>t.Contains("PostgreSQL físico"));
            Assert.DoesNotContain(texts,t=>t.Contains("DESKTOP_NOT_INITIALIZED"));
        });
    }
    [Fact]
    public void Unified_home_summary_does_not_cover_the_module_table()
    {
        GuardianProtocolFormTests.OnSta(()=>
        {
            using var form=new PainelInicialForm(unified:new(new(),Path.GetTempPath()));
            form.ShowInTaskbar=false;form.StartPosition=System.Windows.Forms.FormStartPosition.Manual;form.Location=new(-20000,-20000);
            form.Show();System.Windows.Forms.Application.DoEvents();
            var controls=GuardianProtocolFormTests.Descendants(form).ToArray();
            var summary=controls.Single(c=>c.Name=="LblResumoConteudo");
            var grid=controls.Single(c=>c.Name=="UnifiedModules");
            Assert.True(summary.Bottom<grid.Top);form.Close();
        });
    }
    [Fact]
    public void Ambiguous_correlation_blocks_protocol_despite_an_id()
    {
        var steps=MonitorFluxoAcm.Criar(new(){Fluxo="R165AL GUARDIAN SIMULADO"},new([],[],[]),new(){Protocolo="986801",CorrelacaoAmbigua=true});
        Assert.Equal("BLOQUEADO",steps.Single(x=>x.Etapa=="Protocolo Guardian").Estado);
    }
}
