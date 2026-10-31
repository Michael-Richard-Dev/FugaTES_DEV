using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Persistencia.Transportadoras;
using Npgsql;

namespace FugaTES.HostIntegracao;

public sealed class CarrierServiceStartupException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
public sealed record CarrierServiceDatabaseProfile(string Host, int Port, string Database, string Username, string Schema, string CredentialTarget);
public sealed record CarrierServiceSshProfile(bool Enabled, string Host, int Port, string Username, string PrivateKeyPath, string KnownHostsPath, string RemoteDatabaseHost, int RemoteDatabasePort);
public sealed record CarrierServiceProfile(string Environment, bool IntegrationWorkerEnabled, int ReadinessSeconds, string ServiceAccountSid, CarrierServiceDatabaseProfile Database, CarrierServiceSshProfile Ssh)
{
    public static CarrierServiceProfile Load(string path)
    {
        if (!File.Exists(path)) throw new CarrierServiceStartupException("CARRIER_SERVICE_PROFILE_MISSING");
        try
        {
            return JsonSerializer.Deserialize<CarrierServiceProfile>(File.ReadAllText(path),new JsonSerializerOptions {PropertyNameCaseInsensitive=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow}) ?? throw new CarrierServiceStartupException("CARRIER_SERVICE_PROFILE_INVALID");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { throw new CarrierServiceStartupException("CARRIER_SERVICE_PROFILE_INVALID"); }
    }
    public void Validar(string currentAccountSid)
    {
        if (Environment != "HML") throw new CarrierServiceStartupException("CARRIER_SERVICE_ENVIRONMENT_INVALID");
        if (IntegrationWorkerEnabled) throw new CarrierServiceStartupException("CARRIER_INC005_FORBIDDEN");
        if (string.IsNullOrWhiteSpace(ServiceAccountSid) || ServiceAccountSid != currentAccountSid) throw new CarrierServiceStartupException("CARRIER_SERVICE_ACCOUNT_MISMATCH");
        if (Database is null || Database.Host != "127.0.0.1" || Database.Port is < 1 or > 65535) throw new CarrierServiceStartupException("CARRIER_SERVICE_DATABASE_ENDPOINT_INVALID");
        if (Database.Port == 15432) throw new CarrierServiceStartupException("CARRIER_SERVICE_PORT_NOT_DEDICATED");
        if (new[]{Database.Database,Database.Username,Database.CredentialTarget}.Any(string.IsNullOrWhiteSpace) || Database.Schema != "fugates") throw new CarrierServiceStartupException("CARRIER_SERVICE_DATABASE_IDENTITY_MISSING");
        if (ReadinessSeconds is < 1 or > 60 || Ssh is null || !Ssh.Enabled) throw new CarrierServiceStartupException("CARRIER_SERVICE_SSH_REQUIRED");
        if (new[]{Ssh.Host,Ssh.Username,Ssh.PrivateKeyPath,Ssh.KnownHostsPath,Ssh.RemoteDatabaseHost}.Any(string.IsNullOrWhiteSpace) || Ssh.Port is < 1 or > 65535 || Ssh.RemoteDatabasePort is < 1 or > 65535 || !Path.IsPathFullyQualified(Ssh.PrivateKeyPath) || !Path.IsPathFullyQualified(Ssh.KnownHostsPath)) throw new CarrierServiceStartupException("CARRIER_SERVICE_SSH_PROFILE_INVALID");
    }
    public string ConnectionString(string password)
    {
        Validar(ServiceAccountSid);
        if (string.IsNullOrEmpty(password)) throw new CarrierServiceStartupException("CARRIER_SERVICE_CREDENTIAL_MISSING");
        return new NpgsqlConnectionStringBuilder {Host=Database.Host,Port=Database.Port,Database=Database.Database,Username=Database.Username,Password=password,Timeout=3,CommandTimeout=10,IncludeErrorDetail=false,ApplicationName="FugaTES.CarrierSync.HML",Options="-c default_transaction_read_only=off"}.ConnectionString;
    }
}

// The Windows service owns a dedicated tunnel; closing the desktop cannot stop it.
// No DDL or data write is performed by bootstrap. Secret material remains in memory.
public sealed class CarrierServiceRuntimeBootstrap : IDisposable
{
    private Process? ownedTunnel;
    private int disposed;
    public NpgsqlDataSource Source { get; }
    public ICarrierSyncStore Store { get; }
    public bool OwnsTunnel => ownedTunnel is not null;
    private CarrierServiceRuntimeBootstrap(NpgsqlDataSource source,string connectionString)
    {
        Source=source;
        Store=new CarrierSyncPostgreSqlStore(source,connectionString);
    }
    public static async Task<CarrierServiceRuntimeBootstrap> StartAsync(string externalProfilePath,CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new CarrierServiceStartupException("CARRIER_SERVICE_WINDOWS_REQUIRED");
        var profile=CarrierServiceProfile.Load(externalProfilePath);
        profile.Validar(WindowsIdentity.GetCurrent().User?.Value ?? "");
        if (await PortIsListeningAsync(profile.Database.Port,ct)) throw new CarrierServiceStartupException("CARRIER_SERVICE_PORT_IN_USE");
        var secret=ReadCredential(profile.Database.CredentialTarget,profile.Database.Username);
        var connectionString=profile.ConnectionString(secret);
        var runtime=new CarrierServiceRuntimeBootstrap(NpgsqlDataSource.Create(connectionString),connectionString);
        try
        {
            runtime.StartOwnedTunnel(profile);
            var deadline=DateTimeOffset.UtcNow.AddSeconds(profile.ReadinessSeconds);
            while (!await PortIsListeningAsync(profile.Database.Port,ct))
            {
                if (runtime.ownedTunnel!.HasExited) throw new CarrierServiceStartupException("CARRIER_SERVICE_SSH_EXITED");
                if (DateTimeOffset.UtcNow >= deadline) throw new CarrierServiceStartupException("CARRIER_SERVICE_SSH_TIMEOUT");
                await Task.Delay(200,ct);
            }
            if (runtime.ownedTunnel!.HasExited) throw new CarrierServiceStartupException("CARRIER_SERVICE_SSH_EXITED");
            await runtime.ValidateDatabaseAsync(profile,ct);
            if (runtime.ownedTunnel!.HasExited) throw new CarrierServiceStartupException("CARRIER_SERVICE_SSH_EXITED");
            return runtime;
        }
        catch (CarrierServiceStartupException) { runtime.Dispose(); throw; }
        catch (PostgresException ex) { runtime.Dispose(); throw new CarrierServiceStartupException("CARRIER_SERVICE_DATABASE_"+ex.SqlState); }
        catch (OperationCanceledException) {runtime.Dispose();throw;}
        catch { runtime.Dispose(); throw new CarrierServiceStartupException("CARRIER_SERVICE_STARTUP_FAILED"); }
    }
    public static ProcessStartInfo StartInfo(CarrierServiceProfile profile,string executable)
    {
        profile.Validar(profile.ServiceAccountSid);
        var s=profile.Ssh;
        var info=new ProcessStartInfo(executable) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var arg in new[]{"-N","-T","-o","BatchMode=yes","-o","PasswordAuthentication=no","-o","KbdInteractiveAuthentication=no","-o","IdentitiesOnly=yes","-o","StrictHostKeyChecking=yes","-o","ExitOnForwardFailure=yes","-o","ServerAliveInterval=15","-o","ServerAliveCountMax=2","-o","ConnectTimeout=10","-o","UserKnownHostsFile="+s.KnownHostsPath,"-i",s.PrivateKeyPath,"-p",s.Port.ToString(CultureInfo.InvariantCulture),"-L",$"127.0.0.1:{profile.Database.Port}:{s.RemoteDatabaseHost}:{s.RemoteDatabasePort}","-l",s.Username,s.Host}) info.ArgumentList.Add(arg);
        return info;
    }
    private void StartOwnedTunnel(CarrierServiceProfile profile)
    {
        ValidateKeyAndTrust(profile);
        var executable=Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows),"System32","OpenSSH","ssh.exe");
        if (!File.Exists(executable)) throw new CarrierServiceStartupException("CARRIER_SERVICE_OPENSSH_MISSING");
        ownedTunnel=Process.Start(StartInfo(profile,executable)) ?? throw new CarrierServiceStartupException("CARRIER_SERVICE_SSH_START_FAILED");
        ownedTunnel.StandardInput.Close();
        ownedTunnel.ErrorDataReceived+=(_,_)=>{}; ownedTunnel.OutputDataReceived+=(_,_)=>{};
        ownedTunnel.BeginErrorReadLine(); ownedTunnel.BeginOutputReadLine();
    }
    private static void ValidateKeyAndTrust(CarrierServiceProfile profile)
    {
        if (!OperatingSystem.IsWindows()) throw new CarrierServiceStartupException("CARRIER_SERVICE_WINDOWS_REQUIRED");
        var key=Path.GetFullPath(profile.Ssh.PrivateKeyPath);
        var trust=Path.GetFullPath(profile.Ssh.KnownHostsPath);
        var package=Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory))+Path.DirectorySeparatorChar;
        if (key.StartsWith(package,StringComparison.OrdinalIgnoreCase) || trust.StartsWith(package,StringComparison.OrdinalIgnoreCase)) throw new CarrierServiceStartupException("CARRIER_SERVICE_SECRET_OR_TRUST_IN_PACKAGE");
        if (!File.Exists(key) || !File.Exists(trust)) throw new CarrierServiceStartupException("CARRIER_SERVICE_KEY_OR_HOST_TRUST_MISSING");
        var sid=WindowsIdentity.GetCurrent().User!;
        var acl=new FileInfo(key).GetAccessControl();
        if (!sid.Equals(acl.GetOwner(typeof(SecurityIdentifier)))) throw new CarrierServiceStartupException("CARRIER_SERVICE_KEY_ACL_INVALID");
        var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
        var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if (rule.AccessControlType==AccessControlType.Allow && !rule.IdentityReference.Equals(sid) && !rule.IdentityReference.Equals(system) && !rule.IdentityReference.Equals(admins)) throw new CarrierServiceStartupException("CARRIER_SERVICE_KEY_ACL_INVALID");
    }
    private async Task ValidateDatabaseAsync(CarrierServiceProfile profile,CancellationToken ct)
    {
        await using var connection=await Source.OpenConnectionAsync(ct);
        await using var command=new NpgsqlCommand("select current_database(),current_user,current_setting('transaction_read_only'),to_regnamespace(@schema) is not null,to_regclass('fugates.carrier_sync_checkpoint') is not null",connection);
        command.Parameters.AddWithValue("schema",profile.Database.Schema);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetString(0)!=profile.Database.Database || reader.GetString(1)!=profile.Database.Username || reader.GetString(2)!="off" || !reader.GetBoolean(3)) throw new CarrierServiceStartupException("CARRIER_SERVICE_DATABASE_IDENTITY_INVALID");
        if (!reader.GetBoolean(4)) throw new CarrierServiceStartupException("CARRIER_SERVICE_SCHEMA_PENDING_GAIA");
    }
    private static async Task<bool> PortIsListeningAsync(int port,CancellationToken ct)
    {
        using var client=new TcpClient();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(1000);
        try {await client.ConnectAsync("127.0.0.1",port,timeout.Token);return true;}
        catch (SocketException) {return false;}
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) {return false;}
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string ReadCredential(string target,string expectedUser)
    {
        if (!OperatingSystem.IsWindows()) throw new CarrierServiceStartupException("CARRIER_SERVICE_WINDOWS_REQUIRED");
        if (!CredRead(target,1,0,out var pointer)) throw new CarrierServiceStartupException("CARRIER_SERVICE_CREDENTIAL_MISSING");
        try
        {
            var value=Marshal.PtrToStructure<Credential>(pointer);
            if (value.Username!=expectedUser || value.BlobSize==0 || value.BlobSize%2!=0) throw new CarrierServiceStartupException("CARRIER_SERVICE_CREDENTIAL_INVALID");
            return Marshal.PtrToStringUni(value.Blob,checked((int)value.BlobSize/2)) ?? throw new CarrierServiceStartupException("CARRIER_SERVICE_CREDENTIAL_INVALID");
        }
        finally {CredFree(pointer);}
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed,1) != 0) return;
        try { Source.Dispose(); }
        finally
        {
            var process=Interlocked.Exchange(ref ownedTunnel,null);
            if (process is not null)
            {
                try {if (!process.HasExited) {process.Kill();process.WaitForExit(3000);}}
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally {process.Dispose();}
            }
        }
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags,Type;public string Target,Comment;public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;public uint BlobSize;public IntPtr Blob;public uint Persist,AttributeCount;public IntPtr Attributes;public string Alias,Username;
    }
    [DllImport("advapi32.dll",EntryPoint="CredReadW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CredRead(string target,uint type,uint flags,out IntPtr value);
    [DllImport("advapi32.dll")]private static extern void CredFree(IntPtr value);
}
