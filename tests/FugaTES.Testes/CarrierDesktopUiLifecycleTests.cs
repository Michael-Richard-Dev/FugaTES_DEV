using FugaTES.WinForms.Transportadoras;
using System.Windows.Forms;
namespace FugaTES.Testes;
public class CarrierDesktopUiLifecycleTests
{
    [Fact] public void UI_remains_responsive_and_FormClosing_waits_without_blocking_message_pump()
    {
        Exception? failure=null;int ticks=0;bool disposed=false;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var cleanup=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle=new CarrierDesktopLifecycle(_=>Task.FromResult(new CarrierDesktopSession(async ct=>{entered.TrySetResult();try{await Task.Delay(Timeout.Infinite,ct);}finally{await cleanup.Task;}},(_,_,_,_)=>Task.FromResult(new FugaTES.HostIntegracao.CarrierManualResponse(false,"CARRIER_REQUEST_NOT_AUTHORIZED")),()=>{disposed=true;return ValueTask.CompletedTask;})),TimeProvider.System);
        var thread=new Thread(()=>
        {
            try
            {
                using var form=new FugaTES.WinForms.PainelInicialForm(lifecycle){Opacity=0,ShowInTaskbar=false};using var timer=new System.Windows.Forms.Timer{Interval=25};
                bool closeRequested=false,cleanupReleased=false;var start=DateTime.UtcNow;timer.Tick+=(_,_)=>
                {
                    ticks++;
                    if(DateTime.UtcNow-start>TimeSpan.FromSeconds(8)){cleanup.TrySetResult();throw new TimeoutException("UI_TEST_TIMEOUT");}
                    if(!entered.Task.IsCompleted)return;
                    if(ticks>=3&&!closeRequested){closeRequested=true;form.Close();}
                    if(closeRequested&&ticks>=5&&!cleanupReleased){Assert.False(disposed);cleanupReleased=true;cleanup.TrySetResult();}
                };
                form.Shown+=(_,_)=>timer.Start();Application.Run(form);timer.Stop();Assert.True(disposed);Assert.False(lifecycle.Snapshot.Active);Assert.True(ticks>=3);
            }
            catch(Exception ex){failure=ex;cleanup.TrySetResult();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}