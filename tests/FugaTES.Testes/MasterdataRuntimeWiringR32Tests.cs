using System.Windows.Forms;
using System.Reflection;
using FugaTES.WinForms;
using FugaTES.WinForms.Transportadoras.MasterdataR3;
namespace FugaTES.Testes;
public class MasterdataRuntimeWiringR32Tests
{
    static void Sta(Action action){Exception? error=null;var thread=new Thread(()=>{try{action();}catch(Exception ex){error=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(5000));if(error is not null)throw error;}
    [Fact] public void No_duplicate_execution_timer(){Assert.DoesNotContain(typeof(FrmCadastrosGuardianR3).GetFields(BindingFlags.Instance|BindingFlags.NonPublic),f=>f.FieldType==typeof(System.Windows.Forms.Timer));}
    [Fact] public void Menu_contains_Guardian_navigation(){Sta(()=>{using var menu=new FrmIntegracoes();Assert.Contains(Controls(menu).OfType<Button>(),b=>b.Text=="Guardian");});}
    [Fact] public void Form_close_does_not_stop_shared_runtime(){Sta(()=>{var shared=new OrquestradorCompartilhadoMasterdataR3();using var form=new FrmCadastrosGuardianR3(shared);form.Show();form.Close();Assert.NotEqual(SituacaoCicloMasterdataR3.Encerrado,shared.ExecutarAsync(OrigemCicloMasterdataR3.Manual).GetAwaiter().GetResult().Situacao);shared.Encerrar();});}
    static async Task Until(Func<bool> condition){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!condition())await Task.Delay(10,timeout.Token);}
    [Fact] public async Task Manual_BP_request_is_queued_once_and_consumed_by_existing_30sec_pipeline()
    {
        var clock=new CarrierTestClock();int cycles=0,requests=0,consumed=0;string? queued=null;
        var lifecycle=new FugaTES.WinForms.Transportadoras.CarrierDesktopLifecycle(_=>Task.FromResult(new FugaTES.WinForms.Transportadoras.CarrierDesktopSession(
            _=>{Interlocked.Increment(ref cycles);if(queued is not null){consumed++;queued=null;}return Task.CompletedTask;},
            (_,bp,_,_)=>{requests++;queued=bp;return Task.FromResult(new FugaTES.HostIntegracao.CarrierManualResponse(true,"CARRIER_REQUEST_ACCEPTED"));})),clock);
        lifecycle.Start();await Until(()=>cycles==1&&lifecycle.Snapshot.NextQueryUtc>clock.GetUtcNow());
        var response=await lifecycle.RequestMasterdataAsync(Guid.NewGuid(),"BP_OTHER",CancellationToken.None);
        Assert.True(response.Accepted);Assert.Equal("BP_OTHER",queued);Assert.Equal(1,requests);Assert.Equal(1,cycles);
        clock.Advance(TimeSpan.FromSeconds(30));await Until(()=>cycles==2);Assert.Equal(1,consumed);
        Assert.All(lifecycle.Masterdata.Estados().Where(s=>s.Categoria!=CategoriaMasterdataR3.Transportadoras),s=>Assert.Equal(EstadoMasterdataR3.Bloqueado,s.Estado));
        await lifecycle.StopAsync();clock.Advance(TimeSpan.FromSeconds(90));await Task.Delay(20);Assert.Equal(2,cycles);
    }
    [Fact] public async Task Shared_lock_refuses_manual_submission_while_automatic_cycle_is_running()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int submissions=0;
        var lifecycle=new FugaTES.WinForms.Transportadoras.CarrierDesktopLifecycle(_=>Task.FromResult(new FugaTES.WinForms.Transportadoras.CarrierDesktopSession(
            async ct=>{entered.SetResult();await Task.Delay(Timeout.Infinite,ct);},
            (_,_,_,_)=>{submissions++;return Task.FromResult(new FugaTES.HostIntegracao.CarrierManualResponse(true,"CARRIER_REQUEST_ACCEPTED"));})),new CarrierTestClock());
        lifecycle.Start();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var response=await lifecycle.RequestMasterdataAsync(Guid.NewGuid(),"BP_OTHER",CancellationToken.None);
        Assert.False(response.Accepted);Assert.Equal("CARRIER_CYCLE_BUSY",response.Code);Assert.Equal(0,submissions);Assert.True(lifecycle.Snapshot.Cycling);
        await lifecycle.StopAsync();Assert.False(lifecycle.Snapshot.Active);
    }
    [Fact] public async Task App_close_waits_for_manual_request_cleanup_before_disposing_session()
    {
        var clock=new CarrierTestClock();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);bool disposed=false;int cycles=0;
        var lifecycle=new FugaTES.WinForms.Transportadoras.CarrierDesktopLifecycle(_=>Task.FromResult(new FugaTES.WinForms.Transportadoras.CarrierDesktopSession(
            _=>{cycles++;return Task.CompletedTask;},async(_,_,_,ct)=>{entered.SetResult();try{await Task.Delay(Timeout.Infinite,ct);}finally{await release.Task;}return new(false,"CARRIER_REQUEST_NOT_READY");},()=>{disposed=true;return ValueTask.CompletedTask;})),clock);
        lifecycle.Start();await Until(()=>cycles==1&&lifecycle.Snapshot.NextQueryUtc>clock.GetUtcNow());
        var request=lifecycle.RequestMasterdataAsync(Guid.NewGuid(),"BP_OTHER",CancellationToken.None);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var closing=lifecycle.StopAsync();Assert.False(closing.IsCompleted);Assert.False(disposed);release.SetResult();await request;await closing.WaitAsync(TimeSpan.FromSeconds(3));Assert.True(disposed);
    }
    [Fact] public async Task HML_unavailable_blocks_manual_and_keeps_UI_button_disabled()
    {
        var lifecycle=new FugaTES.WinForms.Transportadoras.CarrierDesktopLifecycle(_=>throw new FugaTES.WinForms.Runtime.DesktopStartupException("CARRIER_DESKTOP_HML_NOT_PROVEN"),TimeProvider.System);
        lifecycle.Start();await lifecycle.Completion;var response=await lifecycle.RequestMasterdataAsync(Guid.NewGuid(),"BP_OTHER",CancellationToken.None);Assert.False(response.Accepted);
        Sta(()=>{using var form=new FrmCadastrosGuardianR3(lifecycle:lifecycle);Assert.False(Controls(form).OfType<Button>().Single(b=>b.Text=="Sincronizar agora").Enabled);});await lifecycle.StopAsync();
    }
    [Fact] public async Task Persistent_logs_use_existing_session_repository_delegate()
    {
        var clock=new CarrierTestClock();int cycles=0;var record=new FugaTES.Aplicacao.Transportadoras.CarrierStoredCycle(Guid.NewGuid(),clock.GetUtcNow(),clock.GetUtcNow(),2,0,0,2,0,0,"SUCCESS");
        var lifecycle=new FugaTES.WinForms.Transportadoras.CarrierDesktopLifecycle(_=>Task.FromResult(new FugaTES.WinForms.Transportadoras.CarrierDesktopSession(_=>{cycles++;return Task.CompletedTask;},(_,_,_,_)=>Task.FromResult(new FugaTES.HostIntegracao.CarrierManualResponse(false,"CARRIER_REQUEST_NOT_AUTHORIZED")),logs:_=>Task.FromResult<IReadOnlyList<FugaTES.Aplicacao.Transportadoras.CarrierStoredCycle>>([record]))),clock);
        lifecycle.Start();await Until(()=>cycles==1&&lifecycle.Snapshot.NextQueryUtc>clock.GetUtcNow());Assert.Same(record,Assert.Single(await lifecycle.ReadLogsAsync(CancellationToken.None)));await lifecycle.StopAsync();
    }
    [Fact] public void Menu_navigation_opens_form_with_same_shared_orchestrator()
    {
        Sta(()=>{var lifecycle=new FugaTES.WinForms.Transportadoras.CarrierDesktopLifecycle(_=>throw new InvalidOperationException(),TimeProvider.System);using var menu=new FrmIntegracoes(lifecycle);menu.Show();
            bool opened=false;using var timer=new System.Windows.Forms.Timer{Interval=20};timer.Tick+=(_,_)=>{var child=Application.OpenForms.OfType<FrmCadastrosGuardianR3>().FirstOrDefault();if(child is null)return;opened=true;Assert.Same(lifecycle.Masterdata,typeof(FrmCadastrosGuardianR3).GetField("_orquestrador",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(child));child.Close();timer.Stop();};timer.Start();Controls(menu).OfType<Button>().Single(b=>b.Text=="Guardian").PerformClick();Assert.True(opened);menu.Close();lifecycle.StopAsync().GetAwaiter().GetResult();});
    }
    [Fact] public void Manual_button_requires_BP_and_uses_shared_queue_without_running_global_batch()
    {
        Sta(()=>{var clock=new CarrierTestClock();int cycles=0,requests=0;string? requestedBp=null;
            var lifecycle=new FugaTES.WinForms.Transportadoras.CarrierDesktopLifecycle(_=>Task.FromResult(new FugaTES.WinForms.Transportadoras.CarrierDesktopSession(_=>{Interlocked.Increment(ref cycles);return Task.CompletedTask;},(_,bp,_,_)=>{requestedBp=bp;requests++;return Task.FromResult(new FugaTES.HostIntegracao.CarrierManualResponse(true,"CARRIER_REQUEST_ACCEPTED"));})),clock);
            lifecycle.Start();Until(()=>cycles==1&&lifecycle.Snapshot.NextQueryUtc>clock.GetUtcNow()).GetAwaiter().GetResult();
            using var form=new FrmCadastrosGuardianR3(lifecycle:lifecycle);form.Show();var button=Controls(form).OfType<Button>().Single(b=>b.Text=="Sincronizar agora");Assert.False(button.Enabled);
            Controls(form).OfType<TextBox>().Single(t=>t.PlaceholderText=="BP transportadora").Text="BP_UI";Assert.True(button.Enabled);button.PerformClick();Assert.Equal(1,requests);Assert.Equal("BP_UI",requestedBp);Assert.Equal(1,cycles);
            form.Close();Assert.True(lifecycle.Snapshot.Active);lifecycle.StopAsync().GetAwaiter().GetResult();});
    }
    static IEnumerable<Control> Controls(Control c){foreach(Control x in c.Controls){yield return x;foreach(var child in Controls(x))yield return child;}}
}
