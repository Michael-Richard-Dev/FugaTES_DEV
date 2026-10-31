using FugaTES.WinForms.Monitor;

namespace FugaTES.WinForms;

public static class UnifiedDesktopStartup
{
    public static void Run(string[] args)
    {
        using var single=new Mutex(false,"Local\\FugaTES.Unified.DEV110");
        bool acquired;
        try{acquired=single.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
        if(!acquired){MessageBox.Show("FugaTES DEV110 já está aberto.","FugaTES");return;}
        try
        {
            using var session=UnifiedDesktopSession.Load(AppContext.BaseDirectory);
            using var shell=new PainelInicialForm(unified:session);
            if(args.Length==2 && args[0]=="--validate-ui" && Path.IsPathFullyQualified(args[1]))
                shell.Shown+=async(_,_)=>await UnifiedDesktopVisualCheck.RunAsync(shell,session,args[1]);
            Application.Run(shell);
        }
        finally{single.ReleaseMutex();}
    }
}
