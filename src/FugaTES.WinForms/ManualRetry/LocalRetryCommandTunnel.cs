using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FugaTES.WinForms.ManualRetry;

public sealed record LocalRetryCommandTunnelOptions
{
    public bool Enabled{get;init;}
    public string Host{get;init;}="";
    public string User{get;init;}="";
    public string IdentityFile{get;init;}="";
    public string KnownHostsFile{get;init;}="";
    public int RemoteCommandPort{get;init;}=18763;
}

public sealed record LocalRetryCommandTunnelListener(int ProcessId,string ExecutablePath,string CommandLine);

// Dedicated local ACM command transport. No remote command or SSH configuration change.
public sealed class LocalRetryCommandTunnel(LocalRetryCommandTunnelOptions options,Func<CancellationToken,Task<LocalRetryCommandTunnelListener?>>? listenerProbe=null):IDisposable
{
    readonly SemaphoreSlim gate=new(1,1);
    readonly CancellationTokenSource closed=new();
    Process? verified;
    bool owned;
    volatile bool disposed;
    DateTimeOffset nextStart;
    public static string SshExecutable=>Path.Combine(Environment.SystemDirectory,"OpenSSH","ssh.exe");
    public static string[] BuildArguments(LocalRetryCommandTunnelOptions o)
    {
        if(!o.Enabled || o.Host!="192.168.17.40" || o.User!="michael" || o.RemoteCommandPort!=18763 ||
            !Path.IsPathFullyQualified(o.IdentityFile) || !Path.IsPathFullyQualified(o.KnownHostsFile) ||
            o.IdentityFile.Any(char.IsControl) || o.KnownHostsFile.Any(char.IsControl))
            throw new InvalidOperationException("SSH_BINDING_REQUIRED");
        return ["-N","-T","-a","-x","-F","NUL","-i",o.IdentityFile,
            "-o","PermitLocalCommand=no",
            "-o","BatchMode=yes","-o","IdentitiesOnly=yes","-o","IdentityAgent=none",
            "-o","StrictHostKeyChecking=yes","-o","UpdateHostKeys=no","-o","UserKnownHostsFile="+o.KnownHostsFile,
            "-o","GlobalKnownHostsFile=NUL","-o","ConnectTimeout=10","-o","ControlMaster=no","-o","ControlPath=none",
            "-o","ProxyCommand=none","-o","ProxyJump=none","-o","ExitOnForwardFailure=yes",
            "-o","ServerAliveInterval=30","-o","ServerAliveCountMax=3",
            "-L","127.0.0.1:18763:127.0.0.1:"+o.RemoteCommandPort,o.User+"@"+o.Host];
    }
    public static bool IsApprovedCommand(string executable,IReadOnlyList<string> arguments,LocalRetryCommandTunnelOptions o)
        =>string.Equals(executable,SshExecutable,StringComparison.OrdinalIgnoreCase) &&
            arguments.SequenceEqual(BuildArguments(o),StringComparer.Ordinal);

    // Teardown may synchronously wait for this gate on the UI thread. No awaited operation below may capture it.
    public async Task EnsureAsync(CancellationToken ct)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,closed.Token);
        ct=linked.Token;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var args=BuildArguments(options);
            if(verified is {HasExited:false})
            {
                var active=await ProbeListenerAsync(ct).ConfigureAwait(false);
                if(active?.ProcessId!=verified.Id || !IsApprovedCommand(active.ExecutablePath,SplitCommand(active.CommandLine).Skip(1).ToArray(),options))
                    throw new InvalidOperationException("LOCAL_PORT_UNTRUSTED");
                return;
            }
            ReleaseVerified();
            var peer=await ProbeListenerAsync(ct).ConfigureAwait(false);
            if(peer is not null)
            {
                var command=SplitCommand(peer.CommandLine);
                if(command.Length<2 || !IsApprovedCommand(peer.ExecutablePath,command.Skip(1).ToArray(),options))
                    throw new InvalidOperationException("LOCAL_PORT_UNTRUSTED");
                verified=Process.GetProcessById(peer.ProcessId);
                if(verified.HasExited)throw new InvalidOperationException("SSH_CONNECT_FAILED");
                return; // Reuse verified existing tunnel; it remains externally owned.
            }
            if(DateTimeOffset.UtcNow<nextStart)throw new InvalidOperationException("SSH_CONNECT_FAILED");
            nextStart=DateTimeOffset.UtcNow.AddSeconds(20);
            if(!File.Exists(SshExecutable)||!File.Exists(options.IdentityFile)||!File.Exists(options.KnownHostsFile))
                throw new InvalidOperationException("SSH_PREREQUISITE_MISSING");
            var start=new ProcessStartInfo(SshExecutable){UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var a in args)start.ArgumentList.Add(a);
            verified=Process.Start(start)??throw new InvalidOperationException("SSH_CONNECT_FAILED");owned=true;
            // Discard diagnostics: neither private data nor raw authentication messages reach logs/UI.
            _=verified.StandardError.ReadToEndAsync();_=verified.StandardOutput.ReadToEndAsync();
            var until=DateTimeOffset.UtcNow.AddSeconds(12);
            while(DateTimeOffset.UtcNow<until)
            {
                ct.ThrowIfCancellationRequested();
                if(verified.HasExited)throw new InvalidOperationException("SSH_CONNECT_FAILED");
                using var tcp=new TcpClient();
                try
                {
                    await tcp.ConnectAsync("127.0.0.1",18763,ct).ConfigureAwait(false);
                    var listener=await ProbeListenerAsync(ct).ConfigureAwait(false);
                    if(listener?.ProcessId!=verified.Id || !IsApprovedCommand(listener.ExecutablePath,SplitCommand(listener.CommandLine).Skip(1).ToArray(),options))
                        throw new InvalidOperationException("LOCAL_PORT_UNTRUSTED");
                    return;
                }
                catch(SocketException){await Task.Delay(200,ct).ConfigureAwait(false);}
            }
            throw new InvalidOperationException("SSH_CONNECT_FAILED");
        }
        catch
        {
            ReleaseVerified();throw;
        }
        finally{gate.Release();}
    }

    Task<LocalRetryCommandTunnelListener?> ProbeListenerAsync(CancellationToken ct)=>(listenerProbe??InspectListenerAsync)(ct);
    static async Task<LocalRetryCommandTunnelListener?> InspectListenerAsync(CancellationToken ct)
    {
        // Local metadata only, filtered to the dedicated loopback listener. No credential APIs or files.
        const string script="$ErrorActionPreference='Stop'; $c=@(Get-NetTCPConnection -State Listen -LocalPort 18763 -ErrorAction SilentlyContinue); if($c.Count -eq 0){'null';exit}; if($c.Count -ne 1 -or $c[0].LocalAddress -ne '127.0.0.1'){throw 'UntrustedListener'}; $p=Get-CimInstance Win32_Process -Filter ('ProcessId='+$c[0].OwningProcess); if($null -eq $p){throw 'UnknownListener'}; @{ProcessId=$p.ProcessId;ExecutablePath=$p.ExecutablePath;CommandLine=$p.CommandLine}|ConvertTo-Json -Compress";
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe"))
            {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-Command",script})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new InvalidOperationException("LOCAL_PORT_UNTRUSTED");
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(ct);limit.CancelAfter(TimeSpan.FromSeconds(12));
        var output=process.StandardOutput.ReadToEndAsync(limit.Token);_=process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            if(process.ExitCode!=0)throw new InvalidOperationException("LOCAL_PORT_UNTRUSTED");
            return JsonSerializer.Deserialize<LocalRetryCommandTunnelListener>(await output.ConfigureAwait(false));
        }
        catch{if(!process.HasExited)process.Kill();throw;}
    }
    static string[] SplitCommand(string command)
    {
        if(string.IsNullOrWhiteSpace(command))return [];
        var pointer=CommandLineToArgvW(command,out var count);
        if(pointer==IntPtr.Zero)return [];
        try{return Enumerable.Range(0,count).Select(i=>Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer,i*IntPtr.Size))??"").ToArray();}
        finally{LocalFree(pointer);}
    }
    [DllImport("shell32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string command,out int count);
    [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr memory);
    void ReleaseVerified()
    {
        var process=verified;var terminate=owned;verified=null;owned=false;
        if(process is null)return;
        try
        {
            if(terminate&&!process.HasExited)process.Kill();
        }
        catch(InvalidOperationException){} // The owned process may have exited between inspection and cleanup.
        finally{process.Dispose();}
    }
    public void Dispose()
    {
        closed.Cancel();
        gate.Wait();
        try
        {
            if(disposed)return;disposed=true;
            // Cancel preparation and serialize teardown with it; never terminate a reused external process.
            ReleaseVerified();
        }
        finally{gate.Release();}
    }
}




