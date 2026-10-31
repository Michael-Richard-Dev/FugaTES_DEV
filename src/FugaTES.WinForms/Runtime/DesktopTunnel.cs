using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FugaTES.WinForms.Runtime;

public sealed class DesktopTunnel : IDisposable
{
    private Process? ownedProcess;
    public bool OwnsProcess => ownedProcess is not null;
    public bool HasExited => ownedProcess?.HasExited == true;

    public static ProcessStartInfo StartInfo(DesktopConfiguration configuration, string executable)
    {
        configuration.Validar();
        var ssh = configuration.Ssh;
        if (!ssh.Enabled) throw new DesktopStartupException("DESKTOP_SSH_DISABLED");
        var info = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-N", "-T", "-o", "BatchMode=yes", "-o", "PasswordAuthentication=no",
            "-o", "KbdInteractiveAuthentication=no", "-o", "IdentitiesOnly=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=2",
            "-o", "ConnectTimeout=10", "-o", "UserKnownHostsFile=" + ssh.KnownHostsPath,
            "-i", ssh.PrivateKeyPath, "-p", ssh.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-L", $"127.0.0.1:{configuration.Database.Port}:{ssh.RemoteDatabaseHost}:{ssh.RemoteDatabasePort}",
            "-l", ssh.Username, ssh.Host }) info.ArgumentList.Add(argument);
        return info;
    }

    public static DesktopTunnel Start(DesktopConfiguration configuration)
    {
        var key = Path.GetFullPath(configuration.Ssh.PrivateKeyPath);
        if (key.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
            throw new DesktopStartupException("DESKTOP_SSH_KEY_IN_PACKAGE");
        if (!File.Exists(key) || !File.Exists(configuration.Ssh.KnownHostsPath))
            throw new DesktopStartupException("DESKTOP_SSH_KEY_OR_HOST_TRUST_MISSING");
        ValidateKeyAcl(key);
        var executable = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
        if (!File.Exists(executable)) throw new DesktopStartupException("DESKTOP_OPENSSH_MISSING");
        var lease = new DesktopTunnel();
        try
        {
            lease.ownedProcess = Process.Start(StartInfo(configuration, executable))
                ?? throw new DesktopStartupException("DESKTOP_SSH_START_FAILED");
            lease.ownedProcess.StandardInput.Close();
            // Drain without recording remote diagnostics, usernames or sensitive runtime state.
            lease.ownedProcess.ErrorDataReceived += (_, _) => { };
            lease.ownedProcess.OutputDataReceived += (_, _) => { };
            lease.ownedProcess.BeginErrorReadLine();
            lease.ownedProcess.BeginOutputReadLine();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static void ValidateKeyAcl(string key)
    {
        var sid = WindowsIdentity.GetCurrent().User!;
        var acl = new FileInfo(key).GetAccessControl();
        if (!sid.Equals(acl.GetOwner(typeof(SecurityIdentifier))))
            throw new DesktopStartupException("DESKTOP_SSH_KEY_ACL_INVALID");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(sid) &&
                !rule.IdentityReference.Equals(system) && !rule.IdentityReference.Equals(admins))
                throw new DesktopStartupException("DESKTOP_SSH_KEY_ACL_INVALID");
    }

    public void Dispose()
    {
        var process = Interlocked.Exchange(ref ownedProcess, null);
        if (process is null) return;
        try { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } }
        finally { process.Dispose(); }
    }
}