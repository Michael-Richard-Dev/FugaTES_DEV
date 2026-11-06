using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FugaTES.WinForms.Monitor;

public sealed record LocalReadTunnelOptions
{
    public bool Enabled{get;init;}
    public string Host{get;init;}="";
    public string User{get;init;}="";
    public string IdentityFile{get;init;}="";
    public string KnownHostsFile{get;init;}="";
    public int RemoteReadPort{get;init;}=18761;
}

// Dedicated local GET transport. Never sends a remote command or changes SSH configuration.
public sealed class LocalReadTunnel(LocalReadTunnelOptions options):IDisposable
{
    readonly SemaphoreSlim gate=new(1,1);
    Process? verified;
    bool owned,disposed;
    DateTimeOffset nextStart;
    public static string SshExecutable=>Path.Combine(Environment.SystemDirectory,"OpenSSH","ssh.exe");
    public static string[] BuildArguments(LocalReadTunnelOptions o)
    {
        // 18763 e a porta da API privada de leitura do QAS120; 18762 e a do DEV110, e 18761 e a
        // ponta LOCAL do tunel, que entra aqui por ser o padrao historico do perfil.
        //
        // Esta lista e a mesma decisao que LeituraPrivadaIngresso, no receptor, escrita em dois
        // lugares porque os dois lados sao binarios diferentes. Acrescentar um ambiente la sem
        // acrescentar aqui deixa a tela sem conectar, e o sintoma aparece como "API privada
        // indisponivel" - que manda procurar o servidor quando o recusador e o cliente.
        if(!o.Enabled || o.Host!="192.168.17.40" || o.User!="michael" || o.RemoteReadPort is not(18761 or 18762 or 18763) ||
            !Path.IsPathFullyQualified(o.IdentityFile) || !Path.IsPathFullyQualified(o.KnownHostsFile) ||
            o.IdentityFile.Any(char.IsControl) || o.KnownHostsFile.Any(char.IsControl))
            throw new PhysicalConnectionException("SSH_BINDING_REQUIRED");
        return ["-N","-T","-F","NUL","-i",o.IdentityFile,
            "-o","BatchMode=yes","-o","IdentitiesOnly=yes","-o","IdentityAgent=none",
            "-o","StrictHostKeyChecking=yes","-o","UpdateHostKeys=no","-o","UserKnownHostsFile="+o.KnownHostsFile,
            "-o","GlobalKnownHostsFile=NUL","-o","ConnectTimeout=10","-o","ControlMaster=no","-o","ControlPath=none",
            "-o","ProxyCommand=none","-o","ProxyJump=none","-o","ExitOnForwardFailure=yes",
            "-o","ServerAliveInterval=30","-o","ServerAliveCountMax=3",
            "-L","127.0.0.1:18761:127.0.0.1:"+o.RemoteReadPort,o.User+"@"+o.Host];
    }
    public static bool IsApprovedCommand(string executable,IReadOnlyList<string> arguments,LocalReadTunnelOptions o)
        =>string.Equals(executable,SshExecutable,StringComparison.OrdinalIgnoreCase) &&
            arguments.SequenceEqual(BuildArguments(o),StringComparer.OrdinalIgnoreCase);

    public async Task EnsureAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var args=BuildArguments(options);
            if(verified is {HasExited:false})return;
            verified?.Dispose();verified=null;owned=false;
            var peer=await InspectListenerAsync(ct);
            if(peer is not null)
            {
                var command=SplitCommand(peer.CommandLine);
                if(command.Length<2 || !IsApprovedCommand(peer.ExecutablePath,command.Skip(1).ToArray(),options))
                    throw new PhysicalConnectionException("LOCAL_PORT_UNTRUSTED");
                verified=Process.GetProcessById(peer.ProcessId);
                if(verified.HasExited)throw new PhysicalConnectionException("SSH_CONNECT_FAILED");
                return; // Reuse verified existing tunnel; it remains externally owned.
            }
            if(DateTimeOffset.UtcNow<nextStart)throw new PhysicalConnectionException("SSH_CONNECT_FAILED");
            nextStart=DateTimeOffset.UtcNow.AddSeconds(20);
            if(!File.Exists(SshExecutable)||!File.Exists(options.IdentityFile)||!File.Exists(options.KnownHostsFile))
                throw new PhysicalConnectionException("SSH_PREREQUISITE_MISSING");
            var start=new ProcessStartInfo(SshExecutable){UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var a in args)start.ArgumentList.Add(a);
            verified=Process.Start(start)??throw new PhysicalConnectionException("SSH_CONNECT_FAILED");owned=true;
            // Discard diagnostics: neither private data nor raw authentication messages reach logs/UI.
            _=verified.StandardError.ReadToEndAsync();_=verified.StandardOutput.ReadToEndAsync();
            var until=DateTimeOffset.UtcNow.AddSeconds(12);
            while(DateTimeOffset.UtcNow<until)
            {
                ct.ThrowIfCancellationRequested();
                if(verified.HasExited)throw new PhysicalConnectionException("SSH_CONNECT_FAILED");
                using var tcp=new TcpClient();
                try
                {
                    await tcp.ConnectAsync("127.0.0.1",18761,ct);
                    var listener=await InspectListenerAsync(ct);
                    if(listener?.ProcessId!=verified.Id || !IsApprovedCommand(listener.ExecutablePath,SplitCommand(listener.CommandLine).Skip(1).ToArray(),options))
                        throw new PhysicalConnectionException("LOCAL_PORT_UNTRUSTED");
                    return;
                }
                catch(SocketException){await Task.Delay(200,ct);}
            }
            throw new PhysicalConnectionException("SSH_CONNECT_FAILED");
        }
        catch
        {
            if(owned && verified is {HasExited:false})verified.Kill();
            verified?.Dispose();verified=null;owned=false;throw;
        }
        finally{gate.Release();}
    }

    sealed record Listener(int ProcessId,string ExecutablePath,string CommandLine);
    static async Task<Listener?> InspectListenerAsync(CancellationToken ct)
    {
        // Local metadata only, filtered to the dedicated loopback listener. No credential APIs or files.
        const string script="$ErrorActionPreference='Stop'; $c=@(Get-NetTCPConnection -State Listen -LocalPort 18761 -ErrorAction SilentlyContinue); if($c.Count -eq 0){'null';exit}; if($c.Count -ne 1 -or $c[0].LocalAddress -ne '127.0.0.1'){throw 'UntrustedListener'}; $p=Get-CimInstance Win32_Process -Filter ('ProcessId='+$c[0].OwningProcess); if($null -eq $p){throw 'UnknownListener'}; @{ProcessId=$p.ProcessId;ExecutablePath=$p.ExecutablePath;CommandLine=$p.CommandLine}|ConvertTo-Json -Compress";
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe"))
            {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-Command",script})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new PhysicalConnectionException("LOCAL_PORT_UNTRUSTED");
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(ct);limit.CancelAfter(TimeSpan.FromSeconds(12));
        var output=process.StandardOutput.ReadToEndAsync(limit.Token);_=process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(limit.Token);
            if(process.ExitCode!=0)throw new PhysicalConnectionException("LOCAL_PORT_UNTRUSTED");
            return JsonSerializer.Deserialize<Listener>(await output);
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
    public void Dispose()
    {
        disposed=true;
        // Only the process started by this instance can be terminated; reused tunnels are preserved.
        if(owned && verified is {HasExited:false})verified.Kill();
        verified?.Dispose();verified=null;
    }
}
